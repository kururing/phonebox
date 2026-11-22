using System.Diagnostics;
using System.Runtime.InteropServices;

#if WINDOWS
using System.Windows.Forms;
#endif

namespace PhoneBox.Agent;

/// <summary>Streams the NVIDIA-connected secondary display to the phone.</summary>
public sealed partial class ScreenMirror : IDisposable
{
    internal static readonly TimeSpan VddDetachDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan IdleSessionTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly ILogger<ScreenMirror>? _log;
    private bool _active;
    private long _lastRequest;
    private string? _lastError;

    public ScreenMirror(ILogger<ScreenMirror>? log = null)
    {
        _log = log;
#if WINDOWS
        _touch.SetLogger(log);
        _vddDetach = new VddDetachScheduler(
            _gate,
            VddDetachDelay,
            canDetach: () => !_active && _activeVideo is not { IsCancellationRequested: false },
            detach: () =>
            {
                var detached = DetachVddDisplay();
                _log?.LogInformation("ScreenMirror: VDD secondary display detached after {DelaySec:F0}s (ok={Ok})",
                    VddDetachDelay.TotalSeconds, detached);
            },
            onError: ex => _log?.LogWarning("ScreenMirror: delayed VDD detach failed: {Msg}", ex.Message));
#endif
    }

    internal static TimeSpan ComputeRemainingDetachDelay(TimeSpan elapsedSinceExit, TimeSpan totalDelay)
    {
        var remaining = totalDelay - elapsedSinceExit;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    internal sealed class VddDetachScheduler : IDisposable
    {
        private readonly object _gate;
        private readonly TimeSpan _defaultDelay;
        private readonly Func<bool> _canDetach;
        private readonly Action _detach;
        private readonly Action<Exception>? _onError;
        private CancellationTokenSource? _pendingCts;
        private long _generation;
        private bool _disposed;

        public VddDetachScheduler(
            object gate,
            TimeSpan defaultDelay,
            Func<bool> canDetach,
            Action detach,
            Action<Exception>? onError = null)
        {
            _gate = gate;
            _defaultDelay = defaultDelay;
            _canDetach = canDetach;
            _detach = detach;
            _onError = onError;
        }

        public bool IsPending
        {
            get
            {
                lock (_gate) return _pendingCts is not null;
            }
        }

        public void ScheduleLocked(TimeSpan? delayOverride = null)
        {
            if (_disposed || !_canDetach()) return;
            // Preserve an existing countdown instead of extending it on duplicate stops.
            if (_pendingCts is not null) return;
            var delay = delayOverride ?? _defaultDelay;
            if (delay <= TimeSpan.Zero)
            {
                _detach();
                return;
            }

            var gen = ++_generation;
            var cts = new CancellationTokenSource();
            _pendingCts = cts;
            _ = RunAsync(cts, gen, delay);
        }

        public void CancelLocked()
        {
            _generation++;
            var cts = _pendingCts;
            _pendingCts = null;
            if (cts is not null)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        public void DetachNowLocked(bool markDisposed = false)
        {
            if (_disposed) return;
            if (markDisposed) _disposed = true;
            CancelLocked();
            _detach();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                CancelLocked();
            }
        }

        private async Task RunAsync(CancellationTokenSource cts, long gen, TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || _generation != gen || !ReferenceEquals(_pendingCts, cts)) return;
                    _pendingCts = null;
                    if (!_canDetach()) return;
                    _detach();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _onError?.Invoke(ex); }
            finally { cts.Dispose(); }
        }
    }

