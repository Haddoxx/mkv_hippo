namespace MkvHippo.Core.Reporting;

/// <summary>
/// Computes a smoothed transfer rate (bytes/sec) from periodic samples of a growing
/// byte total. Tolerates the total shrinking (a partial output deleted on failure or
/// cancel) by treating that interval as zero progress and re-baselining.
/// </summary>
public sealed class ThroughputMeter
{
    private readonly double _smoothing;
    private long _lastTotal;
    private double _lastSeconds;
    private double? _rate;
    private bool _hasBaseline;

    /// <param name="smoothing">EMA weight of the newest interval, in (0, 1].</param>
    public ThroughputMeter(double smoothing = 0.4)
    {
        if (smoothing <= 0 || smoothing > 1)
            throw new ArgumentOutOfRangeException(nameof(smoothing));
        _smoothing = smoothing;
    }

    public double Current => _rate ?? 0;

    /// <summary>Feed the current total and a monotonic clock reading; returns the smoothed rate.</summary>
    public double Update(long totalBytes, double atSeconds)
    {
        if (!_hasBaseline)
        {
            _lastTotal = totalBytes;
            _lastSeconds = atSeconds;
            _hasBaseline = true;
            return 0;
        }

        double dt = atSeconds - _lastSeconds;
        if (dt <= 0)
            return Current;

        long delta = Math.Max(0, totalBytes - _lastTotal);
        double instant = delta / dt;
        _rate = _rate is null ? instant : _smoothing * instant + (1 - _smoothing) * _rate.Value;
        _lastTotal = totalBytes;
        _lastSeconds = atSeconds;
        return _rate.Value;
    }
}
