using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace PhoneBox.Agent;

internal sealed class FrameBackpressureGate<T>
{
    private readonly Channel<T> _channel;
    private readonly Func<long> _timestamp;
    private readonly long _retryTicks;
    private int _needIdr;
    private long _lastIdrReqTick = long.MinValue;

    public ChannelReader<T> Reader => _channel.Reader;

    public FrameBackpressureGate(int capacity = 2, TimeSpan? idrRetryCooldown = null, Func<long>? timestampProvider = null)
    {
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _timestamp = timestampProvider ?? Stopwatch.GetTimestamp;
        var cooldown = idrRetryCooldown ?? TimeSpan.FromMilliseconds(150);
        _retryTicks = (long)(cooldown.TotalSeconds * Stopwatch.Frequency);
    }

    public bool Enqueue(T frame, bool isIdr, Action requestIdr, Action<T> dispose)
    {
        if (!isIdr && Volatile.Read(ref _needIdr) != 0)
        {
            dispose(frame);
            MaybeRequestIdr(requestIdr, force: false);
            return false;
        }

        if (isIdr)
        {
            while (_channel.Reader.TryRead(out var old)) dispose(old);
            if (_channel.Writer.TryWrite(frame))
            {
                Volatile.Write(ref _needIdr, 0);
                return true;
            }
            dispose(frame);
            Volatile.Write(ref _needIdr, 1);
            MaybeRequestIdr(requestIdr, force: true);
            return false;
        }

        if (_channel.Writer.TryWrite(frame)) return true;

        dispose(frame);
        while (_channel.Reader.TryRead(out var old)) dispose(old);
        Volatile.Write(ref _needIdr, 1);
        MaybeRequestIdr(requestIdr, force: true);
        return false;
    }

    public void Complete(Action<T> dispose)
    {
        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out var leftover)) dispose(leftover);
    }

    private void MaybeRequestIdr(Action requestIdr, bool force)
    {
        var now = _timestamp();
        var prev = Volatile.Read(ref _lastIdrReqTick);
        if (force || prev == long.MinValue || (now - prev) >= _retryTicks)
        {
            Volatile.Write(ref _lastIdrReqTick, now);
            requestIdr();
        }
    }
}

public sealed partial class ScreenMirror
{
    internal static (bool UseHevc, bool UseH264Baseline) NegotiateCodec(
        string[]? codecs, bool h264LowLatency, bool hevcLowLatency, bool preferHevc)
    {
        var hasHevc = codecs?.Any(c => string.Equals(c, "hevc", StringComparison.OrdinalIgnoreCase)) ?? false;
        // CodecProbe only advertises "hevc" when a hardware HEVC decoder is present; NVENC already encodes
        // 0-B-frame / maxNumRefFrames=1 bitstreams, so prefer HEVC when requested, and fall back to H.264
        // Baseline when the client lacks Android R FEATURE_LowLatency (avoids High-profile reorder latency).
        var useHevc = preferHevc && hasHevc;
        var useBaseline = !useHevc && !h264LowLatency;
        return (useHevc, useBaseline);
    }

    internal static (uint Width, uint Height) ComputeEncodeSize(
        int vddWidth,
        int vddHeight,
        int screenWidth,
        int screenHeight,
        int decoderSafeWidth,
        int decoderSafeHeight,
        bool useHevc)
    {
        _ = useHevc;
        var vw = Math.Max(64, vddWidth & ~1);
        var vh = Math.Max(64, vddHeight & ~1);
        var w = vw;
        var h = vh;
        var vddLandscape = vw >= vh;

        void ApplyCap(int rawCapW, int rawCapH)
        {
            if (rawCapW <= 0 || rawCapH <= 0) return;
            var capW = vddLandscape ? Math.Max(rawCapW, rawCapH) : Math.Min(rawCapW, rawCapH);
            var capH = vddLandscape ? Math.Min(rawCapW, rawCapH) : Math.Max(rawCapW, rawCapH);
            if (w <= capW && h <= capH) return;
            var scale = Math.Min((double)capW / w, (double)capH / h);
            w = Math.Max(64, ((int)Math.Floor(w * scale)) & ~1);
            h = Math.Max(64, ((int)Math.Floor(h * scale)) & ~1);
        }

        ApplyCap(screenWidth, screenHeight);
        ApplyCap(decoderSafeWidth, decoderSafeHeight);
        ApplyCap(4096, 4096);

        w = Math.Clamp(w & ~1, 64, vw);
        h = Math.Clamp(h & ~1, 64, vh);
        return ((uint)w, (uint)h);
    }