#if WINDOWS
    private readonly VddDetachScheduler _vddDetach;
    private CancellationTokenSource? _activeVideo;
    private NvidiaCaptureTarget? _captureTarget;
    private bool _isPrimaryMode;

    public ScreenMirrorStatus Status()
    {
        bool stoppedIdle;
        ScreenMirrorStatus status;
        lock (_gate)
        {
            stoppedIdle = StopIfIdleLocked();
            status = BuildStatusLocked();
        }
        if (stoppedIdle) RestoreDefaultAudioIfSwitched();
        return status;
    }

    private ScreenMirrorStatus BuildStatusLocked()
    {
        var deviceName = _isPrimaryMode ? PrimaryDisplayDeviceName() : SecondaryDisplayDeviceName();
        var target = _active ? _captureTarget :
            deviceName is null ? null : FindNvidiaCaptureTarget(deviceName);
        var error = _lastError ?? (deviceName is null
            ? "Chưa có màn hình phụ"
            : target is null
                ? "Màn hình phụ không được nối với card NVIDIA"
                : null);
        return new(true, _active && target is not null, target?.Width ?? 0, target?.Height ?? 0,
            target is not null, target?.DeviceName ?? deviceName, error);
    }

    public ScreenMirrorStatus Start(int phoneWidth = 0, int phoneHeight = 0, bool primary = false)
    {
        lock (_gate)
        {
            if (primary)
            {
                var primaryName = PrimaryDisplayDeviceName();
                if (primaryName is null)
                {
                    _active = false;
                    _captureTarget = null;
                    _isPrimaryMode = false;
                    _lastError = "Không tìm thấy màn hình chính";
                    return BuildStatusLocked();
                }
                var primaryTarget = FindCaptureTarget(primaryName, requireNvidia: false);
                if (primaryTarget is null)
                {
                    _active = false;
                    _captureTarget = null;
                    _isPrimaryMode = false;
                    _lastError = "Không thể chụp màn hình chính (DXGI)";
                    return BuildStatusLocked();
                }
                _active = true;
                _isPrimaryMode = true;
                _captureTarget = primaryTarget;
                _lastError = null;
                _lastRequest = Stopwatch.GetTimestamp();
                _activeVideo?.Cancel();
                ScheduleIdleCheck();
                return BuildStatusLocked();
            }

            _vddDetach.CancelLocked();
            _isPrimaryMode = false;
            var attachError = AttachVddDisplay();
            if (attachError is not null)
            {
                _active = false;
                _captureTarget = null;
                _lastError = attachError;
                return BuildStatusLocked();
            }
            var deviceName = SecondaryDisplayDeviceName();
            if (deviceName is null) return BuildStatusLocked();
            if (phoneWidth > 0 && phoneHeight > 0)
            {
                var preferredGpu = FindPreferredNvidiaAdapterName();
                var error = ApplyPhoneDisplayResolution(deviceName, phoneWidth, phoneHeight, preferredGpu);
                if (error is not null)
                {
                    _active = false;
                    _captureTarget = null;
                    _lastError = error;
                    _vddDetach.ScheduleLocked();
                    return BuildStatusLocked();
                }
                deviceName = SecondaryDisplayDeviceName();
                if (deviceName is null) return BuildStatusLocked();
            }
            var target = FindNvidiaCaptureTargetWithWait(deviceName);
            if (target is null)
            {
                _active = false;
                _captureTarget = null;
                _vddDetach.ScheduleLocked();
                return BuildStatusLocked();
            }
            _active = true;
            _captureTarget = target;
            _lastError = null;
            _lastRequest = Stopwatch.GetTimestamp();
            _activeVideo?.Cancel();
            ScheduleIdleCheck();
            return BuildStatusLocked();
        }
    }
#else
    public ScreenMirrorStatus Status() => new(false, false, 0, 0, false, null, "Windows only");

    public ScreenMirrorStatus Start(int phoneWidth = 0, int phoneHeight = 0, bool primary = false) => Status();

#endif

    public ScreenMirrorStatus Stop()
    {
        ScreenMirrorStatus? status = null;
        lock (_gate)
        {
            StopSessionLocked(detachImmediately: false);
#if WINDOWS
            status = BuildStatusLocked();
#endif
        }
#if WINDOWS
        RestoreDefaultAudioIfSwitched();
        return status!;
#else
        return Status();
#endif
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopSessionLocked(detachImmediately: true, markDisposed: true);
        }
#if WINDOWS
        RestoreDefaultAudioIfSwitched();
