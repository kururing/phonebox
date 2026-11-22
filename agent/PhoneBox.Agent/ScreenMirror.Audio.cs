using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Channels;

#if WINDOWS
using NAudio.CoreAudioApi;
using NAudio.Wave;
#endif

namespace PhoneBox.Agent;

public enum ScreenAudioMode
{
    Off,
    Default,
    Virtual,
    Device,
}

public sealed record ScreenAudioEndpoint(string Id, string Name, bool IsDefault, bool IsVirtual);

public sealed record ScreenAudioInventory(
    bool Supported,
    IReadOnlyList<ScreenAudioEndpoint> Devices,
    ScreenAudioEndpoint? VirtualDevice,
    bool CanInstallVirtual,
    string? DriverHint);

public sealed record ScreenAudioInstallResult(
    bool Ok,
    bool AlreadyInstalled,
    bool RebootRequired,
    ScreenAudioEndpoint? Device,
    string? Error);

internal static class ScreenAudioProtocol
{
    public const byte OpcodeAudioFrame = 0x03;
    public const byte OpcodeAudioMeta = 0x04;
    public const byte CodecPcm16 = 0;
    public const byte CodecNone = 255;
    public const int TargetSampleRate = 48_000;
    public const int TargetChannels = 2;
    public const int FrameSamples = 960;
    public const int FramePcmBytes = FrameSamples * TargetChannels * 2;
    public const int FrameHeaderBytes = 15;

    private static readonly string[] ExcludedHardwareVendors = ["Realtek", "NVIDIA", "Intel", "SIMGOT"];

    public static byte[] BuildAudioMeta(
        bool active,
        bool isVirtual,
        int sampleRate = TargetSampleRate,
        int channels = TargetChannels,
        int frameSamples = FrameSamples)
    {
        var meta = new byte[10];
        meta[0] = OpcodeAudioMeta;
        meta[1] = active ? CodecPcm16 : CodecNone;
        BinaryPrimitives.WriteUInt32LittleEndian(meta.AsSpan(2, 4), (uint)Math.Max(0, sampleRate));
        meta[6] = (byte)Math.Clamp(channels, 0, 255);
        BinaryPrimitives.WriteUInt16LittleEndian(meta.AsSpan(7, 2), (ushort)Math.Clamp(frameSamples, 0, ushort.MaxValue));
        meta[9] = (byte)((active ? 1 : 0) | (isVirtual ? 2 : 0));
        return meta;
    }

