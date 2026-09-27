using MicBoost.Audio;
using Xunit;

namespace MicBoost.Tests;

public class BoostPlannerTests
{
    // A typical Realtek "Microphone Boost": 0..+30 dB in 10 dB steps.
    private static readonly HardwareBoostRange Realtek = new(0f, 30f, 10f);

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(50, 0.5f)]
    [InlineData(100, 1f)]
    [InlineData(150, 1f)]
    [InlineData(200, 1f)]
    public void EndpointScalar_CapsAt100Percent(double level, float expected) =>
        Assert.Equal(expected, BoostPlanner.EndpointScalar(level), 3);

    [Theory]
    [InlineData(80, 30, 0f)]
    [InlineData(100, 30, 0f)]
    [InlineData(150, 30, 15f)]
    [InlineData(200, 30, 30f)]
    [InlineData(200, 20, 20f)]
    [InlineData(125, 20, 5f)]
    public void DesiredBoost_MapsLinearlyFrom100To200(double level, double max, float expected) =>
        Assert.Equal(expected, BoostPlanner.DesiredBoostDb(level, max), 3);

    [Fact]
    public void HardwareFloorsAndSoftwareFillsRemainder_WhenSoftwareAvailable()
    {
        var plan = BoostPlanner.Plan(23f, Realtek, softwareAvailable: true);
        Assert.Equal(20f, plan.HardwareDb, 3);
        Assert.Equal(3f, plan.SoftwareDb, 3);
        Assert.Equal(23f, plan.TotalDb, 3);
        Assert.False(plan.Limited);
    }

    [Fact]
    public void HardwareSnapsToNearestStep_WithoutSoftware()
    {
        Assert.Equal(10f, BoostPlanner.Plan(12f, Realtek, false).HardwareDb, 3);
        Assert.Equal(20f, BoostPlanner.Plan(17f, Realtek, false).HardwareDb, 3);
        Assert.Equal(0f, BoostPlanner.Plan(4f, Realtek, false).HardwareDb, 3);
        Assert.Equal(0f, BoostPlanner.Plan(4f, Realtek, false).SoftwareDb, 3);
    }

    [Fact]
    public void BeyondHardwareMax_IsLimitedWithoutSoftware()
    {
        var plan = BoostPlanner.Plan(36f, Realtek, softwareAvailable: false);
        Assert.Equal(30f, plan.HardwareDb, 3);
        Assert.True(plan.Limited);
    }

    [Fact]
    public void BeyondHardwareMax_SoftwareTakesTheRest()
    {
        var plan = BoostPlanner.Plan(36f, Realtek, softwareAvailable: true);
        Assert.Equal(30f, plan.HardwareDb, 3);
        Assert.Equal(6f, plan.SoftwareDb, 3);
        Assert.False(plan.Limited);
    }

    [Fact]
    public void NoHardware_SoftwareOnly()
    {
        var plan = BoostPlanner.Plan(7.5f, null, softwareAvailable: true);
        Assert.True(float.IsNaN(plan.HardwareLevelDb));
        Assert.Equal(7.5f, plan.SoftwareDb, 3);
        Assert.False(plan.Limited);
    }

    [Fact]
    public void NoHardwareNoSoftware_IsLimited()
    {
        var plan = BoostPlanner.Plan(5f, null, softwareAvailable: false);
        Assert.Equal(0f, plan.TotalDb, 3);
        Assert.True(plan.Limited);
    }

    [Fact]
    public void ZeroBoost_ResetsHardwareToNeutral()
    {
        var plan = BoostPlanner.Plan(0f, Realtek, softwareAvailable: true);
        Assert.Equal(0f, plan.HardwareLevelDb, 3);
        Assert.Equal(0f, plan.TotalDb, 3);
        Assert.False(plan.Limited);
    }

    [Fact]
    public void NeutralIsStepClosestToZero_ForRangesNotStartingAtZero()
    {
        // e.g. a USB mic exposing -6..+24 dB in 6 dB steps
        var range = new HardwareBoostRange(-6f, 24f, 6f);
        Assert.Equal(0f, range.NeutralDb, 3);
        Assert.Equal(24f, range.MaxContributionDb, 3);
        var plan = BoostPlanner.Plan(13f, range, softwareAvailable: true);
        Assert.Equal(12f, plan.HardwareLevelDb, 3);
        Assert.Equal(1f, plan.SoftwareDb, 3);
    }

    [Fact]
    public void ContinuousControl_ZeroStepping_UsesHalfDbGrid()
    {
        var range = new HardwareBoostRange(0f, 20f, 0f);
        Assert.Equal(7.5f, range.Snap(7.4f), 3);
        Assert.Equal(7f, range.FloorStep(7.4f), 3);
    }
}
