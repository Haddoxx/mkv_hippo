namespace MkvHippo.Core.Reporting;

/// <summary>
/// Accumulates CPU/disk/network utilization samples over a session and produces a
/// one-line verdict on the likely bottleneck. Resources whose probe failed (null
/// samples) are simply excluded.
/// </summary>
public sealed class BottleneckStats
{
    private sealed class Accumulator
    {
        public double Sum;
        public double Peak;
        public int Count;

        public void Add(double value)
        {
            Sum += value;
            Peak = Math.Max(Peak, value);
            Count++;
        }

        public double Average => Sum / Count;
    }

    private readonly Accumulator _cpu = new();
    private readonly Accumulator _disk = new();
    private readonly Accumulator _network = new();

    /// <summary>Average utilization below which nothing is called a bottleneck.</summary>
    public double SaturationThreshold { get; init; } = 40;

    public int SampleCount { get; private set; }

    public void AddSample(double? cpu, double? disk, double? network)
    {
        if (cpu is double c)
            _cpu.Add(c);
        if (disk is double d)
            _disk.Add(d);
        if (network is double n)
            _network.Add(n);
        if (cpu.HasValue || disk.HasValue || network.HasValue)
            SampleCount++;
    }

    public string Summarize()
    {
        var seen = new (string Name, Accumulator Acc)[] { ("cpu", _cpu), ("disk", _disk), ("network", _network) }
            .Where(r => r.Acc.Count > 0)
            .ToList();
        if (seen.Count == 0)
            return "no resource usage data collected";

        var top = seen.MaxBy(r => r.Acc.Average);
        string Describe((string Name, Accumulator Acc) r) =>
            $"{r.Name} avg {r.Acc.Average:0}% peak {r.Acc.Peak:0}%";
        var others = string.Join(", ", seen.Where(r => r.Name != top.Name).Select(Describe));

        if (top.Acc.Average >= SaturationThreshold)
        {
            var rest = others.Length > 0 ? $" — {others}" : "";
            return $"likely bottleneck: {top.Name.ToUpperInvariant()} " +
                   $"(avg {top.Acc.Average:0}%, peak {top.Acc.Peak:0}%){rest}";
        }

        return $"no clear bottleneck ({string.Join(", ", seen.Select(Describe))}) — " +
               "likely limited by the source device or mkvmerge itself";
    }
}