    internal static (uint BitrateBps, uint MaxBitrateBps) ComputeStreamBitrate(
        uint encodeWidth,
        uint encodeHeight,
        bool isUsb,
        bool useHevc,
        uint requestedBitrate,
        uint requestedMaxBitrate)
    {
        var pixels = (ulong)Math.Max(64u, encodeWidth) * Math.Max(64u, encodeHeight);
        uint usbBr;
        uint usbMax;
        if (pixels <= 600_000UL)
        {
            usbBr = useHevc ? 5_000_000u : 6_000_000u;
            usbMax = useHevc ? 8_000_000u : 9_000_000u;
        }
        else if (pixels <= 1_300_000UL)
        {
            usbBr = useHevc ? 8_000_000u : 10_000_000u;
            usbMax = useHevc ? 12_000_000u : 15_000_000u;
        }
        else if (pixels <= 2_800_000UL)
        {
            usbBr = useHevc ? 11_000_000u : 14_000_000u;
            usbMax = useHevc ? 17_000_000u : 21_000_000u;
        }
        else
        {
            usbBr = useHevc ? 14_000_000u : 18_000_000u;
            usbMax = useHevc ? 21_000_000u : 26_000_000u;
        }

        uint br;
        uint maxBr;
        if (isUsb)
        {
            br = usbBr;
            maxBr = usbMax;
        }
        else
        {
            br = (uint)(usbBr * 65UL / 100UL);
            if (requestedBitrate > 0) br = Math.Min(br, requestedBitrate);
            maxBr = (uint)(usbMax * 65UL / 100UL);
            if (requestedMaxBitrate > 0) maxBr = Math.Min(maxBr, Math.Max(requestedMaxBitrate, br));
        }

        br = Math.Clamp(br, 1_000_000u, 30_000_000u);
        maxBr = Math.Clamp(Math.Max(maxBr, br), br, 35_000_000u);
        return (br, maxBr);
    }

    internal sealed class AdaptiveScaleController
    {
        private static readonly double[] ScaleTiers = [1.0, 0.85, 0.72, 0.60, 0.50];
        private readonly uint _baseW;
        private readonly uint _baseH;
        private readonly uint _minShort;
        private readonly Func<long> _timestamp;
        private readonly long _downCooldownTicks;
        private readonly long _upCooldownTicks;

        private int _tierIndex;
        private uint _currentW;
        private uint _currentH;
        private int _badStreak;
        private int _goodStreak;
        private long _lastChangeTick = long.MinValue;

        public AdaptiveScaleController(uint baseWidth, uint baseHeight, Func<long>? timestampProvider = null)
        {
            _baseW = Math.Max(64u, baseWidth & ~1u);
            _baseH = Math.Max(64u, baseHeight & ~1u);
            _minShort = Math.Min(Math.Min(_baseW, _baseH), 480u);
            _currentW = _baseW;
            _currentH = _baseH;
            _timestamp = timestampProvider ?? Stopwatch.GetTimestamp;
            _downCooldownTicks = (long)(2.0 * Stopwatch.Frequency);
            _upCooldownTicks = (long)(4.0 * Stopwatch.Frequency);
        }

        public bool OnTelemetry(int decodeP50Ms, int decodeP95Ms, int dropped, out uint nextWidth, out uint nextHeight)
        {
            nextWidth = _currentW;
            nextHeight = _currentH;

            var now = _timestamp();
            var isBad = decodeP50Ms > 22 || decodeP95Ms > 38 || dropped >= 3;
            var isGood = decodeP50Ms < 11 && decodeP95Ms < 18 && dropped == 0;

            if (isBad)
            {
                _goodStreak = 0;
                if (_lastChangeTick != long.MinValue && (now - _lastChangeTick) < _downCooldownTicks)
                    return false;
                _badStreak++;
                if (_badStreak >= 2)
                {
                    for (var idx = _tierIndex + 1; idx < ScaleTiers.Length; idx++)
                    {
                        var (cw, ch) = SizeForTier(idx);
                        if ((cw < _currentW || ch < _currentH) && Math.Min(cw, ch) >= _minShort)
                        {
                            _tierIndex = idx;
                            _currentW = cw;
                            _currentH = ch;
                            _badStreak = 0;
                            _lastChangeTick = now;
                            nextWidth = cw;
                            nextHeight = ch;
                            return true;
                        }
                    }
                }
                return false;
            }

            if (isGood)
            {
                _goodStreak++;
                _badStreak = 0;
                if (_goodStreak >= 10 && _tierIndex > 0 &&
                    (_lastChangeTick == long.MinValue || (now - _lastChangeTick) >= _upCooldownTicks))
                {
                    var nextIdx = _tierIndex - 1;
                    var (cw, ch) = SizeForTier(nextIdx);
                    _tierIndex = nextIdx;
                    _currentW = cw;
                    _currentH = ch;
                    _goodStreak = 0;
                    _lastChangeTick = now;
                    nextWidth = cw;
                    nextHeight = ch;
                    return true;
                }
                return false;
            }

            _badStreak = 0;
            return false;
        }

