#if WINDOWS
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace PhoneBox.Agent;

/// <summary>CPU-only elevated worker. It sends readings over a user-only pipe and exits with its parent.</summary>
internal sealed class WindowsCpuSensors(ILogger log, CancellationToken cancellation)
{
    private static string PipeName(int pid) => $"PhoneBox.CpuSensors.{pid}";
    private sealed record Reading(long At, double? Celsius, string? Error);
    private volatile Reading? _last;

    public void Start()
    {
        try
        {
            var pipe = new NamedPipeServerStream(PipeName(Environment.ProcessId), PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _ = Receive(pipe);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add("--cpu-sensors");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString());
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { log.LogWarning("CPU sensors could not start: {Message}", ex.Message); }
    }

    public double? Read()
    {
        var reading = _last;
        var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (reading?.At ?? 0);
        return age is >= 0 and <= 10000 && reading?.Celsius is >= -40 and <= 125 ? reading.Celsius : null;
    }

    private async Task Receive(NamedPipeServerStream pipe)
    {
        using (pipe)
        try
        {
            await pipe.WaitForConnectionAsync(cancellation);
            using var reader = new StreamReader(pipe);
            while (await reader.ReadLineAsync(cancellation) is { } line)
                _last = JsonSerializer.Deserialize<Reading>(line);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { log.LogWarning("CPU sensor connection ended: {Message}", ex.Message); }
    }

    internal static async Task RunWorker(string parentId, string startedAt)
    {
        if (!int.TryParse(parentId, out var pid) || !long.TryParse(startedAt, out var ticks)) return;
        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks != ticks || !string.Equals(parent.MainModule?.FileName,
                Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) { parent.Dispose(); return; }
        }
        catch { return; }
        using (parent)
        {
            var computer = new Computer { IsCpuEnabled = true };
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName(pid), PipeDirection.Out, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(15000);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                computer.Open();
                while (!parent.HasExited)
                {
                    var readings = new List<(string Name, float? Value)>();
                    foreach (var hardware in computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
                    {
                        hardware.Update();
                        readings.AddRange(hardware.Sensors.Where(s => s.SensorType == SensorType.Temperature).Select(s => (s.Name, s.Value)));
                    }
                    var temp = CpuTemperature.Select(readings);
                    var data = JsonSerializer.Serialize(new Reading(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), temp,
                        temp is null ? "CPU sensor is unavailable" : null));
                    await writer.WriteLineAsync(data);
                    await Task.Delay(2000);
                }
            }
            catch { /* Parent exited, or access to the hardware is unavailable. */ }
            finally
            {
                computer.Close();
            }
        }
    }
}
#endif
