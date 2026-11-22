using System.Buffers.Binary;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

public sealed class ScreenMirrorAudioTests
{
    [Fact]
    public void AudioWireMetaAndFrameHeaderEncodeLittleEndianFormat()
    {
        var meta = ScreenAudioProtocol.BuildAudioMeta(active: true, isVirtual: true, sampleRate: 48_000, channels: 2, frameSamples: 960);
        Assert.Equal(10, meta.Length);
        Assert.Equal(ScreenAudioProtocol.OpcodeAudioMeta, meta[0]);
        Assert.Equal(ScreenAudioProtocol.CodecPcm16, meta[1]);
        Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(meta.AsSpan(2, 4)));
        Assert.Equal(2, meta[6]);
        Assert.Equal((ushort)960, BinaryPrimitives.ReadUInt16LittleEndian(meta.AsSpan(7, 2)));
        Assert.Equal(0x03, meta[9]);

        var offMeta = ScreenAudioProtocol.BuildAudioMeta(active: false, isVirtual: false);
        Assert.Equal(ScreenAudioProtocol.CodecNone, offMeta[1]);
        Assert.Equal(0x00, offMeta[9]);

        var hdr = new byte[ScreenAudioProtocol.FrameHeaderBytes];
        ScreenAudioProtocol.WriteAudioFrameHeader(hdr, payloadLen: 3840, seq: 42, ptsUs: 123_456_789L);
        Assert.Equal(ScreenAudioProtocol.OpcodeAudioFrame, hdr[0]);
        Assert.Equal((ushort)3840, BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(1, 2)));
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(3, 4)));
        Assert.Equal(123_456_789L, BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(7, 8)));
    }

    [Theory]
    [InlineData("off", ScreenAudioMode.Off)]
    [InlineData("default", ScreenAudioMode.Default)]
    [InlineData("VIRTUAL", ScreenAudioMode.Virtual)]
    [InlineData(" Device ", ScreenAudioMode.Device)]
    [InlineData(null, ScreenAudioMode.Off)]
    [InlineData("", ScreenAudioMode.Off)]
    [InlineData("unknown", ScreenAudioMode.Off)]
    public void ParseAudioModeHandlesValidAndFallbackValues(string? raw, ScreenAudioMode expected)
    {
        Assert.Equal(expected, ScreenAudioProtocol.ParseAudioMode(raw));
    }

    [Fact]
    public void SelectAudioEndpointAppliesVirtualPriorityAndDeviceRules()
    {
        var realtek = new ScreenAudioEndpoint("id-realtek", "Speakers (Realtek(R) Audio)", IsDefault: true, IsVirtual: false);
        var steam = new ScreenAudioEndpoint("id-steam", "Steam Streaming Speakers", IsDefault: false, IsVirtual: true);
        var vad = new ScreenAudioEndpoint("id-vad", "Virtual Audio Driver by MTT", IsDefault: false, IsVirtual: true);
        var vbcable = new ScreenAudioEndpoint("id-vb", "CABLE Input (VB-Audio Virtual Cable)", IsDefault: false, IsVirtual: true);

        ScreenAudioEndpoint[] all = [realtek, steam, vad, vbcable];

        Assert.Null(ScreenAudioProtocol.SelectAudioEndpoint(ScreenAudioMode.Off, "", all));
        Assert.Equal(realtek, ScreenAudioProtocol.SelectAudioEndpoint(ScreenAudioMode.Default, "", all));
        Assert.Equal(vbcable, ScreenAudioProtocol.SelectAudioEndpoint(ScreenAudioMode.Virtual, "", all));
        Assert.Equal(vad, ScreenAudioProtocol.SelectAudioEndpoint(ScreenAudioMode.Virtual, "", [realtek, steam, vad]));
        Assert.Equal(steam, ScreenAudioProtocol.SelectAudioEndpoint(ScreenAudioMode.Virtual, "", [realtek, steam]));
        Assert.Null(ScreenAudioProtocol.SelectAudioEndpoint(ScreenAudioMode.Virtual, "", [realtek]));

        Assert.Equal(vad, ScreenAudioProtocol.SelectAudioEndpoint(ScreenAudioMode.Device, "ID-VAD", all));
        Assert.Equal(realtek, ScreenAudioProtocol.SelectAudioEndpoint(ScreenAudioMode.Device, "", all));
        Assert.Null(ScreenAudioProtocol.SelectAudioEndpoint(ScreenAudioMode.Device, "missing-id", all));
    }

    [Fact]
    public void AudioEndpointsRemainLocalOnlyAndBlockedOnLanAndUsb()
    {
        Assert.False(Lan.AllowedOverLan(new PathString("/api/screen/audio/devices")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/screen/audio/install-virtual")));
        Assert.False(UsbLink.Allowed("GET", new PathString("/api/screen/audio/devices")));
        Assert.False(UsbLink.Allowed("POST", new PathString("/api/screen/audio/install-virtual")));
    }

    [Fact]
    public void ConverterPassthrough48kStereoFloatEmitsExact20msChunks()
    {
        var conv = new ScreenAudioConverter(48_000, 2, 32, sourceIsFloat: true);
        var input = new byte[960 * 2 * 4];
        for (var i = 0; i < 960; i++)
        {
            var l = i % 2 == 0 ? 0.5f : -0.5f;
            var r = -0.25f;
            BinaryPrimitives.WriteSingleLittleEndian(input.AsSpan(i * 8, 4), l);
            BinaryPrimitives.WriteSingleLittleEndian(input.AsSpan(i * 8 + 4, 4), r);
        }

        var frames = new List<byte[]>();
        conv.Process(input, isSilentFlag: false, span => frames.Add(span.ToArray()));

        Assert.Single(frames);
        Assert.Equal(ScreenAudioProtocol.FramePcmBytes, frames[0].Length);
        var s0L = BinaryPrimitives.ReadInt16LittleEndian(frames[0].AsSpan(0, 2));
        var s0R = BinaryPrimitives.ReadInt16LittleEndian(frames[0].AsSpan(2, 2));
        var s1L = BinaryPrimitives.ReadInt16LittleEndian(frames[0].AsSpan(4, 2));
        Assert.InRange(s0L, 16380, 16390);
        Assert.InRange(s0R, -8200, -8188);
        Assert.InRange(s1L, -16390, -16380);
    }

    [Fact]
    public void ConverterResamples44100To48000AcrossCallbacksWithoutPhaseReset()
    {
        var conv = new ScreenAudioConverter(44_100, 2, 32, sourceIsFloat: true);
        var totalFrames = 0;
        var chunk = new byte[441 * 2 * 4]; // 10ms @ 44.1kHz
        for (var c = 0; c < 10; c++) // 100ms total -> ~4800 dst samples -> 5 frames (20ms each)
        {
            for (var i = 0; i < 441; i++)
            {
                var val = MathF.Sin((c * 441 + i) * 0.05f) * 0.4f;
                BinaryPrimitives.WriteSingleLittleEndian(chunk.AsSpan(i * 8, 4), val);
                BinaryPrimitives.WriteSingleLittleEndian(chunk.AsSpan(i * 8 + 4, 4), val);
            }
            conv.Process(chunk, isSilentFlag: false, _ => totalFrames++);
        }

        Assert.InRange(totalFrames, 4, 5);
    }

    [Fact]
    public void ConverterDownmixesMonoAndSurroundWithoutOverflow()
    {
        // Mono 16-bit -> stereo
        var monoConv = new ScreenAudioConverter(48_000, 1, 16, sourceIsFloat: false);
        var monoIn = new byte[960 * 2];
        for (var i = 0; i < 960; i++)
            BinaryPrimitives.WriteInt16LittleEndian(monoIn.AsSpan(i * 2, 2), 12000);
        byte[]? monoOut = null;
        monoConv.Process(monoIn, isSilentFlag: false, s => monoOut = s.ToArray());
        Assert.NotNull(monoOut);
        Assert.Equal(12000, BinaryPrimitives.ReadInt16LittleEndian(monoOut.AsSpan(0, 2)));
        Assert.Equal(12000, BinaryPrimitives.ReadInt16LittleEndian(monoOut.AsSpan(2, 2)));

        // 5.1 surround 24-bit PCM -> clamped stereo without wraparound
        var surConv = new ScreenAudioConverter(48_000, 6, 24, sourceIsFloat: false);
        var surIn = new byte[960 * 6 * 3];
        for (var i = 0; i < 960; i++)
        {
            for (var ch = 0; ch < 6; ch++)
            {
                var off = (i * 6 + ch) * 3;
                // Max positive 24-bit int: 0x7FFFFF
                surIn[off] = 0xFF;
                surIn[off + 1] = 0xFF;
                surIn[off + 2] = 0x7F;
            }
        }
        byte[]? surOut = null;
        surConv.Process(surIn, isSilentFlag: false, s => surOut = s.ToArray());
        Assert.NotNull(surOut);
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(surOut.AsSpan(0, 2)));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(surOut.AsSpan(2, 2)));
    }

    [Fact]
    public void ConverterSuppressesSilenceAfterTailAndResumesImmediatelyOnSignal()
    {
        var conv = new ScreenAudioConverter(48_000, 2, 16, sourceIsFloat: false);
        var silentChunk = new byte[960 * 2 * 2];
        var emitted = 0;

        for (var i = 0; i < 25; i++)
            conv.Process(silentChunk, isSilentFlag: true, _ => emitted++);
        Assert.Equal(10, emitted);

        var loudChunk = new byte[960 * 2 * 2];
        for (var i = 0; i < 960; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(loudChunk.AsSpan(i * 4, 2), 8000);
            BinaryPrimitives.WriteInt16LittleEndian(loudChunk.AsSpan(i * 4 + 2, 2), -8000);
        }
        conv.Process(loudChunk, isSilentFlag: false, _ => emitted++);
        Assert.Equal(11, emitted);
    }
}
