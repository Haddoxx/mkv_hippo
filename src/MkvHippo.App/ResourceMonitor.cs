using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace MkvHippo.App;

/// <summary>
/// Samples system-wide CPU, disk and network utilization percentages, aligned with what
/// Task Manager shows. CPU uses the frequency-normalized "% Processor Utility" counter
/// (Task Manager's metric; plain "% Processor Time" reads far too high on power-managed
/// CPUs), falling back to "% Processor Time" where unavailable. Disk is physical-disk
/// active time. Network is the busiest adapter's throughput against its own link speed —
/// never a sum across adapters, which virtual NICs (Hyper-V/WSL, VPNs) with inflated
/// advertised speeds would dilute. Counters are added by English name via
/// PdhAddEnglishCounter, so localized Windows works. A failed probe reports null.
/// </summary>
internal sealed class ResourceMonitor : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IntPtr _query;
    private IntPtr _cpuCounter;
    private IntPtr _diskIdleCounter;
    private bool _pdhReady;
    private Dictionary<string, (long Bytes, double Seconds)> _previousNicSample = new();

    public ResourceMonitor()
    {
        try
        {
            if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
                return;

            // Task Manager's CPU metric; the Time counter is the fallback for old systems.
            if (Pdh.PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility",
                    IntPtr.Zero, out _cpuCounter) != 0)
            {
                Pdh.PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time",
                    IntPtr.Zero, out _cpuCounter);
            }
            Pdh.PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\% Idle Time",
                IntPtr.Zero, out _diskIdleCounter);

            if (_cpuCounter != IntPtr.Zero || _diskIdleCounter != IntPtr.Zero)
            {
                Pdh.PdhCollectQueryData(_query); // baseline; rate counters need two collections
                _pdhReady = true;
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    public (double? Cpu, double? Disk, double? Network) Sample()
    {
        double? cpu = null, disk = null;
        if (_pdhReady && Pdh.PdhCollectQueryData(_query) == 0)
        {
            if (_cpuCounter != IntPtr.Zero && ReadCounter(_cpuCounter) is double c)
                cpu = Math.Clamp(c, 0, 100); // Utility can exceed 100 under turbo; cap like Task Manager
            if (_diskIdleCounter != IntPtr.Zero && ReadCounter(_diskIdleCounter) is double idle)
                disk = Math.Clamp(100 - idle, 0, 100);
        }
        return (cpu, disk, SampleNetwork());
    }

    private static double? ReadCounter(IntPtr counter)
    {
        if (Pdh.PdhGetFormattedCounterValue(counter, Pdh.PDH_FMT_DOUBLE | Pdh.PDH_FMT_NOCAP100,
                IntPtr.Zero, out var value) != 0)
            return null;
        // PDH_CSTATUS_VALID_DATA (0) or PDH_CSTATUS_NEW_DATA (1)
        return value.CStatus <= 1 ? value.DoubleValue : null;
    }

    /// <summary>Utilization of the busiest adapter, each measured against its own link speed.</summary>
    private double? SampleNetwork()
    {
        double now = _clock.Elapsed.TotalSeconds;
        var current = new Dictionary<string, (long Bytes, double Seconds)>();
        double? busiest = null;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || nic.Speed <= 0)
                {
                    continue;
                }

                var stats = nic.GetIPStatistics();
                long bytes = stats.BytesReceived + stats.BytesSent;
                current[nic.Id] = (bytes, now);

                if (!_previousNicSample.TryGetValue(nic.Id, out var previous))
                    continue;
                double dt = now - previous.Seconds;
                if (dt <= 0)
                    continue;
                double bytesPerSec = Math.Max(0, bytes - previous.Bytes) / dt;
                double percent = Math.Clamp(bytesPerSec / (nic.Speed / 8.0) * 100, 0, 100);
                busiest = Math.Max(busiest ?? 0, percent);
            }
        }
        catch (NetworkInformationException)
        {
            return null;
        }
        _previousNicSample = current;
        return busiest;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            Pdh.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _pdhReady = false;
        }
    }

    private static class Pdh
    {
        internal const uint PDH_FMT_DOUBLE = 0x00000200;
        internal const uint PDH_FMT_NOCAP100 = 0x00008000;

        [StructLayout(LayoutKind.Explicit)]
        internal struct FmtCounterValue
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double DoubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        internal static extern uint PdhOpenQuery(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        internal static extern uint PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

        [DllImport("pdh.dll")]
        internal static extern uint PdhCollectQueryData(IntPtr hQuery);

        [DllImport("pdh.dll")]
        internal static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, IntPtr lpdwType, out FmtCounterValue pValue);

        [DllImport("pdh.dll")]
        internal static extern uint PdhCloseQuery(IntPtr hQuery);
    }
}
