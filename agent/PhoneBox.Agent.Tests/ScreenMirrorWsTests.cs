using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

public sealed class ScreenMirrorWsTests
{
    [Fact]
    public void WebSocketScreenEndpointIsAllowedOverLanAndUsb()
    {
        Assert.True(Lan.AllowedOverLan(new PathString("/api/screen/ws")));
        Assert.True(UsbLink.Allowed("GET", new PathString("/api/screen/ws")));
    }

    [Theory]
    [InlineData(new[] { "h264", "hevc" }, true, true, false, false, false)]
    [InlineData(new[] { "h264", "hevc" }, false, false, false, false, true)]
    [InlineData(new[] { "h264", "hevc" }, false, false, true, true, false)]
    [InlineData(new[] { "h264", "hevc" }, true, false, true, true, false)]
    [InlineData(new[] { "h264", "hevc" }, true, true, true, true, false)]
    [InlineData(new[] { "h264" }, true, true, true, false, false)]
    [InlineData(new[] { "hevc" }, false, false, true, true, false)]
    public void NegotiateCodecFollowsLowLatencyAndOptInRules(
        string[] codecs, bool h264LowLat, bool hevcLowLat, bool preferHevc,
        bool expectedHevc, bool expectedBaseline)
    {
        var (useHevc, useBaseline) = ScreenMirror.NegotiateCodec(codecs, h264LowLat, hevcLowLat, preferHevc);
        Assert.Equal(expectedHevc, useHevc);
        Assert.Equal(expectedBaseline, useBaseline);
    }

    [Fact]
    public void BackpressureGateEvictsStaleFramesAndRequestsIdrWithoutDeadlock()
    {
        long fakeNow = 0;
        var gate = new FrameBackpressureGate<string>(
            capacity: 2,
            idrRetryCooldown: TimeSpan.FromMilliseconds(150),
            timestampProvider: () => fakeNow);

        var idrRequests = 0;
        var disposed = new List<string>();
        void ReqIdr() => idrRequests++;
        void Dispose(string s) => disposed.Add(s);

        // Fill capacity with P-frames
        Assert.True(gate.Enqueue("p1", isIdr: false, ReqIdr, Dispose));
        Assert.True(gate.Enqueue("p2", isIdr: false, ReqIdr, Dispose));
        Assert.Equal(0, idrRequests);

        // 3rd P-frame overflows -> drops p3, evicts p1 & p2, demands IDR
        Assert.False(gate.Enqueue("p3", isIdr: false, ReqIdr, Dispose));
        Assert.Equal(1, idrRequests);
        Assert.Equal(["p3", "p1", "p2"], disposed);

        // Next P-frame before cooldown is dropped without spamming IDR
        fakeNow += (long)(0.05 * Stopwatch.Frequency);
        Assert.False(gate.Enqueue("p4", isIdr: false, ReqIdr, Dispose));
        Assert.Equal(1, idrRequests);

        // After 150ms cooldown without an IDR arriving, retries IDR request (no permanent stall!)
        fakeNow += (long)(0.16 * Stopwatch.Frequency);
        Assert.False(gate.Enqueue("p5", isIdr: false, ReqIdr, Dispose));
        Assert.Equal(2, idrRequests);

        // IDR arrives -> resets demand and accepts subsequent P-frames
        Assert.True(gate.Enqueue("idr1", isIdr: true, ReqIdr, Dispose));
        Assert.True(gate.Enqueue("p6", isIdr: false, ReqIdr, Dispose));
        Assert.True(gate.Reader.TryRead(out var out1));
        Assert.True(gate.Reader.TryRead(out var out2));
        Assert.Equal("idr1", out1);
        Assert.Equal("p6", out2);
    }