    public static void WriteAudioFrameHeader(Span<byte> dst, ushort payloadLen, uint seq, long ptsUs)
    {
        if (dst.Length < FrameHeaderBytes)
            throw new ArgumentException($"Destination needs {FrameHeaderBytes} bytes", nameof(dst));
        dst[0] = OpcodeAudioFrame;
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(1, 2), payloadLen);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(3, 4), seq);
        BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(7, 8), ptsUs);
    }

    public static ScreenAudioMode ParseAudioMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "default" => ScreenAudioMode.Default,
        "virtual" => ScreenAudioMode.Virtual,
        "device" => ScreenAudioMode.Device,
        _ => ScreenAudioMode.Off,
    };

    public static int VirtualAudioPriority(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        foreach (var vendor in ExcludedHardwareVendors)
        {
            if (name.Contains(vendor, StringComparison.OrdinalIgnoreCase))
                return 0;
        }
        if (name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (name.Contains("Virtual Audio Driver", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (name.Contains("Steam Streaming Speakers", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (name.Contains("VoiceMeeter Input", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("VoiceMeeter Aux Input", StringComparison.OrdinalIgnoreCase))
            return 4;
        if (name.Contains("PhoneBox Audio", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Virtual Audio", StringComparison.OrdinalIgnoreCase))
            return 5;
        return 0;
    }

    public static ScreenAudioEndpoint? SelectAudioEndpoint(
        ScreenAudioMode mode,
        string? configuredDeviceId,
        IReadOnlyList<ScreenAudioEndpoint> endpoints)
    {
        if (endpoints.Count == 0) return null;
        return mode switch
        {
            ScreenAudioMode.Off => null,
            ScreenAudioMode.Default => endpoints.FirstOrDefault(e => e.IsDefault) ?? endpoints[0],
            ScreenAudioMode.Virtual => endpoints
                .Where(e => e.IsVirtual)
                .OrderBy(e =>
                {
                    var p = VirtualAudioPriority(e.Name);
                    return p > 0 ? p : int.MaxValue;
                })
                .FirstOrDefault(),
            ScreenAudioMode.Device => string.IsNullOrWhiteSpace(configuredDeviceId)
                ? endpoints.FirstOrDefault(e => e.IsDefault) ?? endpoints[0]
                : endpoints.FirstOrDefault(e => string.Equals(e.Id, configuredDeviceId.Trim(), StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
    }
}

internal sealed class ScreenAudioConverter
{
    private const int SilenceTailFrames = 10;
    private const short SilenceThreshold = 2;

    private readonly int _srcRate;
    private readonly int _srcChannels;
    private readonly int _srcBits;
    private readonly bool _srcFloat;
    private readonly int _dstRate;
    private readonly int _frameSamples;
    private readonly int _bytesPerSrcSample;
    private readonly int _bytesPerSrcFrame;
    private readonly double _step;
    private readonly byte[] _outBuf;

    private int _outPos;
    private double _phase;
    private float _prevL;
    private float _prevR;
    private float _curL;
    private float _curR;
    private bool _hasPrev;
    private bool _hasCur;
    private int _silentRun;
    private short _frameMaxAbs;

    public ScreenAudioConverter(
        int sourceSampleRate,
        int sourceChannels,
        int sourceBitsPerSample,
        bool sourceIsFloat,
        int targetSampleRate = ScreenAudioProtocol.TargetSampleRate,
        int frameSamples = ScreenAudioProtocol.FrameSamples)
    {
        _srcRate = Math.Max(1, sourceSampleRate);
        _srcChannels = Math.Max(1, sourceChannels);
        _srcBits = sourceBitsPerSample;
        _srcFloat = sourceIsFloat;
        _dstRate = Math.Max(1, targetSampleRate);
        _frameSamples = Math.Max(1, frameSamples);
        _bytesPerSrcSample = Math.Max(1, _srcBits / 8);
        _bytesPerSrcFrame = _bytesPerSrcSample * _srcChannels;
        _step = (double)_srcRate / _dstRate;
        _outBuf = new byte[_frameSamples * ScreenAudioProtocol.TargetChannels * 2];
    }

    public void Process(ReadOnlySpan<byte> input, bool isSilentFlag, Action<ReadOnlySpan<byte>> onFrame)
    {
        if (isSilentFlag)
        {
            _prevL = 0f;
            _prevR = 0f;
            _curL = 0f;
            _curR = 0f;
            _hasPrev = false;
            _hasCur = false;
            if (input.IsEmpty)
            {
                FeedSilentOutputSamples(_frameSamples, onFrame);
                return;
            }
            var srcFrames = input.Length / _bytesPerSrcFrame;
            var dstFrames = _srcRate == _dstRate
                ? srcFrames
                : Math.Max(1, (int)Math.Round((double)srcFrames * _dstRate / _srcRate));
            FeedSilentOutputSamples(dstFrames, onFrame);
            return;
        }

        if (_bytesPerSrcFrame <= 0 || input.Length < _bytesPerSrcFrame) return;

        var frameCount = input.Length / _bytesPerSrcFrame;
        if (_srcRate == _dstRate)
        {
            for (var i = 0; i < frameCount; i++)
            {
                ReadStereoFrame(input.Slice(i * _bytesPerSrcFrame, _bytesPerSrcFrame), out var l, out var r);
                EmitOutputSample(l, r, onFrame);
            }
            _hasPrev = false;
            _hasCur = false;
            _phase = 0;
            return;
        }

        for (var i = 0; i < frameCount; i++)
        {
            ReadStereoFrame(input.Slice(i * _bytesPerSrcFrame, _bytesPerSrcFrame), out var l, out var r);
            if (!_hasCur)
            {
                _prevL = l;
                _prevR = r;
                _curL = l;
                _curR = r;
                _hasCur = true;
                _hasPrev = true;
                _phase = 0.0;
            }
            else
            {
                _prevL = _curL;
                _prevR = _curR;
                _curL = l;
                _curR = r;
                _hasPrev = true;
            }

            while (_phase < 1.0)
            {
                var t = (float)_phase;
                var sl = _hasPrev ? _prevL + (_curL - _prevL) * t : _curL;
                var sr = _hasPrev ? _prevR + (_curR - _prevR) * t : _curR;
                EmitOutputSample(sl, sr, onFrame);
                _phase += _step;
            }
            _phase -= 1.0;
        }
    }

    private void FeedSilentOutputSamples(int count, Action<ReadOnlySpan<byte>> onFrame)
    {
        for (var i = 0; i < count; i++)
            EmitOutputSample(0f, 0f, onFrame);
    }

    private void EmitOutputSample(float left, float right, Action<ReadOnlySpan<byte>> onFrame)
    {
        var l16 = FloatToInt16(left);
        var r16 = FloatToInt16(right);
        var absL = l16 == short.MinValue ? short.MaxValue : Math.Abs(l16);
        var absR = r16 == short.MinValue ? short.MaxValue : Math.Abs(r16);
        if (absL > _frameMaxAbs) _frameMaxAbs = absL;
        if (absR > _frameMaxAbs) _frameMaxAbs = absR;

        BinaryPrimitives.WriteInt16LittleEndian(_outBuf.AsSpan(_outPos, 2), l16);
        BinaryPrimitives.WriteInt16LittleEndian(_outBuf.AsSpan(_outPos + 2, 2), r16);
        _outPos += 4;

        if (_outPos < _outBuf.Length) return;

        var isSilent = _frameMaxAbs <= SilenceThreshold;
        _outPos = 0;
        _frameMaxAbs = 0;

        if (isSilent)
        {
            _silentRun++;
            if (_silentRun > SilenceTailFrames) return;
        }
        else
        {
            _silentRun = 0;
        }

        onFrame(_outBuf);
    }

    private static short FloatToInt16(float sample)
    {
        if (!float.IsFinite(sample)) return 0;
        var clamped = Math.Clamp(sample, -1.0f, 1.0f);
        var scaled = (int)MathF.Round(clamped * 32767.0f);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    private void ReadStereoFrame(ReadOnlySpan<byte> frame, out float left, out float right)
    {
        if (_srcChannels == 1)
        {
            var m = ReadSample(frame);
            left = m;
            right = m;
            return;
        }
        if (_srcChannels == 2)
        {
            left = ReadSample(frame);
            right = ReadSample(frame.Slice(_bytesPerSrcSample));
            return;
        }
        if (_srcChannels >= 6)
        {
            // Standard Windows WAVE_FORMAT_EXTENSIBLE order: FL, FR, FC, LFE, RL, RR [, SL, SR]
            const float c = 0.70710678f;
            var fl = ReadSample(frame);
            var fr = ReadSample(frame.Slice(_bytesPerSrcSample));
            var fc = ReadSample(frame.Slice(2 * _bytesPerSrcSample));
            var rl = ReadSample(frame.Slice(4 * _bytesPerSrcSample));
            var rr = ReadSample(frame.Slice(5 * _bytesPerSrcSample));
            var lSum = fl + c * fc + c * rl;
            var rSum = fr + c * fc + c * rr;
            if (_srcChannels >= 8)
            {
                lSum += c * ReadSample(frame.Slice(6 * _bytesPerSrcSample));
                rSum += c * ReadSample(frame.Slice(7 * _bytesPerSrcSample));
            }
            left = Math.Clamp(lSum, -1.0f, 1.0f);
            right = Math.Clamp(rSum, -1.0f, 1.0f);
            return;
        }
        left = ReadSample(frame);
        right = ReadSample(frame.Slice(_bytesPerSrcSample));
    }

    private float ReadSample(ReadOnlySpan<byte> s)
    {
        if (_srcFloat && _srcBits == 32)
        {
            var v = BinaryPrimitives.ReadSingleLittleEndian(s);
            return float.IsFinite(v) ? Math.Clamp(v, -1.0f, 1.0f) : 0f;
        }
        return _srcBits switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768.0f,
            24 => ReadInt24LittleEndian(s) / 8388608.0f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648.0f,
            _ => 0f,
        };
    }

    private static int ReadInt24LittleEndian(ReadOnlySpan<byte> s)
    {
        var v = s[0] | (s[1] << 8) | (s[2] << 16);
        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
        return v;
    }
}

public sealed partial class ScreenMirror
{
#if WINDOWS
    private string? _autoSwitchedVirtualId;
    private Dictionary<int, string>? _previousDefaultRenderByRole;

    public ScreenAudioInventory ListAudioOutputs()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            string? defaultId = null;
            try
            {
                using var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = def.ID;
            }
            catch { }

            var list = new List<ScreenAudioEndpoint>();
            using var endpoints = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var d in endpoints)
            {
                using (d)
                {
                    try
                    {
                        var name = d.FriendlyName;
                        var isVirtual = ScreenAudioProtocol.VirtualAudioPriority(name) > 0;
                        list.Add(new ScreenAudioEndpoint(d.ID, name, string.Equals(d.ID, defaultId, StringComparison.OrdinalIgnoreCase), isVirtual));
                    }
                    catch { }
                }
            }
            var virtualDev = ScreenAudioProtocol.SelectAudioEndpoint(ScreenAudioMode.Virtual, null, list);
            return new ScreenAudioInventory(true, list, virtualDev, true, "VB-CABLE (Windows 10/11)");
        }
        catch
        {
            return new ScreenAudioInventory(true, [], null, true, "VB-CABLE (Windows 10/11)");
        }
    }

    public async Task<ScreenAudioInstallResult> InstallVirtualAudioAsync(CancellationToken ct = default)
    {
        if (ListAudioOutputs().VirtualDevice is { } existing)
            return new ScreenAudioInstallResult(true, true, false, existing, null);

        var tempDir = Path.Combine(Path.GetTempPath(), "phonebox-vbcable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var zipPath = Path.Combine(tempDir, "vbcable.zip");
            var extractDir = Path.Combine(tempDir, "pkg");
            Directory.CreateDirectory(extractDir);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBoxAgent/1.0");
            Exception? dlErr = null;
            var downloaded = false;
            foreach (var url in new[]
            {
                "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip",
                "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip",
            })
            {
                try
                {
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(zipPath);
                    await resp.Content.CopyToAsync(fs, ct);
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { dlErr = ex; }
            }

            if (!downloaded)
                return new ScreenAudioInstallResult(false, false, false, null,
                    "Không tải được VB-CABLE: " + (dlErr?.Message ?? "lỗi mạng"));

            var fullRoot = Path.GetFullPath(extractDir);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
                fullRoot += Path.DirectorySeparatorChar;

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var destPath = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));
                    if (!destPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Archive entry escapes extraction directory.");
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            var setupName = Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe";
            var setupExe = Directory.EnumerateFiles(extractDir, setupName, SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(extractDir, "VBCABLE_Setup*.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (setupExe is null)
                return new ScreenAudioInstallResult(false, false, false, null,
                    "Không tìm thấy trình cài đặt trong gói VB-CABLE.");

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(setupExe, "-i -h")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(setupExe)!,
                },
            };

            try
            {
                if (!proc.Start())
                    return new ScreenAudioInstallResult(false, false, false, null, "Không khởi chạy được trình cài VB-CABLE.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new ScreenAudioInstallResult(false, false, false, null, "Đã hủy cài đặt (UAC bị từ chối).");
            }

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(60));
            await proc.WaitForExitAsync(waitCts.Token);

            var deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (ListAudioOutputs().VirtualDevice is { } installed)
                    return new ScreenAudioInstallResult(true, false, false, installed, null);
                await Task.Delay(250, ct);
            }

            if (ListAudioOutputs().VirtualDevice is { } ready)
                return new ScreenAudioInstallResult(true, false, false, ready, null);

            if (proc.ExitCode == 0)
                return new ScreenAudioInstallResult(true, false, true, null,
                    "Đã cài VB-CABLE; khởi động lại Windows để thiết bị âm thanh ảo xuất hiện.");

            return new ScreenAudioInstallResult(false, false, false, null,
                $"Trình cài VB-CABLE thoát với mã {proc.ExitCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ScreenAudioInstallResult(false, false, false, null, "Đã hủy cài đặt.");
        }
        catch (Exception ex)
        {
            return new ScreenAudioInstallResult(false, false, false, null, ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static string? DefaultRenderDeviceId(Role role = Role.Multimedia)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, role);
            return dev.ID;
        }
        catch { return null; }
    }

    private void EnsureVirtualAudioAutoSwitch(string virtualDeviceId)
    {
        bool needCapture;
        lock (_gate)
        {
            needCapture = _previousDefaultRenderByRole is null ||
                          !string.Equals(_autoSwitchedVirtualId, virtualDeviceId, StringComparison.OrdinalIgnoreCase);
        }

        if (needCapture)
        {
            var captured = new Dictionary<int, string>(3);
            for (var role = 0; role < 3; role++)
            {
                var cur = DefaultRenderDeviceId((Role)role);
                if (!string.IsNullOrEmpty(cur) && !string.Equals(cur, virtualDeviceId, StringComparison.OrdinalIgnoreCase))
                    captured[role] = cur;
            }
            lock (_gate)
            {
                _previousDefaultRenderByRole ??= captured;
                _autoSwitchedVirtualId = virtualDeviceId;
            }
        }

        SetDefaultRenderEndpoint(virtualDeviceId, null);
    }

    private void RestoreDefaultAudioIfSwitched()
    {
        string? virtualId;
        Dictionary<int, string>? prevByRole;
        lock (_gate)
        {
            virtualId = _autoSwitchedVirtualId;
            prevByRole = _previousDefaultRenderByRole;
            _autoSwitchedVirtualId = null;
            _previousDefaultRenderByRole = null;
        }

        if (string.IsNullOrEmpty(virtualId) || prevByRole is null || prevByRole.Count == 0)
            return;

        foreach (var (role, prevId) in prevByRole)
        {
            var current = DefaultRenderDeviceId((Role)role);
            if (string.Equals(current, virtualId, StringComparison.OrdinalIgnoreCase))
                SetDefaultRenderEndpoint(prevId, role);
        }
    }

    private void SetDefaultRenderEndpoint(string deviceId, int? specificRole)
    {
        IPolicyConfig? policy = null;
        try
        {
            policy = (IPolicyConfig)new PolicyConfig();
            if (specificRole is { } r)
            {
                policy.SetDefaultEndpoint(deviceId, r);
            }
            else
            {
                for (var role = 0; role < 3; role++)
                    policy.SetDefaultEndpoint(deviceId, role);
            }
        }
        catch (Exception ex)
        {
            _log?.LogDebug("ScreenMirror audio policy switch failed: {Msg}", ex.Message);
        }
        finally
        {
            if (policy is not null && Marshal.IsComObject(policy))
            {
                try { Marshal.ReleaseComObject(policy); } catch { }
            }
        }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfig;

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }

    private sealed class ScreenAudioPacket(byte[] buffer, int length, long enqueuedTimestamp) : IDisposable
    {
        public byte[] Buffer = buffer;
        public int Length { get; } = length;
        public long EnqueuedTimestamp { get; } = enqueuedTimestamp;

        public void Dispose()
        {
            var b = Interlocked.Exchange(ref Buffer!, null!);
            if (b is not null) ArrayPool<byte>.Shared.Return(b);
        }
    }

    private sealed class ScreenAudioCaptureSession : IDisposable
    {
        private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

        private readonly Channel<ScreenAudioPacket> _channel;
        private readonly ILogger? _log;
        private readonly MMDevice _device;
        private readonly WasapiRecorder _capture;
        private readonly ScreenAudioConverter _converter;
        private readonly long _startTick;
        private uint _seq;
        private int _disposed;
        private float _gain = 1f;
        private AudioEndpointVolume? _endpointVolume;

        public ChannelReader<ScreenAudioPacket> Packets => _channel.Reader;

        private void OnEndpointVolume(AudioVolumeNotificationData data) =>
            Volatile.Write(ref _gain, data.Muted ? 0f : Math.Clamp(data.MasterVolume, 0f, 1f));

        public ScreenAudioCaptureSession(string deviceId, ILogger? log, bool followEndpointVolume = false)
        {
            _log = log;
            _channel = Channel.CreateBounded<ScreenAudioPacket>(
                new BoundedChannelOptions(4)
                {
                    SingleReader = false,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                },
                static p => p.Dispose());

            using var en = new MMDeviceEnumerator();
            _device = en.GetDevice(deviceId);
            if (_device.State != DeviceState.Active)
            {
                _device.Dispose();
                throw new InvalidOperationException("Audio output device is not active.");
            }

            try
            {
                _capture = new WasapiRecorderBuilder()
                    .WithDevice(_device)
                    .WithLoopbackCapture()
                    .WithPollingSync()
                    .WithBufferLength(20)
                    .WithMmcssThreadPriority("Pro Audio")
                    .Build();

                var fmt = _capture.WaveFormat;
                var isFloat = fmt is WaveFormatExtensible ext
                    ? ext.SubFormat == IeeeFloatSubFormat
                    : fmt.Encoding == WaveFormatEncoding.IeeeFloat;

                _converter = new ScreenAudioConverter(fmt.SampleRate, fmt.Channels, fmt.BitsPerSample, isFloat);
                _startTick = Stopwatch.GetTimestamp();

                if (followEndpointVolume)
                {
                    try
                    {
                        _endpointVolume = _device.AudioEndpointVolume;
                        Volatile.Write(ref _gain, _endpointVolume.Mute ? 0f : _endpointVolume.MasterVolumeLevelScalar);
                        _endpointVolume.OnVolumeNotification += OnEndpointVolume;
                    }
                    catch (Exception ex) { _log?.LogDebug("ScreenMirror endpoint volume unavailable: {Msg}", ex.Message); }
                }

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
                _log?.LogInformation(
                    "ScreenMirror audio capture started on '{Device}' ({Rate} Hz, {Bits}-bit, {Ch} ch, float={IsFloat})",
                    _device.FriendlyName, fmt.SampleRate, fmt.BitsPerSample, fmt.Channels, isFloat);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long position, long qpc)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var isSilent = (flags & AudioClientBufferFlags.Silent) != 0;
            _converter.Process(buffer, isSilent, pcm =>
            {
                var total = ScreenAudioProtocol.FrameHeaderBytes + pcm.Length;
                var rented = ArrayPool<byte>.Shared.Rent(total);
                var now = Stopwatch.GetTimestamp();
                var ptsUs = (long)(Stopwatch.GetElapsedTime(_startTick, now).TotalMicroseconds);
                ScreenAudioProtocol.WriteAudioFrameHeader(rented.AsSpan(0, ScreenAudioProtocol.FrameHeaderBytes), (ushort)pcm.Length, ++_seq, ptsUs);
                var dst = rented.AsSpan(ScreenAudioProtocol.FrameHeaderBytes, pcm.Length);
                pcm.CopyTo(dst);
                var gain = Volatile.Read(ref _gain);
                if (gain < 0.999f)
                {
                    // WASAPI loopback ignores the endpoint volume: apply the Windows slider (and mute) here.
                    for (var o = 0; o + 1 < dst.Length; o += 2)
                    {
                        var s = (short)(dst[o] | (dst[o + 1] << 8));
                        var v = (short)Math.Clamp((int)MathF.Round(s * gain), short.MinValue, short.MaxValue);
                        dst[o] = (byte)v;
                        dst[o + 1] = (byte)(v >> 8);
                    }
                }
                var pkt = new ScreenAudioPacket(rented, total, now);
                if (!_channel.Writer.TryWrite(pkt))
                    pkt.Dispose();
            });
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is not null)
                _log?.LogDebug("ScreenMirror audio capture stopped: {Msg}", e.Exception.Message);
            _channel.Writer.TryComplete();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { if (_endpointVolume is not null) _endpointVolume.OnVolumeNotification -= OnEndpointVolume; } catch { }
            try { _capture?.StopRecording(); } catch { }
            try { _capture?.Dispose(); } catch { }
            try { _device.Dispose(); } catch { }
            _channel.Writer.TryComplete();
            while (_channel.Reader.TryRead(out var leftover)) leftover.Dispose();
        }
    }
#else
    public ScreenAudioInventory ListAudioOutputs() => new(false, [], null, false, null);

    public Task<ScreenAudioInstallResult> InstallVirtualAudioAsync(CancellationToken ct = default) =>
        Task.FromResult(new ScreenAudioInstallResult(false, false, false, null, "Windows only"));
#endif
}