        private (uint Width, uint Height) SizeForTier(int tierIdx)
        {
            var f = ScaleTiers[Math.Clamp(tierIdx, 0, ScaleTiers.Length - 1)];
            if (f >= 0.999) return (_baseW, _baseH);
            var w = Math.Max(64u, ((uint)Math.Floor(_baseW * f)) & ~1u);
            var h = Math.Max(64u, ((uint)Math.Floor(_baseH * f)) & ~1u);
            w = Math.Min(w, _baseW);
            h = Math.Min(h, _baseH);
            return (w, h);
        }
    }

    internal sealed class WifiBitrateController
    {
        private readonly Func<long> _timestamp;
        private readonly long _downCooldownTicks;
        private readonly long _upWindowTicks;
        private uint _baseBitrate;
        private uint _minBitrate;
        private int _congestedStreak;
        private long _lastDownTick = long.MinValue;
        private long _healthySinceTick = long.MinValue;

        public uint CurrentBitrate { get; private set; }
        public uint CurrentMaxBitrate { get; private set; }

        public WifiBitrateController(uint baseBitrateBps, Func<long>? timestampProvider = null)
        {
            _timestamp = timestampProvider ?? Stopwatch.GetTimestamp;
            _downCooldownTicks = (long)(1.5 * Stopwatch.Frequency);
            _upWindowTicks = (long)(4.0 * Stopwatch.Frequency);
            ResetBase(baseBitrateBps);
        }

        public void ResetBase(uint newBaseBitrate)
        {
            _baseBitrate = Math.Clamp(newBaseBitrate, 1_200_000u, 30_000_000u);
            _minBitrate = Math.Clamp(_baseBitrate / 3u, 1_200_000u, 2_000_000u);
            CurrentBitrate = _baseBitrate;
            CurrentMaxBitrate = CapMax(_baseBitrate);
            _congestedStreak = 0;
            _healthySinceTick = long.MinValue;
        }

        public bool OnFrameSent(double sendMs, double queueAgeMs, out uint nextBitrate, out uint nextMaxBitrate)
        {
            var now = _timestamp();
            var severeStall = sendMs >= 60.0;
            var congested = severeStall || sendMs >= 25.0 || queueAgeMs >= 30.0;

            if (congested)
            {
                _healthySinceTick = long.MinValue;
                _congestedStreak++;
                if ((_congestedStreak >= 2 || severeStall) &&
                    (_lastDownTick == long.MinValue || (now - _lastDownTick) >= _downCooldownTicks))
                {
                    var stepped = Math.Max(_minBitrate, CurrentBitrate * 72u / 100u);
                    if (stepped < CurrentBitrate)
                    {
                        CurrentBitrate = stepped;
                        CurrentMaxBitrate = CapMax(stepped);
                        _lastDownTick = now;
                        _congestedStreak = 0;
                        nextBitrate = CurrentBitrate;
                        nextMaxBitrate = CurrentMaxBitrate;
                        return true;
                    }
                    _congestedStreak = 0;
                }
                nextBitrate = CurrentBitrate;
                nextMaxBitrate = CurrentMaxBitrate;
                return false;
            }

            _congestedStreak = 0;
            if (sendMs < 12.0 && queueAgeMs < 15.0 && CurrentBitrate < _baseBitrate)
            {
                if (_healthySinceTick == long.MinValue)
                {
                    _healthySinceTick = now;
                }
                else if ((now - _healthySinceTick) >= _upWindowTicks)
                {
                    var stepped = Math.Min(_baseBitrate, CurrentBitrate + 350_000u);
                    _healthySinceTick = now;
                    if (stepped > CurrentBitrate)
                    {
                        CurrentBitrate = stepped;
                        CurrentMaxBitrate = CapMax(stepped);
                        nextBitrate = CurrentBitrate;
                        nextMaxBitrate = CurrentMaxBitrate;
                        return true;
                    }
                }
            }
            else if (sendMs >= 12.0 || queueAgeMs >= 15.0)
            {
                _healthySinceTick = long.MinValue;
            }

            nextBitrate = CurrentBitrate;
            nextMaxBitrate = CurrentMaxBitrate;
            return false;
        }

