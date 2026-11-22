using System.Diagnostics;
#if WINDOWS
using System.Windows.Forms;
#endif

namespace PhoneBox.Agent;

public sealed partial class ScreenMirror
{
    public Task<bool> StreamH264Async(HttpContext context)
    {
#if WINDOWS
        return StreamH264WindowsAsync(context);
#else
        return Task.FromResult(false);
#endif
    }

#if WINDOWS
    private int? _verifiedGpuIndex;

    private async Task<bool> StreamH264WindowsAsync(HttpContext context)
    {
        var requestToken = context.RequestAborted;
        var annexB = string.Equals(context.Request.Query["format"], "annexb", StringComparison.OrdinalIgnoreCase);
        NvidiaCaptureTarget target;
        CancellationTokenSource session;
        lock (_gate)
        {
            if (!_active || _captureTarget is not { } selected ||
                _activeVideo is { IsCancellationRequested: false }) return false;
            target = selected;
            session = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
            _activeVideo = session;
            _lastRequest = Stopwatch.GetTimestamp();
        }

        try
        {
            var ffmpeg = FindFfmpeg();
            if (ffmpeg is null) return Fail("Không tìm thấy FFmpeg trong PhoneBox Agent");
            if (_verifiedGpuIndex != target.NvidiaGpuIndex)
            {
                if (!await EncoderReadyAsync(ffmpeg, target.NvidiaGpuIndex, session.Token))
                    return Fail("Không khởi động được bộ mã hóa NVIDIA NVENC");
                _verifiedGpuIndex = target.NvidiaGpuIndex;
            }
            if (await StreamWithNvencAsync(context, target, ffmpeg, annexB, session.Token)) return true;
            return Fail("Không mở được luồng màn hình qua NVIDIA NVENC");
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            return true;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeVideo, session)) _activeVideo = null;
                _lastRequest = Stopwatch.GetTimestamp();
            }
            session.Dispose();
            ScheduleIdleCheck();
        }
    }

    private static string? FindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("PHONEBOX_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;
        var installed = Path.Combine(AppPaths.InstallDir, "ffmpeg", "ffmpeg.exe");
        return File.Exists(installed) ? installed : null;
    }

    private static async Task<bool> EncoderReadyAsync(string ffmpeg, int gpuIndex,
        CancellationToken cancellationToken)
    {
        var start = NewProcessStart(ffmpeg);
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=1",
            "-frames:v", "1", "-an", "-c:v", "h264_nvenc", "-gpu", gpuIndex.ToString(),
            "-preset", "p1", "-tune", "ull", "-rc", "cbr", "-f", "null", "-",
        }) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return false;
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
                try { await errors; } catch (OperationCanceledException) { }
                return false;
            }
            await errors;
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or
                                   OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return false;
        }
    }

    private async Task<bool> StreamWithNvencAsync(HttpContext context, NvidiaCaptureTarget target,
        string ffmpeg, bool annexB, CancellationToken cancellationToken)
    {
        var start = NewProcessStart(ffmpeg);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats", "-nostdin",
            "-init_hw_device", $"d3d11va=phonebox:{target.AdapterIndex}", "-filter_hw_device", "phonebox",
            "-f", "lavfi", "-i",
            $"ddagrab=output_idx={target.OutputIndex}:framerate=60:draw_mouse=1:dup_frames={(annexB ? 0 : 1)}",
            "-an", "-c:v", "h264_nvenc", "-gpu", target.NvidiaGpuIndex.ToString(),
            "-preset", "p1", "-tune", "ull", "-zerolatency", "1", "-delay", "0",
            "-rc", "vbr", "-cq", "22", "-b:v", "5M", "-maxrate", "8M", "-bufsize", "350K",
            "-g", annexB ? "60" : "12", "-bf", "0",
        };
        if (annexB)
        {
            args.AddRange(["-aud", "1", "-flush_packets", "1", "-f", "h264", "pipe:1"]);
        }
        else
        {
            args.AddRange([
                "-fps_mode", "cfr",
                "-movflags", "+empty_moov+default_base_moof+frag_keyframe",
                "-frag_duration", "1000", "-flush_packets", "1", "-f", "mp4", "pipe:1",
            ]);
        }
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }

        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var buffer = new byte[64 * 1024];
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(7));
                int count;
                try { count = await process.StandardOutput.BaseStream.ReadAsync(buffer, startup.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
                if (count == 0) return false;
                context.Response.ContentType = annexB ? "application/octet-stream" : "video/mp4";
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Accel-Buffering"] = "no";
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }

            while (true)
            {
                var count = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                lock (_gate)
                {
                    if (!_active) break;
                    _lastRequest = Stopwatch.GetTimestamp();
                }
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return true; }
        catch (IOException) { return true; }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            try { await errors; } catch (OperationCanceledException) { }
        }
    }

    private static ProcessStartInfo NewProcessStart(string ffmpeg)
    {
        var folder = Path.GetDirectoryName(ffmpeg)!;
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["PATH"] = folder + Path.PathSeparator +
            (start.Environment.TryGetValue("PATH", out var path) ? path : "");
        return start;
    }
#endif
}