    [Fact]
    public void BackpressureGateEvictsQueuedPFramesSoFreshIdrAlwaysGetsDelivered()
    {
        var gate = new FrameBackpressureGate<string>(capacity: 2);
        var idrRequests = 0;
        var disposed = new List<string>();

        Assert.True(gate.Enqueue("p1", isIdr: false, () => idrRequests++, s => disposed.Add(s)));
        Assert.True(gate.Enqueue("p2", isIdr: false, () => idrRequests++, s => disposed.Add(s)));
        // Fresh IDR arrives while channel has p1 & p2 -> must evict p1 & p2 and enqueue idr1!
        Assert.True(gate.Enqueue("idr1", isIdr: true, () => idrRequests++, s => disposed.Add(s)));
        Assert.Equal(["p1", "p2"], disposed);
        Assert.True(gate.Reader.TryRead(out var f));
        Assert.Equal("idr1", f);
    }

#if WINDOWS
    [Fact]
    public void EnsureVddResolutionPinsNvidiaGpuAndUpgradesRefreshRateTo60()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-vdd-");
        var path = Path.Combine(dir.FullName, "vdd_settings.xml");
        try
        {
            File.WriteAllText(path, """
                <?xml version="1.0" encoding="UTF-8"?>
                <vdd_settings>
                  <monitors><count>1</count></monitors>
                  <gpu><friendlyname>default</friendlyname></gpu>
                  <resolutions>
                    <resolution><width>1600</width><height>720</height><refresh_rate>30</refresh_rate></resolution>
                  </resolutions>
                </vdd_settings>
                """);

            var count = ScreenMirror.EnsureVddResolution(path, 1600, 720, "NVIDIA GeForce GTX 1650 Ti");
            Assert.Equal(1, count);

            var doc = XDocument.Load(path);
            Assert.Equal("NVIDIA GeForce GTX 1650 Ti", doc.Root?.Element("gpu")?.Element("friendlyname")?.Value);
            var res = doc.Root?.Element("resolutions")?.Elements("resolution").Single();
            Assert.Equal("60", res?.Element("refresh_rate")?.Value);
        }
        finally
        {
            Directory.Delete(dir.FullName, true);
        }
    }