#endif
    }

    private void StopSessionLocked(bool detachImmediately, TimeSpan? detachDelay = null, bool markDisposed = false)
    {
        _active = false;
        _lastRequest = 0;
#if WINDOWS
        var wasPrimary = _isPrimaryMode;
        _isPrimaryMode = false;
        _captureTarget = null;
        _activeVideo?.Cancel();
        _touch.ReleaseAll();
        if (detachImmediately)
            _vddDetach.DetachNowLocked(markDisposed);
        else if (markDisposed)
            _vddDetach.Dispose();
        else if (!wasPrimary)
            _vddDetach.ScheduleLocked(detachDelay);
#endif
        _lastError = null;
    }

#if WINDOWS
    private static NvidiaCaptureTarget? FindNvidiaCaptureTargetWithWait(string deviceName, int timeoutMs = 1500)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var target = FindNvidiaCaptureTarget(deviceName);
            if (target is not null) return target;
            if (Environment.TickCount64 >= deadline) return null;
            Thread.Sleep(50);
        }
    }

    private static NvidiaCaptureTarget? FindNvidiaCaptureTarget(string deviceName) =>
        FindCaptureTarget(deviceName, requireNvidia: true);

    private static NvidiaCaptureTarget? FindCaptureTarget(string deviceName, bool requireNvidia)
    {
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return null;
            var enumAdapters = VTableMethod<EnumAdapters1Delegate>(factory, 12);
            var nvidiaGpuIndex = 0;

            for (uint adapterIndex = 0; adapterIndex < 16; adapterIndex++)
            {
                IntPtr adapter = IntPtr.Zero;
                try
                {
                    if (enumAdapters(factory, adapterIndex, out adapter) < 0 || adapter == IntPtr.Zero) break;
                    VTableMethod<GetAdapterDescDelegate>(adapter, 10)(adapter, out var adapterDesc);
                    var isNvidia = adapterDesc.VendorId == 0x10DE;
                    if (requireNvidia && !isNvidia) continue;

                    var gpuIndex = isNvidia ? nvidiaGpuIndex++ : 0;
                    var enumOutputs = VTableMethod<EnumOutputsDelegate>(adapter, 7);
                    for (uint outputIndex = 0; outputIndex < 16; outputIndex++)
                    {
                        IntPtr output = IntPtr.Zero;
                        try
                        {
                            if (enumOutputs(adapter, outputIndex, out output) < 0 || output == IntPtr.Zero) break;
                            VTableMethod<GetOutputDescDelegate>(output, 7)(output, out var outputDesc);
                            if (outputDesc.AttachedToDesktop == 0 ||
                                !string.Equals(outputDesc.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                                continue;

                            var left = outputDesc.DesktopCoordinates.Left;
                            var top = outputDesc.DesktopCoordinates.Top;
                            var width = outputDesc.DesktopCoordinates.Right - left;
                            var height = outputDesc.DesktopCoordinates.Bottom - top;
                            if (width > 0 && height > 0)
                                return new((int)adapterIndex, (int)outputIndex, gpuIndex, left, top, width, height, deviceName,
                                    adapterDesc.Description?.Trim() ?? "");
                        }
                        finally
                        {
                            if (output != IntPtr.Zero) Marshal.Release(output);
                        }
                    }
                }
                finally
                {
                    if (adapter != IntPtr.Zero) Marshal.Release(adapter);
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or
                                   COMException or MarshalDirectiveException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

    private static T VTableMethod<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(
            Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDescDelegate(IntPtr self, out DxgiAdapterDesc1 desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumOutputsDelegate(IntPtr self, uint index, out IntPtr output);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetOutputDescDelegate(IntPtr self, out DxgiOutputDesc desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiOutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public DxgiRect DesktopCoordinates;
        public int AttachedToDesktop;
        public uint Rotation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

    internal static string? FindPreferredNvidiaAdapterName()
    {
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return null;
            var enumAdapters = VTableMethod<EnumAdapters1Delegate>(factory, 12);
            for (uint adapterIndex = 0; adapterIndex < 16; adapterIndex++)
            {
                IntPtr adapter = IntPtr.Zero;
                try
                {
                    if (enumAdapters(factory, adapterIndex, out adapter) < 0 || adapter == IntPtr.Zero) break;
                    VTableMethod<GetAdapterDescDelegate>(adapter, 10)(adapter, out var adapterDesc);
                    if (adapterDesc.VendorId == 0x10DE && !string.IsNullOrWhiteSpace(adapterDesc.Description))
                        return adapterDesc.Description.Trim();
                }
                finally
                {
                    if (adapter != IntPtr.Zero) Marshal.Release(adapter);
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or
                                   COMException or MarshalDirectiveException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

    private sealed record NvidiaCaptureTarget(int AdapterIndex, int OutputIndex, int NvidiaGpuIndex,
        int Left, int Top, int Width, int Height, string DeviceName, string AdapterName)
    {
        public DisplayBounds Bounds => new(Left, Top, Width, Height);
    }

    private bool StopIfIdleLocked()
    {
        if (_activeVideo is { IsCancellationRequested: false }) return false;
        if (!_active || _lastRequest == 0) return false;
        var elapsed = Stopwatch.GetElapsedTime(_lastRequest);
        if (elapsed <= IdleSessionTimeout) return false;
        var remaining = ComputeRemainingDetachDelay(elapsed, VddDetachDelay);
        StopSessionLocked(detachImmediately: false, detachDelay: remaining);
        return true;
    }

    private void ScheduleIdleCheck()
    {
        _ = Task.Delay(IdleSessionTimeout + TimeSpan.FromMilliseconds(500)).ContinueWith(_ =>
        {
            bool stopped;
            lock (_gate) stopped = StopIfIdleLocked();
            if (stopped) RestoreDefaultAudioIfSwitched();
        }, TaskScheduler.Default);
    }
#endif

    private bool Fail(string message)
    {
        lock (_gate) _lastError = message;
        return false;
    }

    public bool InjectKey(string? text, string? key)
    {
#if WINDOWS
        lock (_gate)
        {
            if (!_active) return false;
            _lastRequest = Stopwatch.GetTimestamp();
        }
        var inputs = new List<KeyInput>();
        if (!string.IsNullOrEmpty(text))
        {
            foreach (var ch in text)
            {
                if (ch == '\r') continue;
                if (ch == '\n')
                {
                    AddVirtualKey(inputs, 0x0D);
                    continue;
                }
                inputs.Add(KeyInput.Unicode(ch, keyUp: false));
                inputs.Add(KeyInput.Unicode(ch, keyUp: true));
            }
        }
        if (!string.IsNullOrWhiteSpace(key))
        {
            switch (key.Trim().ToLowerInvariant())
            {
                case "enter": AddVirtualKey(inputs, 0x0D); break;
                case "backspace": AddVirtualKey(inputs, 0x08); break;
                case "tab": AddVirtualKey(inputs, 0x09); break;
                case "esc": AddVirtualKey(inputs, 0x1B); break;
                case "del": AddVirtualKey(inputs, 0x2E); break;
                case "left": AddVirtualKey(inputs, 0x25); break;
                case "up": AddVirtualKey(inputs, 0x26); break;
                case "right": AddVirtualKey(inputs, 0x27); break;
                case "down": AddVirtualKey(inputs, 0x28); break;
                case "win": AddVirtualKey(inputs, 0x5B); break;
                case "prtsc": AddVirtualKey(inputs, 0x2C); break;
                case "copy": AddCtrlKey(inputs, 0x43); break;
                case "paste": AddCtrlKey(inputs, 0x56); break;
            }
        }
        if (inputs.Count == 0) return true;
        var arr = inputs.ToArray();
        SendKeyInput((uint)arr.Length, arr, Marshal.SizeOf<KeyInput>());
        return true;
#else
        return false;
#endif
    }

#if WINDOWS
    private static void AddVirtualKey(List<KeyInput> list, ushort vk)
    {
        list.Add(KeyInput.Virtual(vk, keyUp: false));
        list.Add(KeyInput.Virtual(vk, keyUp: true));
    }

    private static void AddCtrlKey(List<KeyInput> list, ushort vk)
    {
        list.Add(KeyInput.Virtual(0x11, keyUp: false));
        list.Add(KeyInput.Virtual(vk, keyUp: false));
        list.Add(KeyInput.Virtual(vk, keyUp: true));
        list.Add(KeyInput.Virtual(0x11, keyUp: true));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput
    {
        public uint Type;
        public KeyInputUnion Union;

        public static KeyInput Unicode(char ch, bool keyUp) => new()
        {
            Type = 1,
            Union = new KeyInputUnion
            {
                Ki = new KeybdInput { WScan = ch, DwFlags = 0x0004u | (keyUp ? 0x0002u : 0u) }
            }
        };

        public static KeyInput Virtual(ushort vk, bool keyUp) => new()
        {
            Type = 1,
            Union = new KeyInputUnion
            {
                Ki = new KeybdInput { WVk = vk, DwFlags = keyUp ? 0x0002u : 0u }
            }
        };
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct KeyInputUnion
    {
        [FieldOffset(0)] public KeybdInput Ki;
        [FieldOffset(0)] public MouseInputPad Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputPad
    {
        public int Dx, Dy;
        public uint MouseData, DwFlags, Time;
        public nint DwExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendKeyInput(uint nInputs, [In] KeyInput[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo lpgui);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int CbSize;
        public uint Flags;
        public IntPtr HwndActive;
        public IntPtr HwndFocus;
        public IntPtr HwndCapture;
        public IntPtr HwndMenuOwner;
        public IntPtr HwndMoveSize;
        public IntPtr HwndCaret;
        public DxgiRect RcCaret;
    }

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation;

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        int CompareElements(IntPtr el1, IntPtr el2);
        int CompareRuntimeIds(IntPtr id1, IntPtr id2);
        IntPtr GetRootElement();
        IntPtr ElementFromPoint(long pt);
        IntPtr ElementFromHandle(IntPtr hwnd);
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement? element);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? retVal);
    }

    private static IUIAutomation? _uia;

    private static bool IsTextInputFocused(out double left, out double top, out double right, out double bottom, out bool hasBounds)
    {
        left = top = right = bottom = 0;
        hasBounds = false;
        try
        {
            _uia ??= (IUIAutomation)new CUIAutomation();
            if (_uia.GetFocusedElement(out var el) == 0 && el is not null)
            {
                try
                {
                    var isInput = false;
                    // UIA_ControlTypePropertyId = 30003: Edit=50004, ComboBox=50003, Document=50030
                    if (el.GetCurrentPropertyValue(30003, out var ctObj) == 0 && ctObj is int ct)
                    {
                        if (ct == 50004 || ct == 50003) isInput = true;
                        else if (ct == 50030)
                        {
                            isInput = !(el.GetCurrentPropertyValue(30046, out var roObj) == 0 && roObj is true);
                        }
                    }
                    if (!isInput && el.GetCurrentPropertyValue(30043, out var vpObj) == 0 && vpObj is true)
                    {
                        if (el.GetCurrentPropertyValue(30046, out var roObj) == 0 && roObj is false)
                            isInput = true;
                    }
                    if (isInput)
                    {
                        // UIA_BoundingRectanglePropertyId = 30001 -> double[4] { left, top, width, height }
                        if (el.GetCurrentPropertyValue(30001, out var brObj) == 0 && brObj is double[] br && br.Length == 4 && br[2] > 0 && br[3] > 0)
                        {
                            left = br[0];
                            top = br[1];
                            right = br[0] + br[2];
                            bottom = br[1] + br[3];
                            hasBounds = true;
                        }
                        return true;
                    }
                }
                finally
                {
                    if (Marshal.IsComObject(el)) Marshal.ReleaseComObject(el);
                }
            }
        }
        catch { }

        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            var tid = GetWindowThreadProcessId(fg, out _);
            if (tid != 0)
            {
                var info = new GuiThreadInfo { CbSize = Marshal.SizeOf<GuiThreadInfo>() };
                if (GetGUIThreadInfo(tid, ref info) && info.HwndCaret != IntPtr.Zero)
                    return true;
            }
        }
        return false;
    }
#endif
}

public sealed record ScreenMirrorStatus(bool Supported, bool Active, int Width, int Height,
    bool DisplayAvailable, string? DisplayName, string? Error);
