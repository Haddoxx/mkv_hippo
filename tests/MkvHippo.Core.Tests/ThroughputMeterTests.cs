using MkvHippo.Core.Reporting;

namespace MkvHippo.Core.Tests;

public class ThroughputMeterTests
{
    [Fact]
    public void FirstSampleOnlyEstablishesTheBaseline()
    {
        var meter = new ThroughputMeter();
        Assert.Equal(0, meter.Update(5_000_000, 10.0));
        Assert.Equal(0, meter.Current);
    }

    [Fact]
    public void SteadyGrowthYieldsTheExactRate()
    {
        var meter = new ThroughputMeter();
        meter.Update(0, 0.0);
        Assert.Equal(1_048_576, meter.Update(1_048_576, 1.0)); // first real interval: no smoothing yet
    }

    [Fact]
    public void RateIsSmoothedAcrossIntervals()
    {
        var meter = new ThroughputMeter(smoothing: 0.5);
        meter.Update(0, 0.0);
        meter.Update(1000, 1.0);                    // rate = 1000
        var smoothed = meter.Update(1000, 2.0);     // instant 0 → 0.5*0 + 0.5*1000
        Assert.Equal(500, smoothed);
    }

    [Fact]
    public void ShrinkingTotalCountsAsZeroProgressAndRebaselines()
    {
        var meter = new ThroughputMeter(smoothing: 1.0);
        meter.Update(0, 0.0);
        meter.Update(10_000, 1.0);
        Assert.Equal(0, meter.Update(2_000, 2.0));   // partial output deleted mid-run
        Assert.Equal(3_000, meter.Update(5_000, 3.0)); // growth measured from the new baseline
    }

    [Fact]
    public void NonAdvancingClockKeepsThePreviousRate()
    {
        var meter = new ThroughputMeter(smoothing: 1.0);
        meter.Update(0, 0.0);
        meter.Update(1000, 1.0);
        Assert.Equal(1000, meter.Update(9999, 1.0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void RejectsInvalidSmoothing(double smoothing)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThroughputMeter(smoothing));
    }
}