        public bool OnClientDrop(int dropped, out uint nextBitrate, out uint nextMaxBitrate)
        {
            nextBitrate = CurrentBitrate;
            nextMaxBitrate = CurrentMaxBitrate;
            if (dropped < 2) return false;

            var now = _timestamp();
            _healthySinceTick = long.MinValue;
            if (_lastDownTick != long.MinValue && (now - _lastDownTick) < _downCooldownTicks)
                return false;

            var stepped = Math.Max(_minBitrate, CurrentBitrate * 78u / 100u);
            if (stepped >= CurrentBitrate) return false;

            CurrentBitrate = stepped;
            CurrentMaxBitrate = CapMax(stepped);
            _lastDownTick = now;
            _congestedStreak = 0;
            nextBitrate = CurrentBitrate;
            nextMaxBitrate = CurrentMaxBitrate;
            return true;
        }

        private static uint CapMax(uint bitrate) => Math.Max(bitrate, bitrate * 5u / 4u);
    }

#if WINDOWS
    internal enum PbCodec : uint { H264 = 0, Hevc = 1 }
    internal enum PbSourceMode : uint { DxgiOutput = 0, D3d11Synthetic = 1 }

    [Flags]
    internal enum PbFrameFlags : uint { None = 0, Idr = 1 << 0, Hevc = 1 << 1 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PbCapConfig
    {
        public uint AdapterIndex;
        public uint OutputIndex;
        public uint SourceMode;
        public uint SyntheticWidth;
        public uint SyntheticHeight;
        public uint MaxFps;
        public uint BitrateBps;
        public uint MaxBitrateBps;
        public uint Codec;
        public uint DrawCursor;
        public uint IntraRefresh;
        public uint H264Baseline;
        public uint EncodeWidth;
        public uint EncodeHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PbCapStats
    {
        public ulong FramesCaptured;
        public ulong FramesEncoded;
        public ulong FramesSkipped;
        public ulong IdrCount;
        public ulong BytesEncoded;
        public uint LastCaptureUs;
        public uint LastEncodeUs;
        public double AvgCaptureUs;
        public double AvgEncodeUs;
        public uint Width;
        public uint Height;
        public ulong VramCurrentBytes;
    }

    internal sealed class NativeEncodedFrame(
        byte[] buffer,
        int length,
        ulong ptsUs,
        PbFrameFlags flags,
        int width = 0,
        int height = 0,
        long enqueuedTimestamp = 0) : IDisposable
    {
        public byte[] Buffer = buffer;
        public int Length { get; } = length;
        public ulong PtsUs { get; } = ptsUs;
        public PbFrameFlags Flags { get; } = flags;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public long EnqueuedTimestamp { get; } = enqueuedTimestamp;

        public void Dispose()
        {
            var b = Interlocked.Exchange(ref Buffer!, null!);
            if (b is not null) ArrayPool<byte>.Shared.Return(b);
        }
    }

    private static int _nativeDllState; // 0 = unchecked, 1 = loaded, -1 = unavailable

