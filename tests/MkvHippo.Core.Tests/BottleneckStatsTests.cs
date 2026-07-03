using MkvHippo.Core.Reporting;

namespace MkvHippo.Core.Tests;

public class BottleneckStatsTests
{
    [Fact]
    public void NoSamplesProducesTheEmptyMessage()
    {
        Assert.Equal("no resource usage data collected", new BottleneckStats().Summarize());
    }

    [Fact]
    public void SaturatedResourceIsNamedAsTheLikelyBottleneck()
    {
        var stats = new BottleneckStats();
        stats.AddSample(20, 30, 75);
        stats.AddSample(24, 34, 85);

        var summary = stats.Summarize();

        Assert.StartsWith("likely bottleneck: NETWORK (avg 80%, peak 85%)", summary);
        Assert.Contains("cpu avg 22% peak 24%", summary);
        Assert.Contains("disk avg 32% peak 34%", summary);
    }

    [Fact]
    public void NothingSaturatedReportsNoClearBottleneck()
    {
        var stats = new BottleneckStats();
        stats.AddSample(10, 20, 15);
        stats.AddSample(14, 24, 5);

        var summary = stats.Summarize();

        Assert.StartsWith("no clear bottleneck", summary);
        Assert.Contains("disk avg 22% peak 24%", summary);
        Assert.Contains("limited by the source device or mkvmerge itself", summary);
    }

    [Fact]
    public void ThresholdIsConfigurable()
    {
        var stats = new BottleneckStats { SaturationThreshold = 20 };
        stats.AddSample(25, 5, 5);

        Assert.StartsWith("likely bottleneck: CPU", stats.Summarize());
    }

    [Fact]
    public void FailedProbesAreExcludedNotZeroed()
    {
        var stats = new BottleneckStats();
        stats.AddSample(null, null, 90);
        stats.AddSample(null, null, 70);

        var summary = stats.Summarize();

        Assert.StartsWith("likely bottleneck: NETWORK (avg 80%, peak 90%)", summary);
        Assert.DoesNotContain("cpu", summary);
        Assert.DoesNotContain("disk", summary);
    }

    [Fact]
    public void SampleCountIgnoresAllNullSamples()
    {
        var stats = new BottleneckStats();
        stats.AddSample(null, null, null);
        Assert.Equal(0, stats.SampleCount);
        stats.AddSample(50, null, null);
        Assert.Equal(1, stats.SampleCount);
    }
}
