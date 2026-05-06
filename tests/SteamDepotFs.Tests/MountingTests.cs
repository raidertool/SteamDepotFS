using Xunit;
using System.Reflection;

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

    [Theory]
    [InlineData("/Volumes/SteamDepotFS-Test")]
    [InlineData("/Volumes/SteamDepotFS-Test/Nested")]
    public void IsFSKitMountPoint_AcceptsVolumesPaths(string mountPoint)
    {
        var result = TryInvokeIsFSKitMountPoint(mountPoint);
        if (result is null)
        {
            return;
        }

        Assert.True(result.Value);
    }

    [Theory]
    [InlineData("/tmp/SteamDepotFS-Test")]
    [InlineData("/VolumesSidecar/SteamDepotFS-Test")]
    public void IsFSKitMountPoint_RejectsNonVolumesPaths(string mountPoint)
    {
        var result = TryInvokeIsFSKitMountPoint(mountPoint);
        if (result is null)
        {
            return;
        }

        Assert.False(result.Value);
    }

    private static bool? TryInvokeIsFSKitMountPoint(string mountPoint)
    {
        var type = typeof(DepotMountFactory).Assembly.GetType("MacFuseRuntime");
        if (type is null)
        {
            return null;
        }

        var method = type.GetMethod("IsFSKitMountPoint", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return (bool)method.Invoke(null, [mountPoint])!;
    }
}