    internal static bool EnsureNativeCaptureDllLoaded()
    {
        var state = Volatile.Read(ref _nativeDllState);
        if (state != 0) return state > 0;

        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "PhoneBoxCapture.dll"),
            Path.Combine(AppPaths.InstallDir, "PhoneBoxCapture.dll"),
        })
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _))
            {
                Volatile.Write(ref _nativeDllState, 1);
                return true;
            }
        }
        if (NativeLibrary.TryLoad("PhoneBoxCapture", out _))
        {
            Volatile.Write(ref _nativeDllState, 1);
            return true;
        }
        Volatile.Write(ref _nativeDllState, -1);
        return false;
    }

    internal sealed unsafe class NativeCaptureSession : IDisposable
    {
        private nint _handle;
        private GCHandle _selfPin;
        private readonly FrameBackpressureGate<NativeEncodedFrame> _gate = new(capacity: 2);

        public ChannelReader<NativeEncodedFrame> Frames => _gate.Reader;

        public NativeCaptureSession(PbCapConfig cfg)
        {
            if (!EnsureNativeCaptureDllLoaded())
                throw new DllNotFoundException("PhoneBoxCapture.dll not found");

            _selfPin = GCHandle.Alloc(this);

            delegate* unmanaged[Cdecl]<byte*, uint, ulong, uint, uint, uint, nint, void> cb = &OnNativeFrame;
            var rc = pb_cap_create(in cfg, (nint)cb, GCHandle.ToIntPtr(_selfPin), out _handle);
            if (rc != 0)
            {
                var err = GetLastError();
                Dispose();
                throw new InvalidOperationException($"pb_cap_create failed (rc={rc}): {err}");
            }
        }

        public void Start()
        {
            var rc = pb_cap_start(_handle);
            if (rc != 0) throw new InvalidOperationException($"pb_cap_start failed (rc={rc}): {GetLastError()}");
        }

        public void Stop()
        {
            if (_handle != 0) pb_cap_stop(_handle);
        }

        public void RequestIdr()
        {
            if (_handle != 0) pb_cap_request_idr(_handle);
        }

        public void SetBitrate(uint bitrateBps, uint maxBitrateBps)
        {
            if (_handle != 0) pb_cap_set_bitrate(_handle, bitrateBps, maxBitrateBps);
        }

        public int SetEncodeSize(uint width, uint height)
        {
            return _handle != 0 ? pb_cap_set_encode_size(_handle, width, height) : -1;
        }

        public PbCapStats GetStats()
        {
            pb_cap_get_stats(_handle, out var s);
            return s;
        }

        public string GetLastError()
        {
            if (_handle == 0) return "";
            var buf = stackalloc byte[512];
            var len = pb_cap_get_last_error(_handle, buf, 512);
            return len > 0 ? Marshal.PtrToStringUTF8((nint)buf, len) ?? "" : "";
        }

        private void HandleFrame(byte* data, uint size, ulong ptsUs, PbFrameFlags flags)
        {
            var isIdr = (flags & PbFrameFlags.Idr) != 0;
            var frameW = 0;
            var frameH = 0;
            if (isIdr && _handle != 0)
            {
                pb_cap_get_stats(_handle, out var st);
                frameW = (int)st.Width;
                frameH = (int)st.Height;
            }

            var total = 14 + (int)size;
            var rented = ArrayPool<byte>.Shared.Rent(total);
            rented[0] = 0x01; // VideoFrame
            BitConverter.TryWriteBytes(rented.AsSpan(1, 4), size);
            BitConverter.TryWriteBytes(rented.AsSpan(5, 8), ptsUs);
            rented[13] = (byte)flags;
            new ReadOnlySpan<byte>(data, (int)size).CopyTo(rented.AsSpan(14));

            var frame = new NativeEncodedFrame(rented, total, ptsUs, flags, frameW, frameH, Stopwatch.GetTimestamp());
            _gate.Enqueue(frame, isIdr, RequestIdr, static f => f.Dispose());
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnNativeFrame(byte* data, uint size, ulong ptsUs, uint capUs, uint encUs, uint flags, nint user)
        {
            if (user == 0) return;
            var handle = GCHandle.FromIntPtr(user);
            if (handle.Target is NativeCaptureSession self)
                self.HandleFrame(data, size, ptsUs, (PbFrameFlags)flags);
        }

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handle, 0);
            if (h != 0) pb_cap_destroy(h);
            _gate.Complete(static f => f.Dispose());
            if (_selfPin.IsAllocated) _selfPin.Free();
        }

        [DllImport("PhoneBoxCapture", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pb_cap_create(in PbCapConfig cfg, nint cb, nint user, out nint outHandle);

        [DllImport("PhoneBoxCapture", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pb_cap_start(nint handle);

        [DllImport("PhoneBoxCapture", CallingConvention = CallingConvention.Cdecl)]
        private static extern void pb_cap_stop(nint handle);

        [DllImport("PhoneBoxCapture", CallingConvention = CallingConvention.Cdecl)]
        private static extern void pb_cap_request_idr(nint handle);

        [DllImport("PhoneBoxCapture", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pb_cap_set_bitrate(nint handle, uint bitrateBps, uint maxBitrateBps);

        [DllImport("PhoneBoxCapture", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pb_cap_set_encode_size(nint handle, uint encodeWidth, uint encodeHeight);

        [DllImport("PhoneBoxCapture", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pb_cap_get_stats(nint handle, out PbCapStats outStats);

        [DllImport("PhoneBoxCapture", CallingConvention = CallingConvention.Cdecl)]
        private static extern int pb_cap_get_last_error(nint handle, byte* buf, uint bufLen);

        [DllImport("PhoneBoxCapture", CallingConvention = CallingConvention.Cdecl)]
        private static extern void pb_cap_destroy(nint handle);
    }
#endif
}