#endif

    [Theory]
    [InlineData(4800, 2160, 1600, 720, 1920, 864, false, 1600u, 720u)]
    [InlineData(3200, 1440, 1600, 720, 1920, 864, false, 1600u, 720u)]
    [InlineData(2400, 1080, 1600, 720, 1920, 864, false, 1600u, 720u)]
    [InlineData(1066, 480, 1600, 720, 1920, 864, false, 1066u, 480u)]
    [InlineData(3200, 1440, 2400, 1080, 1920, 864, false, 1920u, 864u)]
    [InlineData(4800, 2160, 0, 0, 0, 0, false, 4096u, 1842u)]
    [InlineData(2400, 1080, 720, 1600, 864, 1920, true, 1600u, 720u)]
    public void ComputeEncodeSizeClampsToPhoneDecoderAndHardwareLimits(
        int vddW, int vddH, int scrW, int scrH, int safeW, int safeH, bool hevc,
        uint expectedW, uint expectedH)
    {
        var (w, h) = ScreenMirror.ComputeEncodeSize(vddW, vddH, scrW, scrH, safeW, safeH, hevc);
        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);
    }

    [Theory]
    [InlineData(1066u, 480u, true, false, 6_000_000u, 9_000_000u)]
    [InlineData(1600u, 720u, true, false, 10_000_000u, 15_000_000u)]
    [InlineData(1600u, 720u, true, true, 8_000_000u, 12_000_000u)]
    [InlineData(2400u, 1080u, true, false, 14_000_000u, 21_000_000u)]
    [InlineData(3200u, 1440u, true, false, 18_000_000u, 26_000_000u)]
    [InlineData(1600u, 720u, false, false, 6_500_000u, 9_750_000u)]
    public void ComputeStreamBitrateScalesWithResolutionTransportAndCodec(
        uint w, uint h, bool isUsb, bool hevc, uint expectedBr, uint expectedMax)
    {
        var (br, maxBr) = ScreenMirror.ComputeStreamBitrate(w, h, isUsb, hevc, 0, 0);
        Assert.Equal(expectedBr, br);
        Assert.Equal(expectedMax, maxBr);
    }

    [Fact]
    public void AdaptiveScaleControllerStepsDownAndRecoversWithinFloor()
    {
        long fakeNow = 0;
        var ctrl = new ScreenMirror.AdaptiveScaleController(1600, 720, () => fakeNow);

        // Single bad window does not trigger step-down
        Assert.False(ctrl.OnTelemetry(30, 45, 0, out _, out _));
        // Second bad window triggers step-down to tier 1 (0.85 -> 1360x612)
        Assert.True(ctrl.OnTelemetry(28, 42, 0, out var w1, out var h1));
        Assert.Equal(1360u, w1);
        Assert.Equal(612u, h1);

        // Within 2s cooldown: ignored even if bad
        fakeNow += (long)(1.0 * Stopwatch.Frequency);
        Assert.False(ctrl.OnTelemetry(30, 45, 2, out _, out _));
        Assert.False(ctrl.OnTelemetry(30, 45, 2, out _, out _));

        // After 2s cooldown + 2 bad windows -> tier 2 (0.72 -> 1152x518)
        fakeNow += (long)(2.5 * Stopwatch.Frequency);
        Assert.False(ctrl.OnTelemetry(30, 45, 0, out _, out _));
        Assert.True(ctrl.OnTelemetry(30, 45, 0, out var w2, out var h2));
        Assert.Equal(1152u, w2);
        Assert.Equal(518u, h2);

        // Next tiers (0.60 -> h=432, 0.50 -> h=360) are below 480p floor -> stays clamped
        fakeNow += (long)(3.0 * Stopwatch.Frequency);
        Assert.False(ctrl.OnTelemetry(35, 50, 4, out _, out _));
        Assert.False(ctrl.OnTelemetry(35, 50, 4, out _, out _));

        // 10 good windows after 4s cooldown step back up to tier 1 (1360x612)
        fakeNow += (long)(4.5 * Stopwatch.Frequency);
        for (var i = 0; i < 9; i++) Assert.False(ctrl.OnTelemetry(6, 10, 0, out _, out _));
        Assert.True(ctrl.OnTelemetry(6, 10, 0, out var wUp, out var hUp));
        Assert.Equal(1360u, wUp);
        Assert.Equal(612u, hUp);
    }

    [Fact]
    public void WifiBitrateControllerStepsDownOnStallDropsAndRecoversWithFloor()
    {
        long fakeNow = 0;
        var ctrl = new ScreenMirror.WifiBitrateController(5_200_000u, () => fakeNow);
        Assert.Equal(5_200_000u, ctrl.CurrentBitrate);
        Assert.Equal(6_500_000u, ctrl.CurrentMaxBitrate);

        // Single mild congestion sample does not reduce bitrate immediately
        Assert.False(ctrl.OnFrameSent(26.0, 5.0, out _, out _));
        // Second consecutive congested sample steps down to 72% (3_744_000)
        Assert.True(ctrl.OnFrameSent(28.0, 5.0, out var br1, out var max1));
        Assert.Equal(3_744_000u, br1);
        Assert.Equal(4_680_000u, max1);

        // Inside 1.5s cooldown: ignored even on severe stall or client drop
        fakeNow += (long)(0.8 * Stopwatch.Frequency);
        Assert.False(ctrl.OnFrameSent(80.0, 45.0, out _, out _));
        Assert.False(ctrl.OnClientDrop(4, out _, out _));

        // After cooldown, client drops >= 2 step down further (78%)
        fakeNow += (long)(1.6 * Stopwatch.Frequency);
        Assert.True(ctrl.OnClientDrop(3, out var br2, out _));
        Assert.Equal(2_920_320u, br2);

        // Severe stalls repeatedly clamp cleanly at floor (~1_733_333)
        for (var i = 0; i < 6; i++)
        {
            fakeNow += (long)(1.6 * Stopwatch.Frequency);
            ctrl.OnFrameSent(65.0, 50.0, out _, out _);
        }
        Assert.Equal(5_200_000u / 3u, ctrl.CurrentBitrate);

        // Healthy frames for >4s step up by 350_000
        var floor = ctrl.CurrentBitrate;
        Assert.False(ctrl.OnFrameSent(5.0, 2.0, out _, out _));
        fakeNow += (long)(4.1 * Stopwatch.Frequency);
        Assert.True(ctrl.OnFrameSent(5.0, 2.0, out var brUp, out var maxUp));
        Assert.Equal(floor + 350_000u, brUp);
        Assert.Equal(brUp * 5u / 4u, maxUp);
    }

    [Fact]
    public void VddDetachDelayIs30SecondsAndRemainingDelayClampsCleanly()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ScreenMirror.VddDetachDelay);
        Assert.Equal(TimeSpan.FromSeconds(24.5),
            ScreenMirror.ComputeRemainingDetachDelay(TimeSpan.FromSeconds(5.5), TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.Zero,
            ScreenMirror.ComputeRemainingDetachDelay(TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task VddDetachSchedulerDetachesAfterDelayAndDoesNotExtendOnDuplicateSchedule()
    {
        var gate = new object();
        var detachCount = 0;
        var detachedEvent = new ManualResetEventSlim(false);
        using var scheduler = new ScreenMirror.VddDetachScheduler(
            gate,
            TimeSpan.FromMilliseconds(120),
            canDetach: () => true,
            detach: () =>
            {
                Interlocked.Increment(ref detachCount);
                detachedEvent.Set();
            });

        lock (gate) scheduler.ScheduleLocked();
        Assert.True(scheduler.IsPending);

        await Task.Delay(30);
        Assert.Equal(0, Volatile.Read(ref detachCount));

        // Duplicate stop must keep the original countdown instead of extending it.
        lock (gate) scheduler.ScheduleLocked();
        Assert.True(detachedEvent.Wait(TimeSpan.FromMilliseconds(220)));
        Assert.Equal(1, Volatile.Read(ref detachCount));
        Assert.False(scheduler.IsPending);
    }

    [Fact]
    public async Task VddDetachSchedulerCancelPreventsDetachAndDetachNowRunsOnceOnDispose()
    {
        var gate = new object();
        var canDetach = true;
        var detachCount = 0;
        using var scheduler = new ScreenMirror.VddDetachScheduler(
            gate,
            TimeSpan.FromMilliseconds(100),
            canDetach: () => canDetach,
            detach: () => Interlocked.Increment(ref detachCount));

        lock (gate) scheduler.ScheduleLocked();
        Assert.True(scheduler.IsPending);
        lock (gate) scheduler.CancelLocked();
        Assert.False(scheduler.IsPending);
        await Task.Delay(180);
        Assert.Equal(0, Volatile.Read(ref detachCount));

        // canDetach == false skips scheduling.
        canDetach = false;
        lock (gate) scheduler.ScheduleLocked();
        Assert.False(scheduler.IsPending);
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref detachCount));

        // Flipping canDetach to false before timer expires also prevents detach.
        canDetach = true;
        lock (gate) scheduler.ScheduleLocked();
        Assert.True(scheduler.IsPending);
        canDetach = false;
        await Task.Delay(180);
        Assert.Equal(0, Volatile.Read(ref detachCount));
        Assert.False(scheduler.IsPending);

        // DetachNowLocked(markDisposed: true) runs once immediately and ignores repeats.
        canDetach = true;
        lock (gate)
        {
            scheduler.DetachNowLocked(markDisposed: true);
            scheduler.DetachNowLocked(markDisposed: true);
        }
        Assert.Equal(1, Volatile.Read(ref detachCount));
    }
}
