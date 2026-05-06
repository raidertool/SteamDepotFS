using Xunit;

namespace SteamDepotFs.Tests;

public sealed class MountingTests
{
    [Theory]
    [InlineData("X:")]
    [InlineData("x:")]
    [InlineData("Z:\\")]
    [InlineData("Z:/")]
    public void IsWindowsDriveMountPoint_AcceptsDriveRoots(string mountPoint)
        => Assert.True(DepotMountFactory.IsWindowsDriveMountPoint(mountPoint));

    [Theory]
    [InlineData("")]
    [InlineData("X")]
    [InlineData("XX:")]
    [InlineData("1:")]
    [InlineData("/tmp/steam-depotfs")]
    [InlineData(@"C:\mount\steam-depotfs")]
    public void IsWindowsDriveMountPoint_RejectsNonDriveRoots(string mountPoint)
        => Assert.False(DepotMountFactory.IsWindowsDriveMountPoint(mountPoint));

    [Fact]
    public void Check_RejectsEmptyMountPointBeforePlatformSpecificChecks()
    {
        var result = DepotMountFactory.Check("");

        Assert.False(result.Succeeded);
        Assert.Contains("mount requires --mount-point", result.Errors[0]);
    }
}
