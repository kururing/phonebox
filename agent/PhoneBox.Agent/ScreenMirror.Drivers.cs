using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
#if WINDOWS
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
#endif

namespace PhoneBox.Agent;

public sealed record VddDriverStatus(
    bool Supported,
    bool Installed,
    bool Ready,
    bool Disabled,
    bool ConfigExists,
    string? DeviceName,
    string? InstanceId,
    string? Version,
    string? DriverHint,
    string? DownloadUrl,
    string? Error);

public sealed record ScreenDriversStatus(
    bool Supported,
    VddDriverStatus Display,
    ScreenAudioInventory Audio,
    string? AudioDownloadUrl);

public sealed record VddInstallResult(
    bool Ok,
    bool AlreadyInstalled,
    bool RebootRequired,
    VddDriverStatus Status,
    string? Error);

public sealed partial class ScreenMirror
{
    private const string VddConfigPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";
    private const string VddDriverHint = "Virtual Display Driver (MikeTheTech / MttVDD)";
    private const string VddReleasesUrl = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases";
    private const string AudioDownloadPageUrl = "https://vb-audio.com/Cable/";
    private readonly SemaphoreSlim _vddInstallGate = new(1, 1);

    internal static bool IsVddHardwareId(string? hwId) =>
        !string.IsNullOrWhiteSpace(hwId) && hwId.Contains("MttVDD", StringComparison.OrdinalIgnoreCase);

    internal static (string PrimaryUrl, string FallbackUrl) SelectVddDownloadUrls(Architecture arch) =>
        arch == Architecture.Arm64
            ? (
                "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-ARM64.Driver.Only.zip",
                "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/24.10.27/VirtualDisplayDriver-ARM64.zip"
            )
            : (
                "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip",
                "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/24.10.27/VirtualDisplayDriver-x64.zip"
            );

    internal static VddDriverStatus EvaluateVddStatus(
        bool installed,
        bool disabled,
        uint problemCode,
        bool configExists,
        string? deviceName,
        string? instanceId,
        string? version)
    {
        if (!installed)
        {
            return new VddDriverStatus(
                Supported: true,
                Installed: false,
                Ready: false,
                Disabled: false,
                ConfigExists: configExists,
                DeviceName: null,
                InstanceId: null,
                Version: null,
                DriverHint: VddDriverHint,
                DownloadUrl: VddReleasesUrl,
                Error: null);
        }

        string? error = null;
        var ready = false;
        if (disabled)
            error = "Thiết bị Virtual Display Driver đang bị tắt trong Device Manager.";
        else if (problemCode != 0)
            error = $"Virtual Display Driver đang gặp lỗi thiết bị Windows (mã {problemCode}).";
        else if (!configExists)
            error = @"Thiếu tệp cấu hình C:\VirtualDisplayDriver\vdd_settings.xml.";
        else
            ready = true;

        return new VddDriverStatus(
            Supported: true,
            Installed: true,
            Ready: ready,
            Disabled: disabled,
            ConfigExists: configExists,
            DeviceName: deviceName,
            InstanceId: instanceId,
            Version: version,
            DriverHint: VddDriverHint,
            DownloadUrl: VddReleasesUrl,
            Error: error);
    }

