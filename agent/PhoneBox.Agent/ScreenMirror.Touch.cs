using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

public readonly record struct DisplayBounds(int Left, int Top, int Width, int Height);

public enum ScreenTouchState : byte
{
    Down = 0,
    Move = 1,
    Up = 2,
    Cancel = 3,
}

public readonly record struct ScreenTouchContact(
    byte PointerId,
    ScreenTouchState State,
    ushort XNorm,
    ushort YNorm,
    ushort Pressure);

internal readonly record struct ResolvedTouchContact(
    uint PointerId,
    uint PointerFlags,
    int X,
    int Y,
    uint Pressure);

public sealed record ScreenTouchHttpPayload(ScreenTouchHttpContact[]? Contacts);

public sealed record ScreenTouchHttpContact(int Id, int State, double X, double Y, int? Pressure);

internal static class ScreenTouchProtocol
{
    public const byte OpcodeTouch = 0x30;
    public const int MaxPointers = 10;

    public static bool TryParse(ReadOnlySpan<byte> payload, Span<ScreenTouchContact> dst, out int count)
    {
        count = 0;
        if (payload.Length < 2 || payload[0] != OpcodeTouch) return false;
        count = payload[1];
        if (count == 0) return true;
        if (count > MaxPointers || count > dst.Length || payload.Length < 2 + count * 8) return false;

        for (var i = 0; i < count; i++)
        {
            var o = 2 + i * 8;
            var rawState = payload[o + 1];
            if (rawState > (byte)ScreenTouchState.Cancel) return false;
            var xNorm = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(o + 2, 2));
            var yNorm = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(o + 4, 2));
            var pressure = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(o + 6, 2));
            dst[i] = new ScreenTouchContact(payload[o], (ScreenTouchState)rawState, xNorm, yNorm, pressure);
        }
        return true;
    }

    public static int FromHttpPayload(ScreenTouchHttpPayload? payload, Span<ScreenTouchContact> dst)
    {
        if (payload?.Contacts is null || payload.Contacts.Length == 0) return 0;
        var count = Math.Min(payload.Contacts.Length, Math.Min(MaxPointers, dst.Length));
        var n = 0;
        for (var i = 0; i < count; i++)
        {
            var c = payload.Contacts[i];
            if (c.State < 0 || c.State > (int)ScreenTouchState.Cancel) continue;
            var x = double.IsFinite(c.X) ? Math.Clamp(c.X, 0.0, 1.0) : 0.0;
            var y = double.IsFinite(c.Y) ? Math.Clamp(c.Y, 0.0, 1.0) : 0.0;
            var p = c.Pressure is int rawP ? (ushort)Math.Clamp(rawP, 1, 1024) : (ushort)512;
            dst[n++] = new ScreenTouchContact(
                (byte)Math.Clamp(c.Id, 0, 255),
                (ScreenTouchState)c.State,
                (ushort)Math.Round(x * 65535.0),
                (ushort)Math.Round(y * 65535.0),
                p);
        }
        return n;
    }

    public static (int X, int Y) MapNormalizedToDisplay(ushort xNorm, ushort yNorm, DisplayBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return (bounds.Left, bounds.Top);
        var maxX = bounds.Left + bounds.Width - 1;
        var maxY = bounds.Top + bounds.Height - 1;
        var x = bounds.Left + (int)Math.Round(xNorm * (bounds.Width - 1) / 65535.0);
        var y = bounds.Top + (int)Math.Round(yNorm * (bounds.Height - 1) / 65535.0);
        return (Math.Clamp(x, bounds.Left, maxX), Math.Clamp(y, bounds.Top, maxY));
    }
}

internal sealed class ScreenTouchStateMachine
{
    public const uint PointerFlagInRange = 0x00000002u;
    public const uint PointerFlagInContact = 0x00000004u;
    public const uint PointerFlagCanceled = 0x00008000u;
    public const uint PointerFlagDown = 0x00010000u;
    public const uint PointerFlagUpdate = 0x00020000u;
    public const uint PointerFlagUp = 0x00040000u;

    private readonly Dictionary<uint, (int X, int Y, uint Pressure)> _active = new(ScreenTouchProtocol.MaxPointers);

    public int ActiveCount => _active.Count;

    public int BuildFrame(ReadOnlySpan<ScreenTouchContact> incoming, DisplayBounds bounds, Span<ResolvedTouchContact> output)
    {
        if (incoming.Length == 0) return BuildReleaseAll(output);

        // Keep last contact per pointer ID within a single packet.
        Span<ScreenTouchContact> dedup = stackalloc ScreenTouchContact[ScreenTouchProtocol.MaxPointers];
        var dedupLen = 0;
        for (var i = 0; i < incoming.Length; i++)
        {
            var c = incoming[i];
            var found = -1;
            for (var j = 0; j < dedupLen; j++)
            {
                if (dedup[j].PointerId == c.PointerId) { found = j; break; }
            }
            if (found >= 0) dedup[found] = c;
            else if (dedupLen < dedup.Length) dedup[dedupLen++] = c;
        }

        Span<uint> seenIds = stackalloc uint[ScreenTouchProtocol.MaxPointers];
        var seenCount = 0;
        var outCount = 0;

        for (var i = 0; i < dedupLen && outCount < output.Length; i++)
        {
            var c = dedup[i];
            uint pid = c.PointerId;
            if (seenCount < seenIds.Length) seenIds[seenCount++] = pid;

            var (x, y) = ScreenTouchProtocol.MapNormalizedToDisplay(c.XNorm, c.YNorm, bounds);
            var pressure = c.Pressure == 0 ? 512u : Math.Clamp((uint)c.Pressure, 1u, 1024u);

            switch (c.State)
            {
                case ScreenTouchState.Down:
                {
                    var isExisting = _active.ContainsKey(pid);
                    var flags = (isExisting ? PointerFlagUpdate : PointerFlagDown) | PointerFlagInRange | PointerFlagInContact;
                    _active[pid] = (x, y, pressure);
                    output[outCount++] = new ResolvedTouchContact(pid, flags, x, y, pressure);
                    break;
                }
                case ScreenTouchState.Move:
                {
                    var isExisting = _active.ContainsKey(pid);
                    var flags = (isExisting ? PointerFlagUpdate : PointerFlagDown) | PointerFlagInRange | PointerFlagInContact;
                    _active[pid] = (x, y, pressure);
                    output[outCount++] = new ResolvedTouchContact(pid, flags, x, y, pressure);
                    break;
                }
                case ScreenTouchState.Up:
                case ScreenTouchState.Cancel:
                {
                    if (!_active.Remove(pid, out var previous)) break;
                    var flags = PointerFlagUp | (c.State == ScreenTouchState.Cancel ? PointerFlagCanceled : 0u);
                    // InjectTouchInput requires UP to reuse the last injected contact position.
                    output[outCount++] = new ResolvedTouchContact(pid, flags, previous.X, previous.Y, 0);
                    break;
                }
            }
        }

        // Preserve any active contacts omitted from this update so InjectTouchInput never auto-lifts them.
        foreach (var kv in _active)
        {
            if (outCount >= output.Length) break;
            var alreadySeen = false;
            for (var i = 0; i < seenCount; i++)
            {
                if (seenIds[i] == kv.Key) { alreadySeen = true; break; }
            }
            if (alreadySeen) continue;
            output[outCount++] = new ResolvedTouchContact(
                kv.Key,
                PointerFlagUpdate | PointerFlagInRange | PointerFlagInContact,
                kv.Value.X,
                kv.Value.Y,
                kv.Value.Pressure);
        }

        return outCount;
    }

