using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

public sealed class ScreenMirrorTouchTests
{
    private static byte[] BuildPacket(params (byte id, ScreenTouchState state, ushort x, ushort y, ushort p)[] contacts)
    {
        var buf = new byte[2 + contacts.Length * 8];
        buf[0] = ScreenTouchProtocol.OpcodeTouch;
        buf[1] = (byte)contacts.Length;
        for (var i = 0; i < contacts.Length; i++)
        {
            var o = 2 + i * 8;
            buf[o] = contacts[i].id;
            buf[o + 1] = (byte)contacts[i].state;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 2, 2), contacts[i].x);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 4, 2), contacts[i].y);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o + 6, 2), contacts[i].p);
        }
        return buf;
    }

    [Fact]
    public void TryParseHandlesSingleMultiZeroAndRejectsMalformedPackets()
    {
        Span<ScreenTouchContact> dst = stackalloc ScreenTouchContact[ScreenTouchProtocol.MaxPointers];

        var single = BuildPacket((2, ScreenTouchState.Down, 1234, 5678, 512));
        Assert.True(ScreenTouchProtocol.TryParse(single, dst, out var n1));
        Assert.Equal(1, n1);
        Assert.Equal(new ScreenTouchContact(2, ScreenTouchState.Down, 1234, 5678, 512), dst[0]);

        var multi = BuildPacket(
            (0, ScreenTouchState.Move, 100, 200, 400),
            (3, ScreenTouchState.Up, 65535, 65535, 1));
        Assert.True(ScreenTouchProtocol.TryParse(multi, dst, out var n2));
        Assert.Equal(2, n2);
        Assert.Equal(new ScreenTouchContact(0, ScreenTouchState.Move, 100, 200, 400), dst[0]);
        Assert.Equal(new ScreenTouchContact(3, ScreenTouchState.Up, 65535, 65535, 1), dst[1]);

        Assert.True(ScreenTouchProtocol.TryParse(new byte[] { 0x30, 0x00 }, dst, out var n0));
        Assert.Equal(0, n0);

        Assert.False(ScreenTouchProtocol.TryParse(Array.Empty<byte>(), dst, out _));
        Assert.False(ScreenTouchProtocol.TryParse(new byte[] { 0x31, 0x01, 0, 0, 0, 0, 0, 0, 0, 0 }, dst, out _));
        Assert.False(ScreenTouchProtocol.TryParse(new byte[] { 0x30, 0x01, 0, 0, 0 }, dst, out _));
        Assert.False(ScreenTouchProtocol.TryParse(new byte[] { 0x30, 0x01, 0, 4, 0, 0, 0, 0, 0, 0 }, dst, out _));
        Assert.False(ScreenTouchProtocol.TryParse(new byte[] { 0x30, 11 }, dst, out _));
    }

    [Theory]
    [InlineData(1920, 0, 1600, 720, 0, 0, 1920, 0)]
    [InlineData(1920, 0, 1600, 720, 65535, 65535, 3519, 719)]
    [InlineData(1920, 0, 1600, 720, 32768, 32768, 2720, 360)]
    [InlineData(-1600, -200, 1600, 720, 0, 0, -1600, -200)]
    [InlineData(-1600, -200, 1600, 720, 65535, 65535, -1, 519)]
    [InlineData(-1600, -200, 1600, 720, 32768, 32768, -800, 160)]
    public void MapNormalizedToDisplaySupportsPositiveAndNegativeOffsets(
        int left, int top, int width, int height,
        ushort xNorm, ushort yNorm, int expectedX, int expectedY)
    {
        var bounds = new DisplayBounds(left, top, width, height);
        var (x, y) = ScreenTouchProtocol.MapNormalizedToDisplay(xNorm, yNorm, bounds);
        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
    }

    [Fact]
    public void StateMachineMaintainsValidTransitionsAndMultiTouchPreservation()
    {
        var sm = new ScreenTouchStateMachine();
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];

        // Stray Up is ignored cleanly
        Assert.Equal(0, sm.BuildFrame([new ScreenTouchContact(1, ScreenTouchState.Up, 0, 0, 512)], bounds, outBuf));
        Assert.Equal(0, sm.ActiveCount);

        // Move without prior Down is upgraded to Down
        var n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Move, 0, 0, 0)], bounds, outBuf);
        Assert.Equal(1, n);
        Assert.Equal(ScreenTouchStateMachine.PointerFlagDown | ScreenTouchStateMachine.PointerFlagInRange | ScreenTouchStateMachine.PointerFlagInContact, outBuf[0].PointerFlags);
        Assert.Equal(512u, outBuf[0].Pressure);
        Assert.Equal(1, sm.ActiveCount);

        // Duplicate Down on active contact becomes Update
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, 32768, 32768, 800)], bounds, outBuf);
        Assert.Equal(1, n);
        Assert.Equal(ScreenTouchStateMachine.PointerFlagUpdate | ScreenTouchStateMachine.PointerFlagInRange | ScreenTouchStateMachine.PointerFlagInContact, outBuf[0].PointerFlags);
        Assert.Equal(800u, outBuf[0].Pressure);

        // Second finger Down includes already-active finger 0 if omitted, or updates both
        n = sm.BuildFrame([new ScreenTouchContact(1, ScreenTouchState.Down, 65535, 65535, 300)], bounds, outBuf);
        Assert.Equal(2, n);
        Assert.Equal(2, sm.ActiveCount);
        Assert.Equal(1u, outBuf[0].PointerId);
        Assert.Equal(ScreenTouchStateMachine.PointerFlagDown | ScreenTouchStateMachine.PointerFlagInRange | ScreenTouchStateMachine.PointerFlagInContact, outBuf[0].PointerFlags);
        Assert.Equal(0u, outBuf[1].PointerId);
        Assert.Equal(ScreenTouchStateMachine.PointerFlagUpdate | ScreenTouchStateMachine.PointerFlagInRange | ScreenTouchStateMachine.PointerFlagInContact, outBuf[1].PointerFlags);

        // Finger 1 Up preserves finger 0 as Update
        n = sm.BuildFrame([new ScreenTouchContact(1, ScreenTouchState.Up, 65535, 65535, 1)], bounds, outBuf);
        Assert.Equal(2, n);
        Assert.Equal(1, sm.ActiveCount);
        Assert.Equal(1u, outBuf[0].PointerId);
        Assert.Equal(ScreenTouchStateMachine.PointerFlagUp, outBuf[0].PointerFlags);
        Assert.Equal(0u, outBuf[1].PointerId);
        Assert.Equal(ScreenTouchStateMachine.PointerFlagUpdate | ScreenTouchStateMachine.PointerFlagInRange | ScreenTouchStateMachine.PointerFlagInContact, outBuf[1].PointerFlags);

        // BuildReleaseAll cancels remaining active finger and clears state
        n = sm.BuildReleaseAll(outBuf);
        Assert.Equal(1, n);
        Assert.Equal(0u, outBuf[0].PointerId);
        Assert.Equal(ScreenTouchStateMachine.PointerFlagUp | ScreenTouchStateMachine.PointerFlagCanceled, outBuf[0].PointerFlags);
        Assert.Equal(0, sm.ActiveCount);
    }

    [Fact]
    public void Win32TouchStructsMatchExactX64NativeLayout()
    {
        if (IntPtr.Size == 8)
        {
            Assert.Equal(96, Marshal.SizeOf<Win32PointerInfo>());
            Assert.Equal(144, Marshal.SizeOf<Win32PointerTouchInfo>());
            Assert.Equal(40, Marshal.SizeOf<Win32MouseInput>());
            Assert.Equal(40, Marshal.SizeOf<Win32Input>());
        }
    }

    private static (ushort xNorm, ushort yNorm, int x, int y) NormAt(DisplayBounds bounds, ushort xNorm, ushort yNorm)
    {
        var (x, y) = ScreenTouchProtocol.MapNormalizedToDisplay(xNorm, yNorm, bounds);
        return (xNorm, yNorm, x, y);
    }

    [Fact]
    public void PointerFallbackSingleFingerHoldAndMoveEmitsHoverMoveOnlyWithoutClick()
    {
        var sm = new ScreenTouchStateMachine();
        var now = 1000L;
        var fb = new ScreenPointerFallback(() => now);
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        var n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, 0, 0, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        Assert.Empty(ops);

        now += 50;
        var p1 = NormAt(bounds, 32768, 32768);
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Move, p1.xNorm, p1.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        now += 50;
        var p2 = NormAt(bounds, 40000, 40000);
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Move, p2.xNorm, p2.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        now += 50;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, p2.xNorm, p2.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Equal(
        [
            new PointerFallbackOp(PointerFallbackKind.Move, p1.x, p1.y),
            new PointerFallbackOp(PointerFallbackKind.Move, p2.x, p2.y),
        ], ops);
    }

    [Fact]
    public void PointerFallbackSingleFingerLongHoldWithoutMoveEmitsNothingOnUp()
    {
        var sm = new ScreenTouchStateMachine();
        var now = 1000L;
        var fb = new ScreenPointerFallback(() => now);
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        var n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, 32768, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        now += ScreenPointerFallback.TapMaxDurationMs + 50;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, 32768, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Empty(ops);
    }

    [Fact]
    public void PointerFallbackDoubleTapAndTapHoldEngageSecondPressImmediatelyOnDown()
    {
        var sm = new ScreenTouchStateMachine();
        var now = 1000L;
        var fb = new ScreenPointerFallback(() => now);
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        var tap1 = NormAt(bounds, 32768, 32768);
        var tap2 = NormAt(bounds, 34500, 32768); // ~42 px right, inside DoubleTapMaxRadiusPx (96)

        // 1. Quick double-tap: 2nd Down immediately presses LeftDown at Tap 1's snapped pixel, Up releases LeftUp
        var n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, tap1.xNorm, tap1.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 50;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, tap1.xNorm, tap1.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        now += 150;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, tap2.xNorm, tap2.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Equal(
        [
            new PointerFallbackOp(PointerFallbackKind.Move, tap1.x, tap1.y),
            new PointerFallbackOp(PointerFallbackKind.LeftDown, tap1.x, tap1.y),
        ], ops);

        now += 60;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, tap2.xNorm, tap2.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        // quick 2nd tap = the held-back 1st click was dropped, so Down/Up + one more click = double-click
        Assert.Equal(
        [
            new PointerFallbackOp(PointerFallbackKind.Move, tap1.x, tap1.y),
            new PointerFallbackOp(PointerFallbackKind.LeftDown, tap1.x, tap1.y),
            new PointerFallbackOp(PointerFallbackKind.LeftUp, tap1.x, tap1.y),
            new PointerFallbackOp(PointerFallbackKind.LeftClick, tap1.x, tap1.y),
        ], ops);

        // 2. "Ấn 2 lần rồi giữ": holding the 2nd press for > 1500ms without moving keeps LeftDown held and releases LeftUp on lift
        ops.Clear();
        sm.Reset();
        fb.ReleaseAll(ops.Add);
        now += 1000;

        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, tap1.xNorm, tap1.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 50;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, tap1.xNorm, tap1.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        now += 200;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, tap2.xNorm, tap2.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        // Hold stationary for 1500ms (>> TapMaxDurationMs = 350ms)
        now += 1500;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, tap2.xNorm, tap2.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Equal(
        [
            new PointerFallbackOp(PointerFallbackKind.Move, tap1.x, tap1.y),
            new PointerFallbackOp(PointerFallbackKind.LeftDown, tap1.x, tap1.y),
            new PointerFallbackOp(PointerFallbackKind.LeftUp, tap1.x, tap1.y),
        ], ops);

        ops.Clear();
        ScreenPointerFallback.EmitDoubleTap(2400, 300, ops.Add);
        Assert.Equal([new PointerFallbackOp(PointerFallbackKind.LeftDoubleClick, 2400, 300)], ops);
    }

    [Fact]
    public void PointerFallbackTapAndDragEmitsLeftDownMoveAndLeftUp()
    {
        var sm = new ScreenTouchStateMachine();
        var now = 1000L;
        var fb = new ScreenPointerFallback(() => now);
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        var p1 = NormAt(bounds, 0, 0);
        var p2 = NormAt(bounds, 32768, 32768);

        var n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, p1.xNorm, p1.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, p1.xNorm, p1.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        now += 150;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, p1.xNorm, p1.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Move, p2.xNorm, p2.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, p2.xNorm, p2.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Equal(
        [
            new PointerFallbackOp(PointerFallbackKind.Move, p1.x, p1.y),
            new PointerFallbackOp(PointerFallbackKind.LeftDown, p1.x, p1.y),
            new PointerFallbackOp(PointerFallbackKind.Move, p2.x, p2.y),
            new PointerFallbackOp(PointerFallbackKind.LeftUp, p2.x, p2.y),
        ], ops);
    }

    [Fact]
    public void PointerFallbackTwoFingerScrollWithStaggeredLandingDoesNotClickAndLocksAxis()
    {
        var sm = new ScreenTouchStateMachine();
        var now = 1000L;
        var fb = new ScreenPointerFallback(() => now);
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        var n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, 16384, 16384, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        Assert.Empty(ops);

        // Micro-jitter <= 4px before second finger lands stays TapPending without Move or LeftDown
        now += 20;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Move, 16480, 16480, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        Assert.Empty(ops);

        now += 20;
        n = sm.BuildFrame([new ScreenTouchContact(1, ScreenTouchState.Down, 20000, 16384, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        Assert.Empty(ops);

        now += 30;
        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Move, 16460, 26000, 512),
            new ScreenTouchContact(1, ScreenTouchState.Move, 20080, 26000, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        now += 30;
        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Up, 16460, 26000, 512),
            new ScreenTouchContact(1, ScreenTouchState.Up, 20080, 26000, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.DoesNotContain(ops, o => o.Kind == PointerFallbackKind.LeftClick);
        Assert.DoesNotContain(ops, o => o.Kind == PointerFallbackKind.LeftDown);
        Assert.DoesNotContain(ops, o => o.Kind == PointerFallbackKind.LeftUp);
        Assert.DoesNotContain(ops, o => o.Kind == PointerFallbackKind.RightClick);
        Assert.DoesNotContain(ops, o => o.Kind == PointerFallbackKind.ZoomBegin);
        Assert.DoesNotContain(ops, o => o.Kind == PointerFallbackKind.HWheel);
        Assert.Contains(ops, o => o.Kind == PointerFallbackKind.Wheel && o.Delta > 0);
    }

    [Fact]
    public void PointerFallbackReleasesDraggedLeftDownWhenSecondFingerArrives()
    {
        var sm = new ScreenTouchStateMachine();
        var now = 1000L;
        var fb = new ScreenPointerFallback(() => now);
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        // Tap 1
        var n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, 16384, 16384, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, 16384, 16384, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        // Tap 2 & drag past slop (>= 16 px) engages LeftDown
        now += 150;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, 16384, 16384, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Move, 18000, 16384, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        ops.Clear(); // inspect from second finger arrival onwards
        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(1, ScreenTouchState.Down, 20000, 16384, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        now += 40;
        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Move, 18000, 24000, 512),
            new ScreenTouchContact(1, ScreenTouchState.Move, 20000, 24000, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        now += 40;
        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Up, 18000, 24000, 512),
            new ScreenTouchContact(1, ScreenTouchState.Up, 20000, 24000, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Contains(ops, o => o.Kind == PointerFallbackKind.LeftUp && !o.HasPosition);
        Assert.Contains(ops, o => o.Kind == PointerFallbackKind.Wheel && o.Delta > 0);
        Assert.DoesNotContain(ops, o => o.Kind == PointerFallbackKind.RightClick);
        Assert.DoesNotContain(ops, o => o.Kind == PointerFallbackKind.LeftClick);
    }

    [Fact]
    public void PointerFallbackTwoFingerTapEmitsRightClickAtSecondFingerPosition()
    {
        var sm = new ScreenTouchStateMachine();
        var now = 1000L;
        var fb = new ScreenPointerFallback(() => now);
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        var f1 = NormAt(bounds, 10000, 10000);
        var f2 = NormAt(bounds, 50000, 50000);

        var n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Down, f1.xNorm, f1.yNorm, 512),
            new ScreenTouchContact(1, ScreenTouchState.Down, f2.xNorm, f2.yNorm, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        // Finger 1 lifts slightly first -> still TwoFingerEnding
        now += 60;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, f1.xNorm, f1.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        Assert.Empty(ops);

        // Finger 2 lifts -> RightClick at Finger 2 coordinates (not midpoint)
        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(1, ScreenTouchState.Up, f2.xNorm, f2.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Equal([new PointerFallbackOp(PointerFallbackKind.RightClick, f2.x, f2.y)], ops);
    }

    [Fact]
    public void PointerFallbackHoldFirstAndTapSecondEmitsRightClickAtSecondFingerImmediately()
    {
        var sm = new ScreenTouchStateMachine();
        var now = 1000L;
        var fb = new ScreenPointerFallback(() => now);
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        var anchor = NormAt(bounds, 8000, 12000);
        var tapA = NormAt(bounds, 48000, 42000);
        var tapB = NormAt(bounds, 56000, 20000);

        // Finger 1 held for 1000ms (> TapMaxDurationMs)
        var n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, anchor.xNorm, anchor.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 1000;

        // Finger 2 taps at tapA and lifts while Finger 1 stays held
        n = sm.BuildFrame([new ScreenTouchContact(1, ScreenTouchState.Down, tapA.xNorm, tapA.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 120;
        n = sm.BuildFrame([new ScreenTouchContact(1, ScreenTouchState.Up, tapA.xNorm, tapA.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Equal([new PointerFallbackOp(PointerFallbackKind.RightClick, tapA.x, tapA.y)], ops);

        // Finger 2 taps again at tapB while Finger 1 still stays held
        now += 250;
        n = sm.BuildFrame([new ScreenTouchContact(2, ScreenTouchState.Down, tapB.xNorm, tapB.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 120;
        n = sm.BuildFrame([new ScreenTouchContact(2, ScreenTouchState.Up, tapB.xNorm, tapB.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        // Finger 1 finally lifts -> no LeftClick
        now += 100;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, anchor.xNorm, anchor.yNorm, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Equal(
        [
            new PointerFallbackOp(PointerFallbackKind.RightClick, tapA.x, tapA.y),
            new PointerFallbackOp(PointerFallbackKind.RightClick, tapB.x, tapB.y),
        ], ops);
    }

    [Fact]
    public void PointerFallbackPinchOutAndPinchInEmitZoomBeginStepsAndEnd()
    {
        var sm = new ScreenTouchStateMachine();
        var now = 1000L;
        var fb = new ScreenPointerFallback(() => now);
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        // Pinch out (spread apart)
        var n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Down, 30000, 32768, 512),
            new ScreenTouchContact(1, ScreenTouchState.Down, 35535, 32768, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Move, 25000, 32768, 512),
            new ScreenTouchContact(1, ScreenTouchState.Move, 40535, 32768, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Move, 20000, 32768, 512),
            new ScreenTouchContact(1, ScreenTouchState.Move, 45535, 32768, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Up, 20000, 32768, 512),
            new ScreenTouchContact(1, ScreenTouchState.Up, 45535, 32768, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Equal(PointerFallbackKind.ZoomBegin, ops[0].Kind);
        Assert.Contains(ops, o => o.Kind == PointerFallbackKind.ZoomStep && o.Delta > 0);
        Assert.Equal(PointerFallbackKind.ZoomEnd, ops[^1].Kind);
        Assert.DoesNotContain(ops, o => o.Kind is PointerFallbackKind.Wheel or PointerFallbackKind.HWheel or PointerFallbackKind.RightClick or PointerFallbackKind.LeftClick);

        // Pinch in (move together)
        ops.Clear();
        sm.Reset();
        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Down, 15000, 32768, 512),
            new ScreenTouchContact(1, ScreenTouchState.Down, 50535, 32768, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Move, 24000, 32768, 512),
            new ScreenTouchContact(1, ScreenTouchState.Move, 41535, 32768, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Up, 24000, 32768, 512),
            new ScreenTouchContact(1, ScreenTouchState.Up, 41535, 32768, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Equal(PointerFallbackKind.ZoomBegin, ops[0].Kind);
        Assert.Contains(ops, o => o.Kind == PointerFallbackKind.ZoomStep && o.Delta < 0);
        Assert.Equal(PointerFallbackKind.ZoomEnd, ops[^1].Kind);
    }

    [Fact]
    public void PointerFallbackOneFingerAnchoredPinchTriggersZoom()
    {
        var sm = new ScreenTouchStateMachine();
        var fb = new ScreenPointerFallback();
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        var n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Down, 20000, 32768, 512),
            new ScreenTouchContact(1, ScreenTouchState.Down, 25000, 32768, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        // Finger 0 stays anchored while Finger 1 slides outward
        n = sm.BuildFrame([new ScreenTouchContact(1, ScreenTouchState.Move, 35000, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Up, 20000, 32768, 512),
            new ScreenTouchContact(1, ScreenTouchState.Up, 35000, 32768, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Equal(PointerFallbackKind.ZoomBegin, ops[0].Kind);
        Assert.Contains(ops, o => o.Kind == PointerFallbackKind.ZoomStep && o.Delta > 0);
        Assert.Equal(PointerFallbackKind.ZoomEnd, ops[^1].Kind);
        Assert.DoesNotContain(ops, o => o.Kind is PointerFallbackKind.Wheel or PointerFallbackKind.RightClick);
    }

    [Fact]
    public void PointerFallbackThreeFingerTapEmitsMiddleClickAndSwipesEmitShortcuts()
    {
        var sm = new ScreenTouchStateMachine();
        var now = 1000L;
        var fb = new ScreenPointerFallback(() => now);
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        // 3-finger tap with staggered lift 3 -> 2 -> 1 -> 0
        var n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Down, 28000, 32768, 512),
            new ScreenTouchContact(1, ScreenTouchState.Down, 32000, 32768, 512),
            new ScreenTouchContact(2, ScreenTouchState.Down, 36000, 32768, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(2, ScreenTouchState.Up, 36000, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(1, ScreenTouchState.Up, 32000, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, 28000, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Single(ops, o => o.Kind == PointerFallbackKind.MiddleClick);
        Assert.DoesNotContain(ops, o => o.Kind is PointerFallbackKind.RightClick or PointerFallbackKind.LeftClick);

        // Swipes: left, right, up, down
        var swipeCases = new (ushort dx, ushort dy, PointerFallbackKind expected)[]
        {
            (22000, 32768, PointerFallbackKind.BrowserBack),
            (42000, 32768, PointerFallbackKind.BrowserForward),
            (32768, 18000, PointerFallbackKind.TaskView),
            (32768, 48000, PointerFallbackKind.ShowDesktop),
        };
        foreach (var (tx, ty, expected) in swipeCases)
        {
            ops.Clear();
            sm.Reset();
            now += 500;
            n = sm.BuildFrame([
                new ScreenTouchContact(0, ScreenTouchState.Down, 30000, 32768, 512),
                new ScreenTouchContact(1, ScreenTouchState.Down, 32768, 32768, 512),
                new ScreenTouchContact(2, ScreenTouchState.Down, 35536, 32768, 512),
            ], bounds, outBuf);
            fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

            now += 50;
            n = sm.BuildFrame([
                new ScreenTouchContact(0, ScreenTouchState.Move, (ushort)(tx - 2768), ty, 512),
                new ScreenTouchContact(1, ScreenTouchState.Move, tx, ty, 512),
                new ScreenTouchContact(2, ScreenTouchState.Move, (ushort)(tx + 2768), ty, 512),
            ], bounds, outBuf);
            fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

            now += 50;
            n = sm.BuildFrame([
                new ScreenTouchContact(0, ScreenTouchState.Up, (ushort)(tx - 2768), ty, 512),
                new ScreenTouchContact(1, ScreenTouchState.Up, tx, ty, 512),
                new ScreenTouchContact(2, ScreenTouchState.Up, (ushort)(tx + 2768), ty, 512),
            ], bounds, outBuf);
            fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

            Assert.Equal([new PointerFallbackOp(expected, 0, 0, HasPosition: false)], ops);
        }
    }

    [Fact]
    public void PointerFallbackSingleFingerQuickTapEmitsLeftClickOnUpAndCancelEmitsNothing()
    {
        var sm = new ScreenTouchStateMachine();
        var now = 1000L;
        var fb = new ScreenPointerFallback(() => now);
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        var (expectedX, expectedY) = ScreenTouchProtocol.MapNormalizedToDisplay(32768, 32768, bounds);

        var n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, 32768, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        Assert.Empty(ops);

        // Sub-slop 4px jitter before Up stays pending with zero ops emitted
        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Move, 32900, 32900, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        Assert.Empty(ops);

        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, 32900, 32900, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);

        Assert.Empty(ops); // the click is held back a moment
        now += ScreenPointerFallback.ClickDeferMs;
        fb.Tick(ops.Add);
        Assert.Equal([new PointerFallbackOp(PointerFallbackKind.LeftClick, expectedX, expectedY)], ops);

        // Cancel clears pending without emitting click
        ops.Clear();
        now += 500;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, 32768, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        Assert.Empty(ops);

        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Cancel, 32768, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        Assert.Empty(ops);
    }

    [Fact]
    public void PointerFallbackReleaseAllClearsPendingTapLeftDownAndActiveZoom()
    {
        var sm = new ScreenTouchStateMachine();
        var now = 1000L;
        var fb = new ScreenPointerFallback(() => now);
        var bounds = new DisplayBounds(1920, 0, 1600, 720);
        Span<ResolvedTouchContact> outBuf = stackalloc ResolvedTouchContact[ScreenTouchProtocol.MaxPointers];
        var ops = new List<PointerFallbackOp>();

        fb.ReleaseAll(ops.Add);
        Assert.Empty(ops);

        // ReleaseAll after Down emits nothing
        var n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, 32768, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        fb.ReleaseAll(ops.Add);
        Assert.Empty(ops);

        // Subsequent Up is ignored cleanly
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, 32768, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        Assert.Empty(ops);

        // Tap-and-drag engages LeftDown
        sm.Reset();
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, 32768, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Up, 32768, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 150;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Down, 32768, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        now += 40;
        n = sm.BuildFrame([new ScreenTouchContact(0, ScreenTouchState.Move, 36000, 32768, 512)], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        ops.Clear();

        fb.ReleaseAll(ops.Add);
        Assert.Equal([new PointerFallbackOp(PointerFallbackKind.LeftUp, 0, 0, HasPosition: false)], ops);

        ops.Clear();
        fb.ReleaseAll(ops.Add);
        Assert.Empty(ops);

        // Active pinch emits ZoomEnd once on ReleaseAll
        sm.Reset();
        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Down, 30000, 32768, 512),
            new ScreenTouchContact(1, ScreenTouchState.Down, 35535, 32768, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        n = sm.BuildFrame([
            new ScreenTouchContact(0, ScreenTouchState.Move, 22000, 32768, 512),
            new ScreenTouchContact(1, ScreenTouchState.Move, 43535, 32768, 512),
        ], bounds, outBuf);
        fb.ProcessBatch(outBuf.Slice(0, n), ops.Add);
        ops.Clear();

        fb.ReleaseAll(ops.Add);
        Assert.Equal([new PointerFallbackOp(PointerFallbackKind.ZoomEnd, 0, 0, HasPosition: false)], ops);
        ops.Clear();
        fb.ReleaseAll(ops.Add);
        Assert.Empty(ops);
    }

    [Fact]
    public void ScreenTouchHttpEndpointIsAllowedOverLanAndUsb()
    {
        Assert.True(Lan.AllowedOverLan(new PathString("/api/screen/touch")));
        Assert.True(UsbLink.Allowed("POST", new PathString("/api/screen/touch")));
    }
}
