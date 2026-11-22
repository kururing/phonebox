using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

[Collection("adb session")]
public sealed class AdbReverseRecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-adb-rec-" + Guid.NewGuid().ToString("N"));
    public AdbReverseRecoveryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string CreateConfig(string fakeAdb)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            adbPath = fakeAdb,
            display = new { homeScreen = false, keepAwake = false, followComputerLock = false, openOnConnect = false },
        }));
        return path;
    }

    [Fact]
    public async Task TryRecoverHungAdbRespectsCooldownAndRecoversAfterElapsed()
    {
        var origClock = AdbReverse.Clock;
        long now = 10_000;
        AdbReverse.Clock = () => now;
        try
        {
            using var adb = new AdbReverse(new ConfigStore(CreateConfig("adb")), NullLogger<AdbReverse>.Instance);
            var kills = 0;
            adb.RecoveryCooldown = TimeSpan.FromSeconds(15);
            adb.KillAdbProcesses = () => { kills++; return 1; };

            Assert.True(await adb.TryRecoverHungAdbAsync(CancellationToken.None));
            Assert.Equal(1, kills);

            now += 5_000;
            Assert.False(await adb.TryRecoverHungAdbAsync(CancellationToken.None));
            Assert.Equal(1, kills);

            now += 10_000;
            Assert.True(await adb.TryRecoverHungAdbAsync(CancellationToken.None));
            Assert.Equal(2, kills);
        }
        finally
        {
            AdbReverse.Clock = origClock;
            AdbReverse.ResetSessionEnding();
        }
    }

    [Fact]
    public async Task TryRecoverHungAdbDoesNothingWhileSessionEnding()
    {
        var origClock = AdbReverse.Clock;
        long now = 1_000;
        AdbReverse.Clock = () => now;
        try
        {
            AdbReverse.MarkSessionEnding();
            using var adb = new AdbReverse(new ConfigStore(CreateConfig("adb")), NullLogger<AdbReverse>.Instance);
            var kills = 0;
            adb.KillAdbProcesses = () => { kills++; return 1; };

            Assert.False(await adb.TryRecoverHungAdbAsync(CancellationToken.None));
            Assert.Equal(0, kills);
        }
        finally
        {
            AdbReverse.Clock = origClock;
            AdbReverse.ResetSessionEnding();
        }
    }

    [Fact]
    public void ConfigureEnvironmentSetsWinUsbAndDisablesMdnsOnWindows()
    {
        var winPsi = new ProcessStartInfo("adb");
        AdbReverse.ConfigureEnvironment(winPsi, isWindows: true);
        Assert.Equal("0", winPsi.Environment["ADB_LIBUSB"]);
        Assert.Equal("0", winPsi.Environment["ADB_MDNS"]);

        var unixPsi = new ProcessStartInfo("adb");
        AdbReverse.ConfigureEnvironment(unixPsi, isWindows: false);
        Assert.False(unixPsi.Environment.ContainsKey("ADB_LIBUSB"));
        Assert.False(unixPsi.Environment.ContainsKey("ADB_MDNS"));
    }

    [Fact]
    public async Task ExecuteLoopRecoversAfterTimeoutAndReconnects()
    {
        var recoveredFlag = Path.Combine(_dir, "recovered.flag");
        string fake;
        if (OperatingSystem.IsWindows())
        {
            fake = Path.Combine(_dir, "adb.cmd");
            File.WriteAllText(fake, string.Join("\r\n",
                "@echo off",
                $"if not exist \"{recoveredFlag}\" (ping -n 4 127.0.0.1 >nul & exit /b 0)",
                "if \"%1\"==\"devices\" (echo List of devices attached& echo FAKE1\tdevice& exit /b 0)",
                "if \"%3\"==\"reverse\" if \"%4\"==\"--list\" (echo UsbFfs tcp:8765 tcp:8765& exit /b 0)",
                "if \"%4\"==\"pm\" (echo package:/data/app/base.apk& exit /b 0)",
                "exit /b 0", ""));
        }
        else
        {
            fake = Path.Combine(_dir, "adb");
            File.WriteAllText(fake, string.Join("\n",
                "#!/bin/sh",
                $"if [ ! -f \"{recoveredFlag}\" ]; then sleep 3; exit 0; fi",
                "if [ \"$1\" = devices ]; then printf 'List of devices attached\\nFAKE1\\tdevice\\n'; exit 0; fi",
                "if [ \"$3\" = reverse ] && [ \"$4\" = --list ]; then echo 'UsbFfs tcp:8765 tcp:8765'; exit 0; fi",
                "if [ \"$4\" = pm ]; then echo 'package:/data/app/base.apk'; exit 0; fi",
                "exit 0", ""));
            File.SetUnixFileMode(fake, (UnixFileMode)0b111_101_101);
        }

        using var adb = new AdbReverse(new ConfigStore(CreateConfig(fake)), NullLogger<AdbReverse>.Instance);
        var kills = 0;
        adb.CommandTimeout = TimeSpan.FromMilliseconds(350);
        adb.RecoveryCooldown = TimeSpan.FromSeconds(5);
        adb.DisconnectedPollDelay = TimeSpan.FromMilliseconds(200);
        adb.ConnectedPollDelay = TimeSpan.FromMilliseconds(200);
        adb.KillAdbProcesses = () =>
        {
            kills++;
            File.WriteAllText(recoveredFlag, "ok");
            return 1;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await adb.StartAsync(cts.Token);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTime.UtcNow < deadline && !adb.Connected)
            {
                await Task.Delay(50, cts.Token);
            }
            Assert.True(kills >= 1);
            Assert.True(adb.Connected);
            Assert.Equal("FAKE1", adb.Devices);
        }
        finally
        {
            cts.Cancel();
            try { await adb.StopAsync(CancellationToken.None); } catch { }
        }
    }
}
