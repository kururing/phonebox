using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace PhoneBox.Agent;

public sealed partial class ScreenMirror
{
    public Task<bool> StreamWebSocketAsync(HttpContext context)
    {
#if WINDOWS
        return StreamWebSocketWindowsAsync(context);
#else
        return Task.FromResult(false);
#endif
    }

#if WINDOWS
    private Task? _activeVideoDone;

    private async Task<bool> StreamWebSocketWindowsAsync(HttpContext context)
    {
        var requestToken = context.RequestAborted;
        NvidiaCaptureTarget target;
        CancellationTokenSource session;
        CancellationTokenSource? previousVideo = null;
        Task? previousDone = null;

        lock (_gate)
        {
            if (!_active || _captureTarget is not { } selected) return false;
            target = selected;
            previousVideo = _activeVideo;
            previousDone = _activeVideoDone;
            session = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
            _activeVideo = session;
            _lastRequest = Stopwatch.GetTimestamp();
        }

        previousVideo?.Cancel();
        if (previousDone is not null)
        {
            try { await previousDone.WaitAsync(TimeSpan.FromSeconds(1), requestToken); }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            {
                ReleaseSession(session);
                return true;
            }
            catch { }
        }

        var doneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!ReferenceEquals(_activeVideo, session))
            {
                doneTcs.TrySetResult();
                session.Dispose();
                return true;
            }
            _activeVideoDone = doneTcs.Task;
        }

        try
        {
            using var ws = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                DangerousEnableCompression = false,
            });

            var hsBuf = new byte[4096];
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            hsCts.CancelAfter(TimeSpan.FromSeconds(5));
            var hsRes = await ws.ReceiveAsync(hsBuf, hsCts.Token);
            if (hsRes.MessageType == WebSocketMessageType.Close) return true;

            var hs = JsonSerializer.Deserialize<ScreenMirrorHandshake>(hsBuf.AsSpan(0, hsRes.Count),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

            var (useHevc, useBaseline) = NegotiateCodec(hs.Codecs, hs.H264LowLatency, hs.HevcLowLatency, hs.PreferHevc);
            var fps = Math.Clamp(hs.Fps > 0 ? hs.Fps : 60u, 15u, 60u);
            var isUsb = string.Equals(hs.Route, "usb", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(hs.Route) && IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None));
            var safeW = useHevc ? hs.HevcSafeWidth : hs.H264SafeWidth;
            var safeH = useHevc ? hs.HevcSafeHeight : hs.H264SafeHeight;
            var (encW, encH) = ComputeEncodeSize(target.Width, target.Height, hs.ScreenWidth, hs.ScreenHeight, safeW, safeH, useHevc);
            var (bitrate, maxBitrate) = ComputeStreamBitrate(encW, encH, isUsb, useHevc, hs.Bitrate, hs.MaxBitrate);
            if (!isUsb)
            {
                maxBitrate = Math.Min(maxBitrate, Math.Max(bitrate, bitrate * 5u / 4u));
            }

            var cfg = new PbCapConfig
            {
                AdapterIndex = (uint)target.AdapterIndex,
                OutputIndex = (uint)target.OutputIndex,
                SourceMode = (uint)PbSourceMode.DxgiOutput,
                MaxFps = fps,
                BitrateBps = bitrate,
                MaxBitrateBps = maxBitrate,
                Codec = (uint)(useHevc ? PbCodec.Hevc : PbCodec.H264),
                DrawCursor = 1,
                IntraRefresh = 1,
                H264Baseline = useBaseline ? 1u : 0u,
                EncodeWidth = encW,
                EncodeHeight = encH,
            };

            NativeCaptureSession native;
            try
            {
                native = new NativeCaptureSession(cfg);
            }
            catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
            {
                Fail("Không khởi động được bộ mã hóa native NVENC: " + ex.Message);
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.InternalServerError, "native-unavailable", CancellationToken.None); }
                catch { }
                return true;
            }

            using (native)
            {
                var initStats = native.GetStats();
                _log?.LogInformation(
                    "ScreenMirror stream: {Codec} vdd={VddW}x{VddH} enc={Width}x{Height}@{Fps} bitrate={Bitrate}/{MaxBitrate} usb={IsUsb} adaptive={Adaptive} client={Client}",
                    useHevc ? "HEVC-Main" : (useBaseline ? "H264-Baseline" : "H264-High"),
                    target.Width, target.Height, initStats.Width, initStats.Height, fps, bitrate, maxBitrate, isUsb, hs.AdaptivePlayback, hs.ClientModel ?? "");
                var meta = BuildStreamMeta(initStats.Width, initStats.Height, useHevc, useBaseline, fps, bitrate);

                using var wsSendLock = new SemaphoreSlim(1, 1);
                if (!await SendWithTimeoutAsync(ws, meta, wsSendLock, session.Token)) return true;

                native.Start();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                var adaptive = new AdaptiveScaleController(initStats.Width, initStats.Height);
                var wifiCtrl = isUsb ? null : new WifiBitrateController(bitrate);
                var wifiCtrlLock = new object();
                var lastSentW = initStats.Width;
                var lastSentH = initStats.Height;
                var currentBitrate = bitrate;
                var audioMode = hs.Audio ? ScreenAudioProtocol.ParseAudioMode(hs.AudioMode) : ScreenAudioMode.Off;
                if (audioMode != ScreenAudioMode.Virtual || !hs.AudioAutoSwitch)
                    RestoreDefaultAudioIfSwitched();
                var audioTask = audioMode != ScreenAudioMode.Off
                    ? StreamAudioLoopAsync(ws, wsSendLock, audioMode, hs.AudioDevice, hs.AudioAutoSwitch, linked.Token)
                    : Task.CompletedTask;
                long lastPhoneTouchTick = 0;
                long phoneTouchSeq = 0;
                double lastTouchX = -1, lastTouchY = -1;
                var touchPosLock = new object();
                var inputFocusTask = WatchTextInputFocusAsync(
                    ws, wsSendLock,
                    () => { lock (touchPosLock) return (phoneTouchSeq, lastPhoneTouchTick, lastTouchX, lastTouchY); },
                    linked.Token);

                var recvTask = Task.Run(async () =>
                {
                    var rbuf = new byte[512];
                    var pong = new byte[9];
                    pong[0] = 0x21;
                    try
                    {
                        while (!linked.IsCancellationRequested && ws.State == WebSocketState.Open)
                        {
                            var r = await ws.ReceiveAsync(rbuf, linked.Token);
                            if (r.MessageType == WebSocketMessageType.Close) break;
                            lock (_gate) { _lastRequest = Stopwatch.GetTimestamp(); }
                            if (r.Count < 1) continue;

                            switch (rbuf[0])
                            {
                                case 0x10:
                                    native.RequestIdr();
                                    break;
                                case 0x11 when r.Count >= 5:
                                    var br = Math.Clamp(BitConverter.ToUInt32(rbuf, 1), 500_000u, 35_000_000u);
                                    var maxBr = Math.Clamp(Math.Max(br * 3u / 2u, br), br, 40_000_000u);
                                    Volatile.Write(ref currentBitrate, br);
                                    native.SetBitrate(br, maxBr);
                                    break;
                                case 0x20 when r.Count >= 9:
                                    Buffer.BlockCopy(rbuf, 1, pong, 1, 8);
                                    if (!await SendWithTimeoutAsync(ws, pong, wsSendLock, linked.Token)) return;
                                    break;
                                case 0x23 when r.Count >= 11:
                                    var decP50 = BinaryPrimitives.ReadUInt16LittleEndian(rbuf.AsSpan(1, 2));
                                    var decP95 = BinaryPrimitives.ReadUInt16LittleEndian(rbuf.AsSpan(3, 2));
                                    var fpsX10 = BinaryPrimitives.ReadUInt16LittleEndian(rbuf.AsSpan(5, 2));
                                    var dropped = BinaryPrimitives.ReadUInt16LittleEndian(rbuf.AsSpan(7, 2));
                                    if (hs.AdaptivePlayback && adaptive.OnTelemetry(decP50, decP95, dropped, out var nextW, out var nextH))
                                    {
                                        var (newBr, newMaxBr) = ComputeStreamBitrate(nextW, nextH, isUsb, useHevc, hs.Bitrate, hs.MaxBitrate);
                                        if (!isUsb) newMaxBr = Math.Min(newMaxBr, Math.Max(newBr, newBr * 5u / 4u));
                                        if (wifiCtrl is not null) lock (wifiCtrlLock) wifiCtrl.ResetBase(newBr);
                                        Volatile.Write(ref currentBitrate, newBr);
                                        native.SetEncodeSize(nextW, nextH);
                                        native.SetBitrate(newBr, newMaxBr);
                                        _log?.LogInformation(
                                            "ScreenMirror adaptive resize -> {NextW}x{NextH} bitrate={Bitrate}/{MaxBitrate} (decP50={P50}ms decP95={P95}ms fps={Fps:F1} drop={Drop})",
                                            nextW, nextH, newBr, newMaxBr, decP50, decP95, fpsX10 / 10.0, dropped);
                                    }
                                    else if (wifiCtrl is not null)
                                    {
                                        bool dropAdj;
                                        uint dropBr = 0, dropMax = 0;
                                        lock (wifiCtrlLock) dropAdj = wifiCtrl.OnClientDrop(dropped, out dropBr, out dropMax);
                                        if (dropAdj)
                                        {
                                            Volatile.Write(ref currentBitrate, dropBr);
                                            native.SetBitrate(dropBr, dropMax);
                                            _log?.LogInformation(
                                                "ScreenMirror wifi bitrate (drop={Drop}) -> {Bitrate}/{MaxBitrate}",
                                                dropped, dropBr, dropMax);
                                        }
                                    }
                                    break;
                                case ScreenTouchProtocol.OpcodeTouch:
                                    if (r.Count >= 10 && rbuf[1] >= 1)
                                    {
                                        var xU16 = BinaryPrimitives.ReadUInt16LittleEndian(rbuf.AsSpan(4, 2));
                                        var yU16 = BinaryPrimitives.ReadUInt16LittleEndian(rbuf.AsSpan(6, 2));
                                        var px = target.Bounds.Left + (xU16 / 65535.0) * target.Bounds.Width;
                                        var py = target.Bounds.Top + (yU16 / 65535.0) * target.Bounds.Height;
                                        lock (touchPosLock)
                                        {
                                            lastPhoneTouchTick = Stopwatch.GetTimestamp();
                                            phoneTouchSeq++;
                                            lastTouchX = px;
                                            lastTouchY = py;
                                        }
                                    }
                                    InjectTouchPacket(rbuf.AsSpan(0, r.Count), target.Bounds);
                                    break;
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        _touch.ReleaseAll();
                        linked.Cancel();
                    }
                });

                try
                {
                    var lastSkipIdrTick = long.MinValue;
                    var skipIdrCooldownTicks = (long)(0.30 * Stopwatch.Frequency);
                    void TrackWifiSend(double sendMs, double queueAgeMs)
                    {
                        if (wifiCtrl is null) return;
                        bool adj;
                        uint adjBr, adjMax;
                        lock (wifiCtrlLock) adj = wifiCtrl.OnFrameSent(sendMs, queueAgeMs, out adjBr, out adjMax);
                        if (!adj) return;
                        Volatile.Write(ref currentBitrate, adjBr);
                        native.SetBitrate(adjBr, adjMax);
                        _log?.LogInformation(
                            "ScreenMirror wifi bitrate -> {Bitrate}/{MaxBitrate} (send={SendMs:F1}ms age={AgeMs:F1}ms)",
                            adjBr, adjMax, sendMs, queueAgeMs);
                    }
                    await foreach (var frame in native.Frames.ReadAllAsync(linked.Token))
                    {
                        using (frame)
                        {
                            if (ws.State != WebSocketState.Open) break;
                            lock (_gate)
                            {
                                if (!_active) break;
                                _lastRequest = Stopwatch.GetTimestamp();
                            }
                            var isIdr = (frame.Flags & PbFrameFlags.Idr) != 0;
                            var queueAgeMs = frame.EnqueuedTimestamp > 0
                                ? Stopwatch.GetElapsedTime(frame.EnqueuedTimestamp).TotalMilliseconds
                                : 0.0;
                            if (!isUsb && !isIdr && queueAgeMs > 40.0)
                            {
                                var nowTick = Stopwatch.GetTimestamp();
                                if (lastSkipIdrTick == long.MinValue || (nowTick - lastSkipIdrTick) >= skipIdrCooldownTicks)
                                {
                                    lastSkipIdrTick = nowTick;
                                    native.RequestIdr();
                                }
                                TrackWifiSend(0.0, queueAgeMs);
                                continue;
                            }
                            if (isIdr && frame.Width > 0 && frame.Height > 0 &&
                                ((uint)frame.Width != lastSentW || (uint)frame.Height != lastSentH))
                            {
                                lastSentW = (uint)frame.Width;
                                lastSentH = (uint)frame.Height;
                                var updatedMeta = BuildStreamMeta(lastSentW, lastSentH, useHevc, useBaseline, fps, Volatile.Read(ref currentBitrate));
                                if (!await SendWithTimeoutAsync(ws, updatedMeta, wsSendLock, linked.Token))
                                    break;
                            }
                            var sendStart = Stopwatch.GetTimestamp();
                            if (!await SendWithTimeoutAsync(ws, frame.Buffer.AsMemory(0, frame.Length), wsSendLock, linked.Token))
                                break;
                            TrackWifiSend(Stopwatch.GetElapsedTime(sendStart).TotalMilliseconds, queueAgeMs);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
                catch (IOException) { }
                finally
                {
                    linked.Cancel();
                    native.Stop();
                    await recvTask;
                    try { await audioTask; } catch { }
                    try { await inputFocusTask; } catch { }
                }
            }
            return true;
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            return true;
        }
        catch (WebSocketException)
        {
            return true;
        }
        finally
        {
            ReleaseSession(session, doneTcs);
            ScheduleIdleCheck();
        }
    }

    private static async Task<bool> SendWithTimeoutAsync(
        WebSocket ws, ReadOnlyMemory<byte> payload, SemaphoreSlim sendLock, CancellationToken ct)
    {
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendCts.CancelAfter(5000);
        try
        {
            await sendLock.WaitAsync(sendCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        try
        {
            if (ws.State != WebSocketState.Open) return false;
            await ws.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, sendCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            sendLock.Release();
        }
    }

    private void ReleaseSession(CancellationTokenSource session, TaskCompletionSource? doneTcs = null)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeVideo, session))
            {
                _activeVideo = null;
                _activeVideoDone = null;
            }
            _lastRequest = Stopwatch.GetTimestamp();
        }
        doneTcs?.TrySetResult();
        session.Dispose();
    }

    private static byte[] BuildStreamMeta(uint width, uint height, bool useHevc, bool useBaseline, uint fps, uint bitrate)
    {
        var meta = new byte[12];
        meta[0] = 0x02;
        BitConverter.TryWriteBytes(meta.AsSpan(1, 2), (ushort)width);
        BitConverter.TryWriteBytes(meta.AsSpan(3, 2), (ushort)height);
        meta[5] = (byte)((useHevc ? 1 : 0) | (useBaseline ? 2 : 0));
        BitConverter.TryWriteBytes(meta.AsSpan(6, 2), (ushort)fps);
        BitConverter.TryWriteBytes(meta.AsSpan(8, 4), bitrate);
        return meta;
    }

    private async Task StreamAudioLoopAsync(
        WebSocket ws,
        SemaphoreSlim wsSendLock,
        ScreenAudioMode mode,
        string? configuredDeviceId,
        bool autoSwitch,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var inventory = ListAudioOutputs();
                var endpoint = ScreenAudioProtocol.SelectAudioEndpoint(mode, configuredDeviceId, inventory.Devices);
                if (endpoint is null)
                {
                    _log?.LogWarning("ScreenMirror audio disabled: no matching output endpoint for mode={Mode}", mode);
                    var offMeta = ScreenAudioProtocol.BuildAudioMeta(active: false, isVirtual: false);
                    await SendWithTimeoutAsync(ws, offMeta, wsSendLock, ct);
                    return;
                }

                if (mode == ScreenAudioMode.Virtual && autoSwitch)
                    EnsureVirtualAudioAutoSwitch(endpoint.Id);

                ScreenAudioCaptureSession capture;
                try
                {
                    capture = new ScreenAudioCaptureSession(endpoint.Id, _log, followEndpointVolume: endpoint.IsVirtual);
                }
                catch (Exception ex)
                {
                    _log?.LogWarning("ScreenMirror audio open failed on '{Device}': {Msg}", endpoint.Name, ex.Message);
                    var offMeta = ScreenAudioProtocol.BuildAudioMeta(active: false, isVirtual: endpoint.IsVirtual);
                    await SendWithTimeoutAsync(ws, offMeta, wsSendLock, ct);
                    return;
                }

                using (capture)
                {
                    var onMeta = ScreenAudioProtocol.BuildAudioMeta(active: true, isVirtual: endpoint.IsVirtual);
                    if (!await SendWithTimeoutAsync(ws, onMeta, wsSendLock, ct))
                        return;

                    using var reopenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var switchTask = mode == ScreenAudioMode.Default
                        ? WatchDefaultAudioChangeAsync(endpoint.Id, reopenCts, ct)
                        : Task.CompletedTask;

                    var reopenRequested = false;
                    try
                    {
                        await foreach (var pkt in capture.Packets.ReadAllAsync(reopenCts.Token))
                        {
                            using (pkt)
                            {
                                if (ws.State != WebSocketState.Open || ct.IsCancellationRequested)
                                    return;
                                if (Stopwatch.GetElapsedTime(pkt.EnqueuedTimestamp).TotalMilliseconds > 100.0)
                                    continue;
                                if (!await SendWithTimeoutAsync(ws, pkt.Buffer.AsMemory(0, pkt.Length), wsSendLock, ct))
                                    return;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && reopenCts.IsCancellationRequested)
                    {
                        reopenRequested = true;
                    }
                    finally
                    {
                        reopenCts.Cancel();
                        try { await switchTask; } catch { }
                    }

                    if (!reopenRequested)
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _log?.LogDebug("ScreenMirror audio stream stopped: {Msg}", ex.Message);
        }
    }

    private async Task WatchDefaultAudioChangeAsync(
        string activeDeviceId,
        CancellationTokenSource reopenCts,
        CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var currentDefault = DefaultRenderDeviceId();
                if (!string.IsNullOrEmpty(currentDefault) &&
                    !string.Equals(currentDefault, activeDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    _log?.LogInformation("ScreenMirror audio default output changed; switching capture");
                    reopenCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task WatchTextInputFocusAsync(
        WebSocket ws,
        SemaphoreSlim wsSendLock,
        Func<(long Seq, long Tick, double X, double Y)> getLastTouch,
        CancellationToken ct)
    {
        try
        {
            var kbOpen = false;
            var handledTouchSeq = 0L;
            var pkt = new byte[2];
            pkt[0] = 0x24;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(150));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (ws.State != WebSocketState.Open) return;
                var focused = IsTextInputFocused(out var l, out var t, out var r, out var b, out var hasBounds);
                if (kbOpen)
                {
                    if (!focused)
                    {
                        kbOpen = false;
                        pkt[1] = 0;
                        if (!await SendWithTimeoutAsync(ws, pkt, wsSendLock, ct)) return;
                    }
                    continue;
                }
                if (!focused) continue;
                var (touchSeq, touchTick, tx, ty) = getLastTouch();
                if (touchSeq != handledTouchSeq && touchTick > 0 &&
                    Stopwatch.GetElapsedTime(touchTick).TotalMilliseconds <= 1000)
                {
                    handledTouchSeq = touchSeq;
                    const double pad = 16.0;
                    var hitInput = !hasBounds || (tx >= l - pad && tx <= r + pad && ty >= t - pad && ty <= b + pad);
                    if (hitInput)
                    {
                        kbOpen = true;
                        pkt[1] = 1;
                        if (!await SendWithTimeoutAsync(ws, pkt, wsSendLock, ct)) return;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (IOException) { }
    }

    private sealed record ScreenMirrorHandshake(
        string[]? Codecs = null,
        bool H264LowLatency = false,
        bool HevcLowLatency = false,
        bool PreferHevc = false,
        uint Fps = 60,
        uint Bitrate = 6_000_000,
        uint MaxBitrate = 10_000_000,
        string? ClientModel = null,
        int ScreenWidth = 0,
        int ScreenHeight = 0,
        int H264SafeWidth = 0,
        int H264SafeHeight = 0,
        int HevcSafeWidth = 0,
        int HevcSafeHeight = 0,
        bool AdaptivePlayback = false,
        string? Route = null,
        bool Audio = false,
        string? AudioMode = null,
        string? AudioDevice = null,
        bool AudioAutoSwitch = false);
#endif
}
