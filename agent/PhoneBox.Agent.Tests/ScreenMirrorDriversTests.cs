using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using PhoneBox.Agent;
using Xunit;

public sealed class ScreenMirrorDriversTests
{
    [Fact]
    public void DriverEndpointsRemainLocalOnlyAndBlockedOnLanAndUsb()
    {
        Assert.False(Lan.AllowedOverLan(new PathString("/api/screen/drivers")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/screen/vdd/install")));
        Assert.False(UsbLink.Allowed("GET", new PathString("/api/screen/drivers")));
        Assert.False(UsbLink.Allowed("POST", new PathString("/api/screen/vdd/install")));
    }

    [Theory]
    [InlineData("Root\\MttVDD", true)]
    [InlineData("MttVDD", true)]
    [InlineData("root\\mttvdd", true)]
    [InlineData("PCI\\VEN_10DE&DEV_1F99", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsVddHardwareIdMatchesExpectedIds(string? hwId, bool expected)
    {
        Assert.Equal(expected, ScreenMirror.IsVddHardwareId(hwId));
    }

    [Fact]
    public void SelectVddDownloadUrlsDistinguishesArm64AndX64()
    {
        var arm = ScreenMirror.SelectVddDownloadUrls(Architecture.Arm64);
        Assert.Contains("ARM64", arm.PrimaryUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ARM64", arm.FallbackUrl, StringComparison.OrdinalIgnoreCase);

        var x64 = ScreenMirror.SelectVddDownloadUrls(Architecture.X64);
        Assert.Contains("x86.Driver.Only.zip", x64.PrimaryUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x64.zip", x64.FallbackUrl, StringComparison.OrdinalIgnoreCase);

        var x86 = ScreenMirror.SelectVddDownloadUrls(Architecture.X86);
        Assert.Equal(x64, x86);
    }

    [Fact]
    public void EvaluateVddStatusHandlesAllDeviceStates()
    {
        var notInstalled = ScreenMirror.EvaluateVddStatus(false, false, 0, false, null, null, null);
        Assert.False(notInstalled.Installed);
        Assert.False(notInstalled.Ready);
        Assert.False(notInstalled.Disabled);
        Assert.Null(notInstalled.Error);
        Assert.NotNull(notInstalled.DownloadUrl);

        var disabled = ScreenMirror.EvaluateVddStatus(true, true, 22, true, "Virtual Display Driver", @"ROOT\DISPLAY\0000", "11.30.4.434");
        Assert.True(disabled.Installed);
        Assert.True(disabled.Disabled);
        Assert.False(disabled.Ready);
        Assert.NotNull(disabled.Error);

        var problem = ScreenMirror.EvaluateVddStatus(true, false, 43, true, "Virtual Display Driver", @"ROOT\DISPLAY\0000", "11.30.4.434");
        Assert.True(problem.Installed);
        Assert.False(problem.Disabled);
        Assert.False(problem.Ready);
        Assert.Contains("43", problem.Error);

        var noConfig = ScreenMirror.EvaluateVddStatus(true, false, 0, false, "Virtual Display Driver", @"ROOT\DISPLAY\0000", "11.30.4.434");
        Assert.True(noConfig.Installed);
        Assert.False(noConfig.Ready);
        Assert.NotNull(noConfig.Error);

        var ready = ScreenMirror.EvaluateVddStatus(true, false, 0, true, "Virtual Display Driver", @"ROOT\DISPLAY\0000", "11.30.4.434");
        Assert.True(ready.Installed);
        Assert.True(ready.Ready);
        Assert.False(ready.Disabled);
        Assert.Null(ready.Error);
        Assert.Equal("11.30.4.434", ready.Version);
    }

    [Fact]
    public void EnsureVddConfigFileCopiesTemplateAndSetsGpuFriendlyNameCarefully()
    {
        var root = Path.Combine(Path.GetTempPath(), "phonebox-vddcfg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var template = Path.Combine(root, "template.xml");
            File.WriteAllText(template, """
                <?xml version="1.0" encoding="UTF-8"?>
                <vdd_settings>
                    <monitors><count>1</count></monitors>
                    <gpu><friendlyname>default</friendlyname></gpu>
                    <resolutions><resolution><width>1920</width><height>1080</height><refresh_rate>60</refresh_rate></resolution></resolutions>
                </vdd_settings>
                """);

            var target = Path.Combine(root, "sub", "vdd_settings.xml");
            ScreenMirror.EnsureVddConfigFile(target, template, "NVIDIA GeForce GTX 1650 Ti");

            Assert.True(File.Exists(target));
            var doc = XDocument.Load(target);
            Assert.Equal("NVIDIA GeForce GTX 1650 Ti", doc.Root?.Element("gpu")?.Element("friendlyname")?.Value);
            Assert.Single(doc.Root?.Element("resolutions")?.Elements("resolution") ?? []);

            // Existing custom resolution and non-default GPU name must be preserved untouched.
            var customDoc = XDocument.Parse("""
                <vdd_settings>
                    <monitors><count>1</count></monitors>
                    <gpu><friendlyname>Custom GPU</friendlyname></gpu>
                    <resolutions><resolution><width>2400</width><height>1080</height><refresh_rate>60</refresh_rate></resolution></resolutions>
                </vdd_settings>
                """);
            customDoc.Save(target);

            ScreenMirror.EnsureVddConfigFile(target, template, "NVIDIA GeForce RTX 4090");
            var reloaded = XDocument.Load(target);
            Assert.Equal("Custom GPU", reloaded.Root?.Element("gpu")?.Element("friendlyname")?.Value);
            Assert.Equal("2400", reloaded.Root?.Element("resolutions")?.Element("resolution")?.Element("width")?.Value);

            // Missing <gpu> element gets added without overwriting existing resolutions.
            var noGpuDoc = XDocument.Parse("""
                <vdd_settings>
                    <monitors><count>1</count></monitors>
                    <resolutions><resolution><width>2400</width><height>1080</height><refresh_rate>60</refresh_rate></resolution></resolutions>
                </vdd_settings>
                """);
            noGpuDoc.Save(target);

            ScreenMirror.EnsureVddConfigFile(target, template, "NVIDIA GeForce RTX 4090");
            var withGpu = XDocument.Load(target);
            Assert.Equal("NVIDIA GeForce RTX 4090", withGpu.Root?.Element("gpu")?.Element("friendlyname")?.Value);
            Assert.Equal("2400", withGpu.Root?.Element("resolutions")?.Element("resolution")?.Element("width")?.Value);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
