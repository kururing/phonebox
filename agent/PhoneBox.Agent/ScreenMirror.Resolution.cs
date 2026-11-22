using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace PhoneBox.Agent;

public sealed partial class ScreenMirror
{
#if WINDOWS
    private static string? ApplyPhoneDisplayResolution(string deviceName, int width, int height,
        string? preferredGpuName = null)
    {
        if (width is < 320 or > 16384 || height is < 200 or > 16384 || (long)width * height > 67_108_864)
            return "Độ phân giải màn hình điện thoại không hợp lệ.";

        const string configPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";
        if (!File.Exists(configPath))
            return "Không tìm thấy cấu hình Virtual Display Driver.";

        try
        {
            var displayCount = EnsureVddResolution(configPath, width, height, preferredGpuName);
            if (TrySetDisplayMode(deviceName, width, height)) return null;
            if (!ReloadVddConfiguration(displayCount))
                return "Không tải được độ phân giải điện thoại vào Virtual Display Driver.";

            var deadline = Environment.TickCount64 + 2500;
            do
            {
                Thread.Sleep(100);
                var currentDevice = SecondaryDisplayDeviceName();
                if (currentDevice is null)
                {
                    AttachVddDisplay();
                    currentDevice = SecondaryDisplayDeviceName();
                }
                if (currentDevice is not null && TrySetDisplayMode(currentDevice, width, height)) return null;
            } while (Environment.TickCount64 < deadline);

            return $"Windows không chấp nhận độ phân giải màn phụ {width} × {height}.";
        }
        catch (UnauthorizedAccessException)
        {
            return "PhoneBox không có quyền cập nhật cấu hình Virtual Display Driver.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Xml.XmlException or
                                   ArgumentException or ExternalException)
        {
            return "Không thể cập nhật độ phân giải màn phụ: " + ex.Message;
        }
    }

    internal static int EnsureVddResolution(string path, int width, int height, string? preferredGpuName = null)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidOperationException("VDD settings are empty.");
        var resolutions = root.Element("resolutions")
            ?? throw new InvalidOperationException("VDD settings have no resolutions section.");
        var displayCount = int.TryParse(root.Element("monitors")?.Element("count")?.Value, out var count)
            ? Math.Clamp(count, 1, 16) : 1;

        var changed = false;
        if (!string.IsNullOrWhiteSpace(preferredGpuName))
        {
            var gpu = root.Element("gpu");
            if (gpu is null)
            {
                gpu = new XElement("gpu", new XElement("friendlyname", preferredGpuName));
                root.AddFirst(gpu);
                changed = true;
            }
            else
            {
                var friendly = gpu.Element("friendlyname");
                var cur = friendly?.Value.Trim();
                if (friendly is null)
                {
                    gpu.Add(new XElement("friendlyname", preferredGpuName));
                    changed = true;
                }
                else if (string.IsNullOrEmpty(cur) || string.Equals(cur, "default", StringComparison.OrdinalIgnoreCase))
                {
                    friendly.Value = preferredGpuName;
                    changed = true;
                }
            }
        }

        var existing = resolutions.Elements("resolution").FirstOrDefault(item =>
            int.TryParse(item.Element("width")?.Value, out var w) && w == width &&
            int.TryParse(item.Element("height")?.Value, out var h) && h == height);
        if (existing is not null)
        {
            var rateEl = existing.Element("refresh_rate");
            if (rateEl is null)
            {
                existing.Add(new XElement("refresh_rate", 60));
                changed = true;
            }
            else if (!int.TryParse(rateEl.Value, out var rate) || rate < 60)
            {
                rateEl.Value = "60";
                changed = true;
            }
        }
        else
        {
            resolutions.Add(new XElement("resolution",
                new XElement("width", width),
                new XElement("height", height),
                new XElement("refresh_rate", 60)));
            changed = true;
        }

        if (!changed) return displayCount;

        var backup = path + ".phonebox-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-ffff") + ".bak";
        var temp = path + ".phonebox.tmp";
        try
        {
            doc.Save(temp, SaveOptions.DisableFormatting);
            File.Copy(path, backup, overwrite: false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return displayCount;
    }

    private static long _lastVddReloadTick = long.MinValue;
    private static readonly long VddReloadCooldownTicks = (long)(15.0 * Stopwatch.Frequency);

    private static bool ReloadVddConfiguration(int displayCount)
    {
        var now = Stopwatch.GetTimestamp();
        var prev = Volatile.Read(ref _lastVddReloadTick);
        if (prev != long.MinValue && (now - prev) < VddReloadCooldownTicks)
            return false;
        Volatile.Write(ref _lastVddReloadTick, now);
        try
        {
            using var pipe = new NamedPipeClientStream(".", "MTTVirtualDisplayPipe", PipeDirection.InOut);
            pipe.Connect(2000);
            var command = Encoding.Unicode.GetBytes($"SETDISPLAYCOUNT {displayCount}");
            pipe.Write(command, 0, command.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TrySetDisplayMode(string deviceName, int width, int height)
    {
        var mode = CurrentDisplayMode(deviceName);
        if (mode.Width == (uint)width && mode.Height == (uint)height && mode.Frequency == 60)
            return true;
        mode.Width = (uint)width;
        mode.Height = (uint)height;
        mode.Frequency = 60;
        mode.Fields = 0x80000 | 0x100000 | 0x400000;
        return ChangeDisplaySettingsEx(deviceName, ref mode, 0, 0x2, 0) == 0 &&
               ChangeDisplaySettingsEx(deviceName, ref mode, 0, 0, 0) == 0;
    }

    private static DisplayMode CurrentDisplayMode(string deviceName)
    {
        var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>(), DeviceName = "", FormName = "" };
        EnumDisplaySettingsEx(deviceName, -1, ref mode, 0);
        return mode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int X, Y;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint Bits, Width, Height, Flags, Frequency, IcmMethod, IcmIntent, Media, Dither, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string name, int mode, ref DisplayMode settings, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string name, ref DisplayMode settings, nint window,
        uint flags, nint parameter);
#endif
}
