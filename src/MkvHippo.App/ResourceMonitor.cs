using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace MkvHippo.App;

/// <summary>
/// Samples system-wide CPU, disk and network utilization percentages. CPU and disk come
/// from PDH performance counters (added by English name, so localized Windows works);
/// network is total NIC bytes/sec against the summed link speed. Any probe that fails
/// reports null and the UI shows a dash instead.
/// </summary>
internal sealed class ResourceMonitor : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IntPtr _query;
    private IntPtr _cpuCounter;
    private IntPtr _diskIdleCounter;
    private bool _pdhReady;
    private long _lastNetBytes;
    private double _lastNetSeconds;
    private bool _netBaselined;

    public ResourceMonitor()
    {
        try
        {
            if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
                && Pdh.PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpuCounter) == 0
                && Pdh.PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out _diskIdleCounter) == 0)
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
            cpu = ReadCounter(_cpuCounter) is double c ? Math.Clamp(c, 0, 100) : null;
            disk = ReadCounter(_diskIdleCounter) is double idle ? Math.Clamp(100 - idle, 0, 100) : null;
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

    private double? SampleNetwork()
    {
        long totalBytes = 0, capacityBitsPerSec = 0;
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
                totalBytes += stats.BytesReceived + stats.BytesSent;
                capacityBitsPerSec += nic.Speed;
            }
        }
        catch (NetworkInformationException)
        {
            return null;
        }
        if (capacityBitsPerSec <= 0)
            return null;

        double now = _clock.Elapsed.TotalSeconds;
        if (!_netBaselined)
        {
            _lastNetBytes = totalBytes;
            _lastNetSeconds = now;
            _netBaselined = true;
            return null;
        }

        double dt = now - _lastNetSeconds;
        if (dt <= 0)
            return null;
        double bytesPerSec = Math.Max(0, totalBytes - _lastNetBytes) / dt;
        _lastNetBytes = totalBytes;
        _lastNetSeconds = now;
        return Math.Clamp(bytesPerSec / (capacityBitsPerSec / 8.0) * 100, 0, 100);
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