    internal static void EnsureVddConfigFile(string targetConfigPath, string? templateConfigPath, string? preferredGpuName)
    {
        var dir = Path.GetDirectoryName(targetConfigPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(targetConfigPath) &&
            !string.IsNullOrWhiteSpace(templateConfigPath) &&
            File.Exists(templateConfigPath))
        {
            File.Copy(templateConfigPath, targetConfigPath, overwrite: false);
        }

        if (!File.Exists(targetConfigPath) || string.IsNullOrWhiteSpace(preferredGpuName))
            return;

        var doc = XDocument.Load(targetConfigPath, LoadOptions.PreserveWhitespace);
        var root = doc.Root;
        if (root is null) return;

        var changed = false;
        var gpu = root.Element("gpu");
        if (gpu is null)
        {
            root.AddFirst(new XElement("gpu", new XElement("friendlyname", preferredGpuName)));
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

        if (!changed) return;

        var temp = targetConfigPath + ".phonebox.tmp";
        try
        {
            doc.Save(temp, SaveOptions.DisableFormatting);
            File.Move(temp, targetConfigPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public ScreenDriversStatus GetDriversStatus() =>
        new(OperatingSystem.IsWindows(), GetVddDriverStatus(), ListAudioOutputs(), AudioDownloadPageUrl);

#if WINDOWS
    private const uint DigcfPresent = 0x00000002;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpHardwareId = 0x00000001;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint DicsFlagGlobal = 0x00000001;
    private const uint DiregDrv = 0x00000002;
    private const uint KeyRead = 0x20019;
    private const uint DnHasProblem = 0x00000400;
    private const uint CmProbDisabled = 22;
    private const uint DicdGenerateId = 0x00000001;
    private const uint DifRemove = 0x00000005;
    private const uint DifRegisterDevice = 0x00000019;
    private const uint InstallFlagForce = 0x00000001;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly Guid GuidDevClassDisplay = new("4d36e968-e325-11ce-bfc1-08002be10318");

    private sealed record VddPnpDeviceInfo(string DeviceName, string InstanceId, string? Version, bool Disabled, uint ProblemCode);

    public VddDriverStatus GetVddDriverStatus()
    {
        var configExists = File.Exists(VddConfigPath);
        try
        {
            var pnp = FindVddPnpDevice();
            if (pnp is not null)
                return EvaluateVddStatus(true, pnp.Disabled, pnp.ProblemCode, configExists, pnp.DeviceName, pnp.InstanceId, pnp.Version);

            if (HasAnyVddDisplayTarget())
                return EvaluateVddStatus(true, false, 0, configExists, "Virtual Display Driver", null, null);

            return EvaluateVddStatus(false, false, 0, configExists, null, null, null);
        }
        catch
        {
            return EvaluateVddStatus(false, false, 0, configExists, null, null, null);
        }
    }

    public async Task<VddInstallResult> InstallVddAsync(CancellationToken ct = default)
    {
        if (!_vddInstallGate.Wait(0))
            return new VddInstallResult(false, false, false, GetVddDriverStatus(), "Đang cài đặt Virtual Display Driver...");

        string? tempDir = null;
        try
        {
            var current = GetVddDriverStatus();
            if (current.Installed && current.Ready)
                return new VddInstallResult(true, true, false, current, null);

            tempDir = Path.Combine(Path.GetTempPath(), "phonebox-vdd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var resultPath = Path.Combine(tempDir, "result.txt");

            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                return new VddInstallResult(false, false, false, current, "Không xác định được tệp chạy PhoneBoxAgent.");

            var psi = new ProcessStartInfo(processPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            if (current.Installed && current.Disabled && current.ConfigExists && !string.IsNullOrWhiteSpace(current.InstanceId))
            {
                psi.ArgumentList.Add("--install-vdd");
                psi.ArgumentList.Add("--enable-only");
                psi.ArgumentList.Add(current.InstanceId);
                psi.ArgumentList.Add("--result");
                psi.ArgumentList.Add(resultPath);
            }
            else
            {
                var zipPath = Path.Combine(tempDir, "vdd.zip");
                var extractDir = Path.Combine(tempDir, "pkg");
                Directory.CreateDirectory(extractDir);

                var (primaryUrl, fallbackUrl) = SelectVddDownloadUrls(RuntimeInformation.OSArchitecture);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneBoxAgent/1.0");
                Exception? dlErr = null;
                var downloaded = false;
                foreach (var url in new[] { primaryUrl, fallbackUrl })
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
                    return new VddInstallResult(false, false, false, GetVddDriverStatus(),
                        "Không tải được Virtual Display Driver: " + (dlErr?.Message ?? "lỗi mạng"));

                ExtractZipSafely(zipPath, extractDir);

                var infPath = Directory.EnumerateFiles(extractDir, "*.inf", SearchOption.AllDirectories)
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "MttVDD.inf", StringComparison.OrdinalIgnoreCase));
                if (infPath is null)
                    return new VddInstallResult(false, false, false, GetVddDriverStatus(),
                        "Không tìm thấy tệp MttVDD.inf trong gói Virtual Display Driver.");

                psi.ArgumentList.Add("--install-vdd");
                psi.ArgumentList.Add("--inf");
                psi.ArgumentList.Add(infPath);
                psi.ArgumentList.Add("--result");
                psi.ArgumentList.Add(resultPath);
            }

            using var proc = new Process { StartInfo = psi };
            try
            {
                if (!proc.Start())
                    return new VddInstallResult(false, false, false, GetVddDriverStatus(),
                        "Không khởi chạy được trình cài đặt Virtual Display Driver.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new VddInstallResult(false, false, false, GetVddDriverStatus(),
                    "Đã hủy cài đặt (UAC bị từ chối).");
            }

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(90));
            await proc.WaitForExitAsync(waitCts.Token);

            var deadline = Environment.TickCount64 + 5000;
            VddDriverStatus latest;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                latest = GetVddDriverStatus();
                if (latest.Installed && latest.Ready)
                    return new VddInstallResult(true, false, false, latest, null);
                if (Environment.TickCount64 >= deadline)
                    break;
                await Task.Delay(250, ct);
            }

            string? workerMsg = null;
            if (File.Exists(resultPath))
            {
                try { workerMsg = File.ReadAllText(resultPath, Encoding.UTF8).Trim(); } catch { }
            }

            if (proc.ExitCode == 3010 || (proc.ExitCode == 0 && latest.Installed))
            {
                var msg = !string.IsNullOrWhiteSpace(workerMsg)
                    ? workerMsg
                    : "Đã cài Virtual Display Driver; khởi động lại Windows để hoàn tất.";
                return new VddInstallResult(true, false, true, latest, msg);
            }

            return new VddInstallResult(
                false,
                false,
                false,
                latest,
                !string.IsNullOrWhiteSpace(workerMsg)
                    ? workerMsg
                    : $"Trình cài Virtual Display Driver thoát với mã {proc.ExitCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new VddInstallResult(false, false, false, GetVddDriverStatus(), "Đã hủy cài đặt.");
        }
        catch (Exception ex)
        {
            return new VddInstallResult(false, false, false, GetVddDriverStatus(), ex.Message);
        }
        finally
        {
            if (tempDir is not null)
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
            _vddInstallGate.Release();
        }
    }

    internal static int RunVddInstallerWorker(string[] args)
    {
        string? enableInstanceId = null;
        string? infPath = null;
        string? resultPath = null;

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--enable-only" && i + 1 < args.Length)
                enableInstanceId = args[++i];
            else if (args[i] == "--inf" && i + 1 < args.Length)
                infPath = args[++i];
            else if (args[i] == "--result" && i + 1 < args.Length)
                resultPath = args[++i];
        }

        void WriteResult(string? message)
        {
            if (string.IsNullOrWhiteSpace(resultPath) || string.IsNullOrWhiteSpace(message)) return;
            try { File.WriteAllText(resultPath, message.Trim(), Encoding.UTF8); } catch { }
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(enableInstanceId))
            {
                var (code, output) = RunPnpUtil("/enable-device", enableInstanceId);
                if (code == 0 || code == 3010) return code;
                WriteResult(string.IsNullOrWhiteSpace(output)
                    ? $"Không bật lại được Virtual Display Driver (pnputil mã {code})."
                    : output);
                return code != 0 ? code : 1;
            }

            if (string.IsNullOrWhiteSpace(infPath) || !File.Exists(infPath))
            {
                WriteResult("Không tìm thấy tệp MttVDD.inf để cài đặt.");
                return 1;
            }

            var infDir = Path.GetDirectoryName(infPath)!;
            var templateCfg = Path.Combine(infDir, "vdd_settings.xml");
            EnsureVddConfigFile(VddConfigPath, templateCfg, FindPreferredNvidiaAdapterName());
            TryGrantVddConfigUsersModifyAccess();

            var catPath = Directory.EnumerateFiles(infDir, "*.cat", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), "mttvdd.cat", StringComparison.OrdinalIgnoreCase));
            if (catPath is not null)
                TryImportDriverCatalogCertificates(catPath);

            var (addCode, addOut) = RunPnpUtil("/add-driver", infPath, "/install");
            if (addCode is not 0 and not 3010 and not 259)
            {
                WriteResult(string.IsNullOrWhiteSpace(addOut)
                    ? $"Không cài được gói driver (pnputil mã {addCode})."
                    : addOut);
                return 1;
            }

            var pnpReboot = addCode == 3010;
            var existing = FindVddPnpDevice();
            if (existing is not null)
            {
                if (existing.Disabled)
                {
                    var (enCode, _) = RunPnpUtil("/enable-device", existing.InstanceId);
                    if (enCode == 3010) pnpReboot = true;
                }

                if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, @"Root\MttVDD", infPath, InstallFlagForce, out var updReboot))
                {
                    var err = Marshal.GetLastWin32Error();
                    WriteResult($"Không cập nhật được Virtual Display Driver (Win32 lỗi {err}).");
                    return 1;
                }
                return (pnpReboot || updReboot) ? 3010 : 0;
            }

            var classNameBuf = new char[64];
            if (!SetupDiGetINFClassW(infPath, out var classGuid, classNameBuf, (uint)classNameBuf.Length, out _))
            {
                var err = Marshal.GetLastWin32Error();
                WriteResult($"Không đọc được INF class từ MttVDD.inf (Win32 lỗi {err}).");
                return 1;
            }

            var classNameLen = Array.IndexOf(classNameBuf, '\0');
            var className = classNameLen >= 0 ? new string(classNameBuf, 0, classNameLen) : new string(classNameBuf);
            var devInfoSet = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
            if (devInfoSet == InvalidHandleValue)
            {
                var err = Marshal.GetLastWin32Error();
                WriteResult($"SetupDiCreateDeviceInfoList thất bại (Win32 lỗi {err}).");
                return 1;
            }

            try
            {
                var devInfoData = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiCreateDeviceInfoW(devInfoSet, className, ref classGuid, null, IntPtr.Zero, DicdGenerateId, ref devInfoData))
                {
                    var err = Marshal.GetLastWin32Error();
                    WriteResult($"SetupDiCreateDeviceInfoW thất bại (Win32 lỗi {err}).");
                    return 1;
                }

                var hwIdBytes = Encoding.Unicode.GetBytes("Root\\MttVDD\0\0");
                if (!SetupDiSetDeviceRegistryPropertyW(devInfoSet, ref devInfoData, SpdrpHardwareId, hwIdBytes, (uint)hwIdBytes.Length))
                {
                    var err = Marshal.GetLastWin32Error();
                    WriteResult($"SetupDiSetDeviceRegistryPropertyW thất bại (Win32 lỗi {err}).");
                    return 1;
                }

                if (!SetupDiCallClassInstaller(DifRegisterDevice, devInfoSet, ref devInfoData))
                {
                    var err = Marshal.GetLastWin32Error();
                    WriteResult($"SetupDiCallClassInstaller(DIF_REGISTERDEVICE) thất bại (Win32 lỗi {err}).");
                    return 1;
                }

                if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, @"Root\MttVDD", infPath, InstallFlagForce, out var installReboot))
                {
                    var err = Marshal.GetLastWin32Error();
                    SetupDiCallClassInstaller(DifRemove, devInfoSet, ref devInfoData);
                    WriteResult($"Cài đặt Virtual Display Driver thất bại (Win32 lỗi {err}).");
                    return 1;
                }

                return (pnpReboot || installReboot) ? 3010 : 0;
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfoSet);
            }
        }
        catch (Exception ex)
        {
            WriteResult(ex.Message);
            return 1;
        }
    }

    private static void ExtractZipSafely(string zipPath, string extractDir)
    {
        var fullRoot = Path.GetFullPath(extractDir);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
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

    private static void TryGrantVddConfigUsersModifyAccess()
    {
        try
        {
            var dirPath = Path.GetDirectoryName(VddConfigPath);
            if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath)) return;
            var dirInfo = new DirectoryInfo(dirPath);
            var security = dirInfo.GetAccessControl();
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                usersSid,
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            dirInfo.SetAccessControl(security);
        }
        catch { }
    }

    private static void TryImportDriverCatalogCertificates(string catPath)
    {
        var certs = new X509Certificate2Collection();
        try
        {
#pragma warning disable SYSLIB0057
            certs.Import(catPath);
#pragma warning restore SYSLIB0057
            if (certs.Count == 0) return;
            using var store = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (var cert in certs)
                store.Add(cert);
        }
        catch { }
        finally
        {
            foreach (var cert in certs)
            {
                try { cert.Dispose(); } catch { }
            }
        }
    }

    private static (int ExitCode, string Output) RunPnpUtil(params string[] arguments)
    {
        var start = new ProcessStartInfo("pnputil.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);

        using var proc = Process.Start(start);
        if (proc is null) return (-1, "Không khởi chạy được pnputil.exe.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stderr}\n{stdout}";
        return (proc.ExitCode, combined.Trim());
    }

    private static bool HasAnyVddDisplayTarget()
    {
        if (QueryDisplayConfigPaths(QdcAllPaths, out var allPaths, out _) != 0)
            return false;
        return allPaths.Any(p => IsVddTarget(p.TargetInfo.AdapterId, p.TargetInfo.Id));
    }

    private static VddPnpDeviceInfo? FindVddPnpDevice()
    {
        var classGuid = GuidDevClassDisplay;
        var devInfoSet = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, DigcfPresent);
        if (devInfoSet == InvalidHandleValue) return null;

        try
        {
            var devInfoData = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(devInfoSet, index, ref devInfoData); index++)
            {
                var hwIds = ReadDeviceMultiSzProperty(devInfoSet, ref devInfoData, SpdrpHardwareId);
                if (!hwIds.Any(IsVddHardwareId))
                {
                    devInfoData.CbSize = (uint)Marshal.SizeOf<SpDevInfoData>();
                    continue;
                }

                var friendly = ReadDeviceStringProperty(devInfoSet, ref devInfoData, SpdrpFriendlyName);
                var desc = ReadDeviceStringProperty(devInfoSet, ref devInfoData, SpdrpDeviceDesc);
                var deviceName = !string.IsNullOrWhiteSpace(friendly) ? friendly
                    : !string.IsNullOrWhiteSpace(desc) ? desc
                    : "Virtual Display Driver";

                var instanceId = ReadDeviceInstanceId(devInfoSet, ref devInfoData) ?? "";
                var version = ReadDeviceDriverVersion(devInfoSet, ref devInfoData);

                var disabled = false;
                uint problemCode = 0;
                if (CM_Get_DevNode_Status(out var status, out var problem, devInfoData.DevInst, 0) == 0)
                {
                    if ((status & DnHasProblem) != 0)
                    {
                        problemCode = problem;
                        disabled = problem == CmProbDisabled;
                    }
                }

                return new VddPnpDeviceInfo(deviceName, instanceId, version, disabled, problemCode);
            }

            return null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }
    }

    private static string[] ReadDeviceMultiSzProperty(IntPtr devInfoSet, ref SpDevInfoData devInfoData, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(devInfoSet, ref devInfoData, property, out _, null, 0, out var required);
        if (required == 0) return [];
        var buf = new byte[required];
        if (!SetupDiGetDeviceRegistryPropertyW(devInfoSet, ref devInfoData, property, out _, buf, required, out _))
            return [];
        return Encoding.Unicode.GetString(buf)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? ReadDeviceStringProperty(IntPtr devInfoSet, ref SpDevInfoData devInfoData, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(devInfoSet, ref devInfoData, property, out _, null, 0, out var required);
        if (required == 0) return null;
        var buf = new byte[required];
        if (!SetupDiGetDeviceRegistryPropertyW(devInfoSet, ref devInfoData, property, out _, buf, required, out _))
            return null;
        var s = Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static string? ReadDeviceInstanceId(IntPtr devInfoSet, ref SpDevInfoData devInfoData)
    {
        var buf = new char[256];
        if (SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, buf, (uint)buf.Length, out var required))
        {
            var len = Array.IndexOf(buf, '\0');
            return len >= 0 ? new string(buf, 0, len) : new string(buf);
        }
        if (required > buf.Length)
        {
            buf = new char[required];
            if (SetupDiGetDeviceInstanceIdW(devInfoSet, ref devInfoData, buf, (uint)buf.Length, out _))
            {
                var len = Array.IndexOf(buf, '\0');
                return len >= 0 ? new string(buf, 0, len) : new string(buf);
            }
        }
        return null;
    }

    private static string? ReadDeviceDriverVersion(IntPtr devInfoSet, ref SpDevInfoData devInfoData)
    {
        var hKey = SetupDiOpenDevRegKey(devInfoSet, ref devInfoData, DicsFlagGlobal, 0, DiregDrv, KeyRead);
        if (hKey == IntPtr.Zero || hKey == InvalidHandleValue) return null;
        try
        {
            using var safeHandle = new SafeRegistryHandle(hKey, ownsHandle: true);
            using var key = RegistryKey.FromHandle(safeHandle);
            return key.GetValue("DriverVersion")?.ToString();
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        uint property, out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        char[]? deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        uint scope, uint hwProfile, uint keyType, uint samDesired);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint dnDevInst, uint ulFlags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetINFClassW(string infName, out Guid classGuid,
        char[] className, uint classNameSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr deviceInfoSet, string deviceName, ref Guid classGuid,
        string? deviceDescription, IntPtr hwndParent, uint creationFlags, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        uint property, byte[] propertyBuffer, uint propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string hardwareId,
        string fullInfPath, uint installFlags, [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);
#else
    public VddDriverStatus GetVddDriverStatus() =>
        new(false, false, false, false, false, null, null, null, VddDriverHint, VddReleasesUrl, "Windows only");

    public Task<VddInstallResult> InstallVddAsync(CancellationToken ct = default) =>
        Task.FromResult(new VddInstallResult(false, false, false, GetVddDriverStatus(), "Windows only"));
#endif
}