    public int BuildReleaseAll(Span<ResolvedTouchContact> output)
    {
        if (_active.Count == 0) return 0;
        var outCount = 0;
        foreach (var kv in _active)
        {
            if (outCount >= output.Length) break;
            output[outCount++] = new ResolvedTouchContact(
                kv.Key,
                PointerFlagUp | PointerFlagCanceled,
                kv.Value.X,
                kv.Value.Y,
                0);
        }
        _active.Clear();
        return outCount;
    }

    public void Reset() => _active.Clear();
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32Point
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32PointerInfo
{
    public uint PointerType;
    public uint PointerId;
    public uint FrameId;
    public uint PointerFlags;
    public nint SourceDevice;
    public nint HwndTarget;
    public Win32Point PtPixelLocation;
    public Win32Point PtHimetricLocation;
    public Win32Point PtPixelLocationRaw;
    public Win32Point PtHimetricLocationRaw;
    public uint DwTime;
    public uint HistoryCount;
    public int InputData;
    public uint DwKeyStates;
    public ulong PerformanceCount;
    public uint ButtonChangeType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32PointerTouchInfo
{
    public Win32PointerInfo PointerInfo;
    public uint TouchFlags;
    public uint TouchMask;
    public Win32Rect RcContact;
    public Win32Rect RcContactRaw;
    public uint Orientation;
    public uint Pressure;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32MouseInputData
{
    public int Dx;
    public int Dy;
    public uint MouseData;
    public uint DwFlags;
    public uint Time;
    public nint DwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32MouseInput
{
    public uint Type;
    public Win32MouseInputData Mi;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32KeybdInputData
{
    public ushort WVk;
    public ushort WScan;
    public uint DwFlags;
    public uint Time;
    public nint DwExtraInfo;
}

[StructLayout(LayoutKind.Explicit)]
internal struct Win32InputUnion
{
    [FieldOffset(0)] public Win32MouseInputData Mi;
    [FieldOffset(0)] public Win32KeybdInputData Ki;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Win32Input
{
    public uint Type;
    public Win32InputUnion U;
}

internal enum PointerFallbackKind
{
    Move,
    LeftDown,
    LeftUp,
    LeftClick,
    LeftDoubleClick,
    RightClick,
    MiddleClick,
    Wheel,
    HWheel,
    ZoomBegin,
    ZoomStep,
    ZoomEnd,
    BrowserBack,
    BrowserForward,
    TaskView,
    ShowDesktop,
}

internal readonly record struct PointerFallbackOp(
    PointerFallbackKind Kind,
    int X,
    int Y,
    int Delta = 0,
    bool HasPosition = true);

internal sealed class ScreenPointerFallback(Func<long>? clockMs = null)
{
    internal const int MoveStartSlopPx = 16;
    internal const int DragStartSlopPx = 12;
    internal const int HoverDeadzonePx = 2;
    internal const int TapMaxDurationMs = 350;
    internal const int DoubleTapMaxGapMs = 400;
    internal const int DoubleTapMaxRadiusPx = 96;
    internal const int PinchStartThresholdPx = 24;
    internal const int PinchStepPx = 36;
    internal const int ScrollStartThresholdPx = 20;
    internal const double ScrollGain = 3.0;
    internal const int ScrollMinEmitDelta = 15;
    internal const int TwoFingerTapSlopPx = 28;
    internal const int RightClickTapMaxMs = 600;
    internal const int ThreeFingerSwipeThresholdPx = 75;
    internal const int ThreeFingerTapMaxMs = 450;

    private enum SingleMode
    {
        None,
        TapPending,
        Hovering,
        SecondTapHeld,
    }

    private enum MultiMode
    {
        None,
        TwoFingerPending,
        TwoFingerScroll,
        TwoFingerPinch,
        TwoFingerEnding,
        AnchorHeld,
        ThreeFingerPending,
        ThreeFingerEnding,
        WaitAllUp,
    }

    private enum ScrollAxis
    {
        None,
        Vertical,
        Horizontal,
        Free,
    }

    private readonly Func<long> _clockMs = clockMs ?? (() => Environment.TickCount64);

    private SingleMode _singleMode;
    private MultiMode _multiMode;
    private ScrollAxis _scrollAxis;

    private uint? _primaryId;
    private int _downX;
    private int _downY;
    private long _downMs;
    private int _lastEmittedX;
    private int _lastEmittedY;
    private bool _leftDown;

    private bool _hasLastTap;
    private bool _secondTapMoved;
    private int _lastTapX;
    private int _lastTapY;
    private long _lastTapUpMs;
    private int _doubleTapAnchorX;
    private int _doubleTapAnchorY;

    private uint _anchorId;
    private uint _secondId;
    private int _anchorStartX;
    private int _anchorStartY;
    private int _secondStartX;
    private int _secondStartY;
    private int _secondLastX;
    private int _secondLastY;
    private long _secondDownMs;
    private bool _suppressRightClick;
    private bool _twoRightClickFired;
    private double _twoStartCentroidX;
    private double _twoStartCentroidY;
    private double _twoLastCentroidX;
    private double _twoLastCentroidY;
    private double _twoStartDist;
    private double _twoLastDist;
    private double _twoScrollAccumX;
    private double _twoScrollAccumY;
    private int _scrollAnchorX;
    private int _scrollAnchorY;

    private bool _pinchActive;
    private double _pinchAccum;
    private int _pinchAnchorX;
    private int _pinchAnchorY;

    private double _threeStartCentroidX;
    private double _threeStartCentroidY;
    private long _threeDownMs;
    private bool _threeFired;

    public static void EmitDoubleTap(int x, int y, Action<PointerFallbackOp> emit) =>
        emit(new PointerFallbackOp(PointerFallbackKind.LeftDoubleClick, x, y));

    internal const int ClickDeferMs = 250;
    private bool _pendingClick;
    private long _pendingMs;
    private bool _secondCancelledClick;

    private void FlushPendingClick(Action<PointerFallbackOp> emit)
    {
        if (!_pendingClick) return;
        _pendingClick = false;
        emit(new PointerFallbackOp(PointerFallbackKind.LeftClick, _lastTapX, _lastTapY));
    }

    /// <summary>Called on a timer: sends the held-back click once no 2nd touch came.</summary>
    public void Tick(Action<PointerFallbackOp> emit)
    {
        if (_pendingClick && _clockMs() - _pendingMs >= ClickDeferMs) FlushPendingClick(emit);
    }

    public void ProcessBatch(ReadOnlySpan<ResolvedTouchContact> batch, Action<PointerFallbackOp> emit)
    {
        if (batch.Length == 0) return;

        var now = _clockMs();
        var activeCount = 0;
        var sumX = 0.0;
        var sumY = 0.0;
        var anyCanceled = false;
        ResolvedTouchContact active0 = default;
        ResolvedTouchContact active1 = default;

        for (var i = 0; i < batch.Length; i++)
        {
            var r = batch[i];
            var f = r.PointerFlags;
            if ((f & ScreenTouchStateMachine.PointerFlagCanceled) != 0)
                anyCanceled = true;
            if ((f & ScreenTouchStateMachine.PointerFlagUp) == 0)
            {
                if (activeCount == 0) active0 = r;
                else if (activeCount == 1) active1 = r;
                activeCount++;
                sumX += r.X;
                sumY += r.Y;
            }
        }

        if (activeCount >= 3)
        {
            FlushPendingClick(emit);
            if (_leftDown)
            {
                _leftDown = false;
                emit(new PointerFallbackOp(PointerFallbackKind.LeftUp, 0, 0, HasPosition: false));
            }
            if (_pinchActive)
            {
                _pinchActive = false;
                emit(new PointerFallbackOp(PointerFallbackKind.ZoomEnd, 0, 0, HasPosition: false));
            }
            _singleMode = SingleMode.None;
            _primaryId = null;
            _hasLastTap = false;

            var cx = sumX / activeCount;
            var cy = sumY / activeCount;
            if (_multiMode is not MultiMode.ThreeFingerPending and not MultiMode.WaitAllUp)
            {
                _multiMode = MultiMode.ThreeFingerPending;
                _threeStartCentroidX = cx;
                _threeStartCentroidY = cy;
                _threeDownMs = now;
                _threeFired = false;
            }
            if (_multiMode == MultiMode.ThreeFingerPending && !_threeFired)
            {
                var dx = cx - _threeStartCentroidX;
                var dy = cy - _threeStartCentroidY;
                if (Hypot(dx, dy) >= ThreeFingerSwipeThresholdPx)
                {
                    _threeFired = true;
                    _multiMode = MultiMode.WaitAllUp;
                    var kind = Math.Abs(dx) >= Math.Abs(dy)
                        ? (dx < 0 ? PointerFallbackKind.BrowserBack : PointerFallbackKind.BrowserForward)
                        : (dy < 0 ? PointerFallbackKind.TaskView : PointerFallbackKind.ShowDesktop);
                    emit(new PointerFallbackOp(kind, 0, 0, HasPosition: false));
                }
            }
            return;
        }

        if (_multiMode is MultiMode.ThreeFingerPending or MultiMode.ThreeFingerEnding)
        {
            if (anyCanceled)
            {
                _multiMode = activeCount == 0 ? MultiMode.None : MultiMode.WaitAllUp;
                return;
            }
            if (activeCount > 0)
            {
                _multiMode = MultiMode.ThreeFingerEnding;
                return;
            }
            if (!_threeFired && (now - _threeDownMs) <= ThreeFingerTapMaxMs)
            {
                emit(new PointerFallbackOp(
                    PointerFallbackKind.MiddleClick,
                    (int)Math.Round(_threeStartCentroidX),
                    (int)Math.Round(_threeStartCentroidY)));
            }
            _multiMode = MultiMode.None;
            return;
        }

        if (_multiMode == MultiMode.WaitAllUp)
        {
            if (activeCount == 0) _multiMode = MultiMode.None;
            return;
        }

        if (activeCount == 2)
        {
            FlushPendingClick(emit);
            var wasDragging = _leftDown;
            if (_leftDown)
            {
                _leftDown = false;
                emit(new PointerFallbackOp(PointerFallbackKind.LeftUp, 0, 0, HasPosition: false));
            }
            _singleMode = SingleMode.None;
            _hasLastTap = false;

            if (_multiMode is MultiMode.None or MultiMode.AnchorHeld or MultiMode.TwoFingerEnding)
            {
                ResolvedTouchContact anchor;
                ResolvedTouchContact second;
                if (_multiMode == MultiMode.AnchorHeld && (active0.PointerId == _anchorId || active1.PointerId == _anchorId))
                {
                    if (active0.PointerId == _anchorId) { anchor = active0; second = active1; }
                    else { anchor = active1; second = active0; }
                }
                else if (_primaryId is uint pid && (active0.PointerId == pid || active1.PointerId == pid))
                {
                    if (active0.PointerId == pid) { anchor = active0; second = active1; }
                    else { anchor = active1; second = active0; }
                }
                else
                {
                    var a0Down = (active0.PointerFlags & ScreenTouchStateMachine.PointerFlagDown) != 0;
                    var a1Down = (active1.PointerFlags & ScreenTouchStateMachine.PointerFlagDown) != 0;
                    if (a1Down && !a0Down) { anchor = active0; second = active1; }
                    else if (a0Down && !a1Down) { anchor = active1; second = active0; }
                    else { anchor = active0; second = active1; }
                }

                _anchorId = anchor.PointerId;
                _secondId = second.PointerId;
                _anchorStartX = anchor.X;
                _anchorStartY = anchor.Y;
                _secondStartX = second.X;
                _secondStartY = second.Y;
                _secondLastX = second.X;
                _secondLastY = second.Y;
                _secondDownMs = now;
                _suppressRightClick = wasDragging;
                _twoStartCentroidX = (active0.X + active1.X) / 2.0;
                _twoStartCentroidY = (active0.Y + active1.Y) / 2.0;
                _twoLastCentroidX = _twoStartCentroidX;
                _twoLastCentroidY = _twoStartCentroidY;
                _twoStartDist = Hypot(active1.X - active0.X, active1.Y - active0.Y);
                _twoLastDist = _twoStartDist;
                _twoScrollAccumX = 0;
                _twoScrollAccumY = 0;
                _scrollAxis = ScrollAxis.None;
                _pinchAccum = 0;
                _twoRightClickFired = false;
                _primaryId = null;
                _multiMode = MultiMode.TwoFingerPending;
                return;
            }

            if (active0.PointerId == _secondId) { _secondLastX = active0.X; _secondLastY = active0.Y; }
            else if (active1.PointerId == _secondId) { _secondLastX = active1.X; _secondLastY = active1.Y; }

            var cx = (active0.X + active1.X) / 2.0;
            var cy = (active0.Y + active1.Y) / 2.0;
            var dist = Hypot(active1.X - active0.X, active1.Y - active0.Y);
            var centroidDisp = Hypot(cx - _twoStartCentroidX, cy - _twoStartCentroidY);
            var distDelta = dist - _twoStartDist;

            if (_multiMode == MultiMode.TwoFingerPending)
            {
                var absDd = Math.Abs(distDelta);
                // pinch = fingers clearly spread/close more than their middle travels; scroll = the middle clearly
                // travels more than the spread changes; in between, keep watching until one wins
                var pinchWins = absDd >= PinchStartThresholdPx && absDd > centroidDisp * 1.4;
                var scrollWins = centroidDisp >= ScrollStartThresholdPx && centroidDisp > absDd * 1.2;
                if (!pinchWins && !scrollWins && Math.Max(absDd, centroidDisp) >= 2 * ScrollStartThresholdPx)
                {
                    pinchWins = absDd > centroidDisp;
                    scrollWins = !pinchWins;
                }
                if (pinchWins)
                {
                    _multiMode = MultiMode.TwoFingerPinch;
                    _pinchActive = true;
                    _pinchAnchorX = (int)Math.Round(_twoStartCentroidX);
                    _pinchAnchorY = (int)Math.Round(_twoStartCentroidY);
                    emit(new PointerFallbackOp(PointerFallbackKind.ZoomBegin, _pinchAnchorX, _pinchAnchorY));
                    _pinchAccum = distDelta - Math.Sign(distDelta) * (PinchStartThresholdPx * 0.5);
                    EmitPinchSteps(emit);
                }
                else if (scrollWins)
                {
                    _multiMode = MultiMode.TwoFingerScroll;
                    _scrollAnchorX = (int)Math.Round(_twoStartCentroidX);
                    _scrollAnchorY = (int)Math.Round(_twoStartCentroidY);
                    var totalDx = cx - _twoStartCentroidX;
                    var totalDy = cy - _twoStartCentroidY;
                    var absDx = Math.Abs(totalDx);
                    var absDy = Math.Abs(totalDy);
                    _scrollAxis = absDy >= absDx * 1.25
                        ? ScrollAxis.Vertical
                        : absDx >= absDy * 1.25
                            ? ScrollAxis.Horizontal
                            : ScrollAxis.Free;
                    EmitTwoFingerScroll(totalDx, totalDy, emit);
                }
            }
            else if (_multiMode == MultiMode.TwoFingerPinch)
            {
                _pinchAccum += (dist - _twoLastDist);
                EmitPinchSteps(emit);
            }
            else if (_multiMode == MultiMode.TwoFingerScroll)
            {
                var dx = cx - _twoLastCentroidX;
                var dy = cy - _twoLastCentroidY;
                if (_scrollAxis == ScrollAxis.Vertical && Math.Abs(dx) > Math.Abs(dy) * 2.5 && Math.Abs(dx) > 10)
                {
                    _scrollAxis = ScrollAxis.Horizontal;
                    _twoScrollAccumY = 0;
                }
                else if (_scrollAxis == ScrollAxis.Horizontal && Math.Abs(dy) > Math.Abs(dx) * 2.5 && Math.Abs(dy) > 10)
                {
                    _scrollAxis = ScrollAxis.Vertical;
                    _twoScrollAccumX = 0;
                }
                EmitTwoFingerScroll(dx, dy, emit);
            }

            _twoLastCentroidX = cx;
            _twoLastCentroidY = cy;
            _twoLastDist = dist;
            return;
        }

        if (_multiMode is MultiMode.TwoFingerPending or MultiMode.TwoFingerScroll or MultiMode.TwoFingerPinch or MultiMode.TwoFingerEnding or MultiMode.AnchorHeld)
        {
            var secondUp = false;
            for (var i = 0; i < batch.Length; i++)
            {
                var r = batch[i];
                if (r.PointerId == _secondId)
                {
                    _secondLastX = r.X;
                    _secondLastY = r.Y;
                    if ((r.PointerFlags & ScreenTouchStateMachine.PointerFlagUp) != 0)
                        secondUp = true;
                }
            }

            if (_pinchActive)
            {
                _pinchActive = false;
                emit(new PointerFallbackOp(PointerFallbackKind.ZoomEnd, 0, 0, HasPosition: false));
                _multiMode = activeCount == 0 ? MultiMode.None : MultiMode.WaitAllUp;
                return;
            }
            if (_multiMode == MultiMode.TwoFingerScroll)
            {
                _multiMode = activeCount == 0 ? MultiMode.None : MultiMode.WaitAllUp;
                return;
            }
            if (_multiMode == MultiMode.AnchorHeld)
            {
                if (activeCount == 0) _multiMode = MultiMode.None;
                return;
            }
            if (_multiMode == MultiMode.TwoFingerPending)
            {
                if (anyCanceled)
                {
                    _multiMode = activeCount == 0 ? MultiMode.None : MultiMode.WaitAllUp;
                    return;
                }
                if (secondUp)
                {
                    var secondDisp = Hypot(_secondLastX - _secondStartX, _secondLastY - _secondStartY);
                    if (!_twoRightClickFired && !_suppressRightClick && secondDisp < TwoFingerTapSlopPx && (now - _secondDownMs) <= RightClickTapMaxMs)
                    {
                        emit(new PointerFallbackOp(PointerFallbackKind.RightClick, _secondStartX, _secondStartY));
                        _twoRightClickFired = true;
                    }
                    _multiMode = activeCount == 0 ? MultiMode.None : MultiMode.AnchorHeld;
                    return;
                }
                _multiMode = activeCount == 0 ? MultiMode.None : MultiMode.TwoFingerEnding;
                return;
            }

            // TwoFingerEnding
            if (activeCount == 0)
            {
                var secondDisp = Hypot(_secondLastX - _secondStartX, _secondLastY - _secondStartY);
                if (!anyCanceled && !_twoRightClickFired && !_suppressRightClick && secondDisp < TwoFingerTapSlopPx && (now - _secondDownMs) <= RightClickTapMaxMs)
                {
                    emit(new PointerFallbackOp(PointerFallbackKind.RightClick, _secondStartX, _secondStartY));
                }
                _multiMode = MultiMode.None;
            }
            return;
        }

        for (var i = 0; i < batch.Length; i++)
        {
            var r = batch[i];
            if (_primaryId is null && (r.PointerFlags & ScreenTouchStateMachine.PointerFlagDown) != 0)
                _primaryId = r.PointerId;
            if (_primaryId != r.PointerId) continue;

            if ((r.PointerFlags & ScreenTouchStateMachine.PointerFlagDown) != 0)
            {
                _downX = r.X;
                _downY = r.Y;
                _downMs = now;

                if (_hasLastTap
                    && (now - _lastTapUpMs) <= DoubleTapMaxGapMs
                    && DistSq(r.X, r.Y, _lastTapX, _lastTapY) <= (long)DoubleTapMaxRadiusPx * DoubleTapMaxRadiusPx)
                {
                    _singleMode = SingleMode.SecondTapHeld;
                    _doubleTapAnchorX = _lastTapX;
                    _doubleTapAnchorY = _lastTapY;
                    _secondTapMoved = false;
                    _secondCancelledClick = _pendingClick;
                    _pendingClick = false;
                    _hasLastTap = false;
                    _leftDown = true;
                    _lastEmittedX = _doubleTapAnchorX;
                    _lastEmittedY = _doubleTapAnchorY;
                    emit(new PointerFallbackOp(PointerFallbackKind.Move, _doubleTapAnchorX, _doubleTapAnchorY));
                    emit(new PointerFallbackOp(PointerFallbackKind.LeftDown, _doubleTapAnchorX, _doubleTapAnchorY));
                }
                else
                {
                    FlushPendingClick(emit);
                    _singleMode = SingleMode.TapPending;
                    _hasLastTap = false;
                    _lastEmittedX = r.X;
                    _lastEmittedY = r.Y;
                }
            }
            else if ((r.PointerFlags & ScreenTouchStateMachine.PointerFlagUp) != 0)
            {
                var canceled = (r.PointerFlags & ScreenTouchStateMachine.PointerFlagCanceled) != 0;
                if (canceled)
                {
                    if (_leftDown)
                    {
                        _leftDown = false;
                        emit(new PointerFallbackOp(PointerFallbackKind.LeftUp, r.X, r.Y, HasPosition: false));
                    }
                    _hasLastTap = false;
                    _secondTapMoved = false;
                }
                else if (_singleMode == SingleMode.TapPending)
                {
                    if ((now - _downMs) <= TapMaxDurationMs)
                    {
                        // the click waits a moment: a 2nd touch that follows and is held then counts as only the hold
                        _pendingClick = true;
                        _pendingMs = now;
                        _hasLastTap = true;
                        _lastTapX = _downX;
                        _lastTapY = _downY;
                        _lastTapUpMs = now;
                    }
                    else
                    {
                        _hasLastTap = false;
                    }
                }
                else if (_singleMode == SingleMode.SecondTapHeld)
                {
                    if (_secondTapMoved)
                    {
                        if (r.X != _lastEmittedX || r.Y != _lastEmittedY)
                        {
                            emit(new PointerFallbackOp(PointerFallbackKind.Move, r.X, r.Y));
                            _lastEmittedX = r.X;
                            _lastEmittedY = r.Y;
                        }
                        emit(new PointerFallbackOp(PointerFallbackKind.LeftUp, r.X, r.Y));
                    }
                    else
                    {
                        emit(new PointerFallbackOp(PointerFallbackKind.LeftUp, _doubleTapAnchorX, _doubleTapAnchorY));
                        // a quick 2nd tap whose first click was held back: the 2nd click that makes the double-click
                        if (_secondCancelledClick && (now - _downMs) <= TapMaxDurationMs)
                            emit(new PointerFallbackOp(PointerFallbackKind.LeftClick, _doubleTapAnchorX, _doubleTapAnchorY));
                    }
                    _secondCancelledClick = false;
                    _leftDown = false;
                    _secondTapMoved = false;
                    _hasLastTap = false;
                }
                _singleMode = SingleMode.None;
                _primaryId = null;
            }
            else if ((r.PointerFlags & ScreenTouchStateMachine.PointerFlagUpdate) != 0)
            {
                if (_singleMode == SingleMode.TapPending)
                {
                    if (DistSq(r.X, r.Y, _downX, _downY) >= (long)MoveStartSlopPx * MoveStartSlopPx)
                    {
                        _singleMode = SingleMode.Hovering;
                        _hasLastTap = false;
                        emit(new PointerFallbackOp(PointerFallbackKind.Move, r.X, r.Y));
                        _lastEmittedX = r.X;
                        _lastEmittedY = r.Y;
                    }
                }
                else if (_singleMode == SingleMode.Hovering)
                {
                    if (DistSq(r.X, r.Y, _lastEmittedX, _lastEmittedY) >= (long)HoverDeadzonePx * HoverDeadzonePx)
                    {
                        emit(new PointerFallbackOp(PointerFallbackKind.Move, r.X, r.Y));
                        _lastEmittedX = r.X;
                        _lastEmittedY = r.Y;
                    }
                }
                else if (_singleMode == SingleMode.SecondTapHeld)
                {
                    if (!_secondTapMoved)
                    {
                        if (DistSq(r.X, r.Y, _downX, _downY) >= (long)DragStartSlopPx * DragStartSlopPx)
                        {
                            _secondTapMoved = true;
                            emit(new PointerFallbackOp(PointerFallbackKind.Move, r.X, r.Y));
                            _lastEmittedX = r.X;
                            _lastEmittedY = r.Y;
                        }
                    }
                    else if (DistSq(r.X, r.Y, _lastEmittedX, _lastEmittedY) >= (long)HoverDeadzonePx * HoverDeadzonePx)
                    {
                        emit(new PointerFallbackOp(PointerFallbackKind.Move, r.X, r.Y));
                        _lastEmittedX = r.X;
                        _lastEmittedY = r.Y;
                    }
                }
            }
        }

        if (activeCount == 0)
        {
            _primaryId = null;
            _singleMode = SingleMode.None;
        }
    }

    public void ReleaseAll(Action<PointerFallbackOp> emit)
    {
        FlushPendingClick(emit);
        if (_leftDown)
        {
            _leftDown = false;
            emit(new PointerFallbackOp(PointerFallbackKind.LeftUp, 0, 0, HasPosition: false));
        }
        if (_pinchActive)
        {
            _pinchActive = false;
            emit(new PointerFallbackOp(PointerFallbackKind.ZoomEnd, 0, 0, HasPosition: false));
        }
        _singleMode = SingleMode.None;
        _multiMode = MultiMode.None;
        _scrollAxis = ScrollAxis.None;
        _primaryId = null;
        _hasLastTap = false;
        _suppressRightClick = false;
        _twoRightClickFired = false;
        _threeFired = false;
        _twoScrollAccumX = 0;
        _twoScrollAccumY = 0;
        _pinchAccum = 0;
    }

    private void EmitPinchSteps(Action<PointerFallbackOp> emit)
    {
        while (Math.Abs(_pinchAccum) >= PinchStepPx)
        {
            var dir = _pinchAccum > 0 ? 1 : -1;
            _pinchAccum -= dir * PinchStepPx;
            emit(new PointerFallbackOp(PointerFallbackKind.ZoomStep, _pinchAnchorX, _pinchAnchorY, Delta: dir * 120));
        }
    }

    private void EmitTwoFingerScroll(double dx, double dy, Action<PointerFallbackOp> emit)
    {
        if (_scrollAxis != ScrollAxis.Horizontal) _twoScrollAccumY += dy;
        if (_scrollAxis != ScrollAxis.Vertical) _twoScrollAccumX += dx;

        var vSteps = (int)Math.Truncate(_twoScrollAccumY * ScrollGain);
        var hSteps = (int)Math.Truncate(-_twoScrollAccumX * ScrollGain);
        if (Math.Abs(vSteps) >= ScrollMinEmitDelta)
        {
            _twoScrollAccumY -= vSteps / ScrollGain;
            emit(new PointerFallbackOp(PointerFallbackKind.Wheel, _scrollAnchorX, _scrollAnchorY, Delta: vSteps));
        }
        if (Math.Abs(hSteps) >= ScrollMinEmitDelta)
        {
            _twoScrollAccumX += hSteps / ScrollGain;
            emit(new PointerFallbackOp(PointerFallbackKind.HWheel, _scrollAnchorX, _scrollAnchorY, Delta: hSteps));
        }
    }

    private static long DistSq(int x1, int y1, int x2, int y2)
    {
        long dx = x1 - x2;
        long dy = y1 - y2;
        return dx * dx + dy * dy;
    }

    private static double Hypot(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);
}

#if WINDOWS
internal sealed class ScreenTouchInjector
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseEventFMove = 0x0001;
    private const uint MouseEventFLeftDown = 0x0002;
    private const uint MouseEventFLeftUp = 0x0004;
    private const uint MouseEventFRightDown = 0x0008;
    private const uint MouseEventFRightUp = 0x0010;
    private const uint MouseEventFMiddleDown = 0x0020;
    private const uint MouseEventFMiddleUp = 0x0040;
    private const uint MouseEventFXDown = 0x0080;
    private const uint MouseEventFXUp = 0x0100;
    private const uint MouseEventFWheel = 0x0800;
    private const uint MouseEventFHWheel = 0x1000;
    private const uint MouseEventFVirtualDesk = 0x4000;
    private const uint MouseEventFAbsolute = 0x8000;
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;
    private const uint KeyEventFExtendedKey = 0x0001;
    private const uint KeyEventFKeyUp = 0x0002;
    private const ushort VkLControl = 0xA2;
    private const ushort VkLWin = 0x5B;
    private const ushort VkTab = 0x09;
    private const ushort VkD = 0x44;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private readonly object _gate = new();
    private readonly ScreenTouchStateMachine _state = new();
    private readonly ScreenPointerFallback _fallback = new();
    private ILogger? _log;
    private bool _firstPacketLogged;
    private bool _sendInputErrorLogged;
    private int _diagOpsLogged;
    private bool _ctrlHeldByZoom;

    private readonly System.Threading.Timer _tick;

    public ScreenTouchInjector()
    {
        _tick = new System.Threading.Timer(_ => { lock (_gate) _fallback.Tick(DispatchFallbackOp); }, null, 40, 40);
    }

    public void SetLogger(ILogger? log) => _log = log;

    /// <summary>Widget option "realTouch" (default true): inject real touch so Windows shows no cursor.</summary>
    public Func<bool>? RealTouch { get; set; }

    private bool _realMode;
    private bool _touchInitTried;
    private bool _touchReady;

    private bool EnsureTouchInjection()
    {
        if (_touchInitTried) return _touchReady;
        _touchInitTried = true;
        try { _touchReady = InitializeTouchInjection(ScreenTouchProtocol.MaxPointers, TouchFeedbackNone); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _touchReady = false; }
        if (!_touchReady)
            _log?.LogWarning("ScreenMirror InitializeTouchInjection failed (Win32 error {Error}); using mouse emulation",
                Marshal.GetLastWin32Error());
        return _touchReady;
    }

    private DisplayBounds _lastBounds = new(0, 0, 1920, 1080);

    private void InjectReal(ReadOnlySpan<ResolvedTouchContact> batch, DisplayBounds bounds)
    {
        var arr = new Win32PointerTouchInfo[batch.Length];
        var minX = bounds.Left;
        var minY = bounds.Top;
        var maxX = bounds.Left + Math.Max(1, bounds.Width) - 1;
        var maxY = bounds.Top + Math.Max(1, bounds.Height) - 1;
        for (var i = 0; i < batch.Length; i++)
        {
            var c = batch[i];
            var isUp = (c.PointerFlags & ScreenTouchStateMachine.PointerFlagUp) != 0;
            arr[i] = new Win32PointerTouchInfo
            {
                PointerInfo = new Win32PointerInfo
                {
                    PointerType = PtTouch,
                    PointerId = c.PointerId,
                    PointerFlags = c.PointerFlags,
                    PtPixelLocation = new Win32Point { X = c.X, Y = c.Y },
                },
                TouchMask = isUp ? 0u : TouchMaskContactArea | TouchMaskOrientation | TouchMaskPressure,
                RcContact = isUp
                    ? default
                    : new Win32Rect
                    {
                        Left = Math.Max(minX, c.X - 2),
                        Top = Math.Max(minY, c.Y - 2),
                        Right = Math.Min(maxX, c.X + 2),
                        Bottom = Math.Min(maxY, c.Y + 2),
                    },
                Orientation = isUp ? 0u : 90u,
                Pressure = isUp ? 0u : c.Pressure,
            };
        }
        if (!InjectTouchInput((uint)arr.Length, arr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == 87) _state.Reset();
            if (!_sendInputErrorLogged)
            {
                _sendInputErrorLogged = true;
                _log?.LogWarning("ScreenMirror InjectTouchInput failed (Win32 error {Error})", err);
            }
        }
    }

    public void Inject(ReadOnlySpan<ScreenTouchContact> contacts, DisplayBounds bounds)
    {
        lock (_gate)
        {
            _lastBounds = bounds;
            // Pick the mode only between gestures so a touch never changes path half way.
            if (_state.ActiveCount == 0)
            {
                bool wanted;
                try { wanted = RealTouch?.Invoke() ?? true; } catch { wanted = true; }
                _realMode = wanted && EnsureTouchInjection();
            }

            if (!_firstPacketLogged)
            {
                _firstPacketLogged = true;
                _log?.LogInformation("ScreenMirror touch active (bounds={Left},{Top} {Width}x{Height})",
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            }

            Span<ResolvedTouchContact> resolved = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
            var count = _state.BuildFrame(contacts, bounds, resolved);
            if (count == 0) return;

            if (_realMode) InjectReal(resolved.Slice(0, count), bounds);
            else _fallback.ProcessBatch(resolved.Slice(0, count), DispatchFallbackOp);
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            if (_realMode)
            {
                Span<ResolvedTouchContact> rel = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
                var n = _state.BuildReleaseAll(rel);
                if (n > 0) InjectReal(rel.Slice(0, n), _lastBounds);
            }
            _state.Reset();
            _fallback.ReleaseAll(DispatchFallbackOp);
            if (_ctrlHeldByZoom)
            {
                _ctrlHeldByZoom = false;
                SendInputs([MakeKeyInput(VkLControl, up: true)], new PointerFallbackOp(PointerFallbackKind.ZoomEnd, 0, 0, HasPosition: false));
            }
        }
    }

    private void DispatchFallbackOp(PointerFallbackOp op)
    {
        var absX = 0;
        var absY = 0;
        var posFlags = 0u;
        if (op.HasPosition)
        {
            var vx = GetSystemMetrics(SmXVirtualScreen);
            var vy = GetSystemMetrics(SmYVirtualScreen);
            var vw = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
            var vh = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
            absX = (int)Math.Clamp(Math.Round(((op.X - vx) + 0.5) * 65535.0 / vw), 0, 65535);
            absY = (int)Math.Clamp(Math.Round(((op.Y - vy) + 0.5) * 65535.0 / vh), 0, 65535);
            posFlags = MouseEventFMove | MouseEventFAbsolute | MouseEventFVirtualDesk;
        }

        switch (op.Kind)
        {
            case PointerFallbackKind.Move:
                SendInputs([MakeMouseInput(absX, absY, posFlags)], op);
                break;
            case PointerFallbackKind.LeftDown:
                SendInputs(
                    op.HasPosition
                        ? [MakeMouseInput(absX, absY, posFlags), MakeMouseInput(0, 0, MouseEventFLeftDown)]
                        : [MakeMouseInput(0, 0, MouseEventFLeftDown)],
                    op);
                break;
            case PointerFallbackKind.LeftUp:
                SendInputs(
                    op.HasPosition
                        ? [MakeMouseInput(absX, absY, posFlags), MakeMouseInput(0, 0, MouseEventFLeftUp)]
                        : [MakeMouseInput(0, 0, MouseEventFLeftUp)],
                    op);
                break;
            case PointerFallbackKind.LeftClick:
                SendInputs(
                [
                    MakeMouseInput(absX, absY, posFlags),
                    MakeMouseInput(0, 0, MouseEventFLeftDown),
                    MakeMouseInput(0, 0, MouseEventFLeftUp),
                ], op);
                break;
            case PointerFallbackKind.LeftDoubleClick:
                SendInputs(
                [
                    MakeMouseInput(absX, absY, posFlags),
                    MakeMouseInput(0, 0, MouseEventFLeftDown),
                    MakeMouseInput(0, 0, MouseEventFLeftUp),
                ], op);
                Thread.Sleep(15);
                SendInputs(
                [
                    MakeMouseInput(absX, absY, posFlags),
                    MakeMouseInput(0, 0, MouseEventFLeftDown),
                    MakeMouseInput(0, 0, MouseEventFLeftUp),
                ], op);
                break;
            case PointerFallbackKind.RightClick:
                SendInputs(
                [
                    MakeMouseInput(absX, absY, posFlags),
                    MakeMouseInput(0, 0, MouseEventFRightDown),
                    MakeMouseInput(0, 0, MouseEventFRightUp),
                ], op);
                break;
            case PointerFallbackKind.MiddleClick:
                SendInputs(
                [
                    MakeMouseInput(absX, absY, posFlags),
                    MakeMouseInput(0, 0, MouseEventFMiddleDown),
                    MakeMouseInput(0, 0, MouseEventFMiddleUp),
                ], op);
                break;
            case PointerFallbackKind.Wheel:
                SendInputs(
                [
                    MakeMouseInput(absX, absY, posFlags),
                    MakeMouseInput(0, 0, MouseEventFWheel, unchecked((uint)op.Delta)),
                ], op);
                break;
            case PointerFallbackKind.HWheel:
                SendInputs(
                [
                    MakeMouseInput(absX, absY, posFlags),
                    MakeMouseInput(0, 0, MouseEventFHWheel, unchecked((uint)op.Delta)),
                ], op);
                break;
            case PointerFallbackKind.ZoomBegin:
                if (!_ctrlHeldByZoom)
                {
                    _ctrlHeldByZoom = true;
                    SendInputs([MakeMouseInput(absX, absY, posFlags), MakeKeyInput(VkLControl, up: false)], op);
                }
                else
                {
                    SendInputs([MakeMouseInput(absX, absY, posFlags)], op);
                }
                break;
            case PointerFallbackKind.ZoomStep:
                if (!_ctrlHeldByZoom)
                {
                    _ctrlHeldByZoom = true;
                    SendInputs(
                    [
                        MakeKeyInput(VkLControl, up: false),
                        MakeMouseInput(0, 0, MouseEventFWheel, unchecked((uint)op.Delta)),
                    ], op);
                }
                else
                {
                    SendInputs([MakeMouseInput(0, 0, MouseEventFWheel, unchecked((uint)op.Delta))], op);
                }
                break;
            case PointerFallbackKind.ZoomEnd:
                if (_ctrlHeldByZoom)
                {
                    _ctrlHeldByZoom = false;
                    SendInputs([MakeKeyInput(VkLControl, up: true)], op);
                }
                break;
            case PointerFallbackKind.BrowserBack:
                SendInputs(
                [
                    MakeMouseInput(0, 0, MouseEventFXDown, XButton1),
                    MakeMouseInput(0, 0, MouseEventFXUp, XButton1),
                ], op);
                break;
            case PointerFallbackKind.BrowserForward:
                SendInputs(
                [
                    MakeMouseInput(0, 0, MouseEventFXDown, XButton2),
                    MakeMouseInput(0, 0, MouseEventFXUp, XButton2),
                ], op);
                break;
            case PointerFallbackKind.TaskView:
                SendInputs(
                [
                    MakeKeyInput(VkLWin, up: false, forceExtended: true),
                    MakeKeyInput(VkTab, up: false),
                    MakeKeyInput(VkTab, up: true),
                    MakeKeyInput(VkLWin, up: true, forceExtended: true),
                ], op);
                break;
            case PointerFallbackKind.ShowDesktop:
                SendInputs(
                [
                    MakeKeyInput(VkLWin, up: false, forceExtended: true),
                    MakeKeyInput(VkD, up: false),
                    MakeKeyInput(VkD, up: true),
                    MakeKeyInput(VkLWin, up: true, forceExtended: true),
                ], op);
                break;
        }
    }

    private void SendInputs(Win32Input[] inputs, PointerFallbackOp op)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32Input>());
        if (sent != inputs.Length && !_sendInputErrorLogged)
        {
            _sendInputErrorLogged = true;
            _log?.LogWarning("ScreenMirror SendInput({Kind}) sent {Sent}/{Expected} (Win32 error {Error})",
                op.Kind, sent, inputs.Length, Marshal.GetLastWin32Error());
        }
        if (op.Kind is PointerFallbackKind.LeftDown or PointerFallbackKind.LeftUp or PointerFallbackKind.LeftClick
            && _diagOpsLogged < 400)
        {
            _diagOpsLogged++;
            _log?.LogInformation("ScreenMirror pointer op #{N}: {Kind} ({X},{Y}) delta={Delta} sent={Sent}/{Expected}",
                _diagOpsLogged, op.Kind, op.X, op.Y, op.Delta, sent, inputs.Length);
        }
    }

    private static Win32Input MakeMouseInput(int dx, int dy, uint flags, uint mouseData = 0) => new()
    {
        Type = InputMouse,
        U = new Win32InputUnion
        {
            Mi = new Win32MouseInputData { Dx = dx, Dy = dy, MouseData = mouseData, DwFlags = flags },
        },
    };

    private static Win32Input MakeKeyInput(ushort vk, bool up, bool forceExtended = false)
    {
        var sc = MapVirtualKeyW(vk, 4);
        var extended = forceExtended || ((sc >> 8) is 0xE0 or 0xE1);
        return new Win32Input
        {
            Type = InputKeyboard,
            U = new Win32InputUnion
            {
                Ki = new Win32KeybdInputData
                {
                    WVk = vk,
                    WScan = (ushort)(sc & 0xFF),
                    DwFlags = (up ? KeyEventFKeyUp : 0u) | (extended ? KeyEventFExtendedKey : 0u),
                },
            },
        };
    }

    private const uint PtTouch = 2;
    private const uint TouchFeedbackNone = 3;
    private const uint TouchMaskContactArea = 0x1;
    private const uint TouchMaskOrientation = 0x2;
    private const uint TouchMaskPressure = 0x4;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectTouchInput(uint count, [In] Win32PointerTouchInfo[] contacts);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, [In] Win32Input[] pInputs, int cbSize);
}
#endif

public sealed partial class ScreenMirror
{
#if WINDOWS
    private readonly ScreenTouchInjector _touch = new();

    internal static void EnablePerMonitorDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(new nint(-4)); }
        catch { }
    }

    public bool InjectTouch(ReadOnlySpan<ScreenTouchContact> contacts)
    {
        DisplayBounds bounds;
        lock (_gate)
        {
            if (!_active || _captureTarget is not { } target) return false;
            bounds = target.Bounds;
        }
        _touch.Inject(contacts, bounds);
        return true;
    }

    internal bool InjectHttpTouch(ScreenTouchHttpPayload? payload)
    {
        Span<ScreenTouchContact> buf = stackalloc ScreenTouchContact[ScreenTouchProtocol.MaxPointers];
        var n = ScreenTouchProtocol.FromHttpPayload(payload, buf);
        return InjectTouch(buf.Slice(0, n));
    }

    internal void InjectTouchPacket(ReadOnlySpan<byte> payload, DisplayBounds bounds)
    {
        Span<ScreenTouchContact> parsed = stackalloc ScreenTouchContact[ScreenTouchProtocol.MaxPointers];
        if (ScreenTouchProtocol.TryParse(payload, parsed, out var tCount))
            _touch.Inject(parsed.Slice(0, tCount), bounds);
    }

    public void ReleaseAllTouch() => _touch.ReleaseAll();

    public Func<bool>? RealTouchProvider { set => _touch.RealTouch = value; }

    [DllImport("user32.dll")]
    private static extern nint SetProcessDpiAwarenessContext(nint value);
#else
    public bool InjectTouch(ReadOnlySpan<ScreenTouchContact> contacts) => false;

    internal bool InjectHttpTouch(ScreenTouchHttpPayload? payload) => false;

    public void ReleaseAllTouch() { }

    public Func<bool>? RealTouchProvider { set { } }
#endif
}
