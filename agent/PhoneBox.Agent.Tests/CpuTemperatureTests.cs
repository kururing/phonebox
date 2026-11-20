using PhoneBox.Agent;
using Xunit;

public class CpuTemperatureTests
{
    [Fact]
    public void PackageIsPreferredOverIndividualCores() => Assert.Equal(72, CpuTemperature.Select([("CPU Core #1", 80), ("CPU Package", 72)]));
    [Fact]
    public void UnavailableAndInvalidValuesAreNotShownAsZero() => Assert.Null(CpuTemperature.Select([("CPU Package", null), ("Core Max", float.NaN), ("CPU Core #1", 999)]));
    [Fact]
    public void HottestCoreIsUsedWithoutPackage() => Assert.Equal(68, CpuTemperature.Select([("CPU Core #1", 61), ("CPU Core #2", 68), ("CPU Core #1 Distance to TjMax", 90)]));
    [Fact]
    public void AmdPackageIsSupported() => Assert.Equal(65, CpuTemperature.Select([("Core (Tctl/Tdie)", 65)]));
}
