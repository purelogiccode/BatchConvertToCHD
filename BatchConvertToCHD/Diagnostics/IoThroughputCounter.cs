using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;


namespace BatchConvertToCHD.Diagnostics;

/// <summary>
///     Cross-platform replacement for <c>System.Diagnostics.PerformanceCounter</c>
///     usage. Reports the current process's disk read/write throughput in bytes per second on
///     Windows (via <c>GetProcessIoCounters</c>) and Linux (via <c>/proc/self/io</c>). On platforms
///     without an implementation (macOS) the factory returns null and the speed card stays hidden.
/// </summary>
internal sealed class IoThroughputCounter : IDisposable
{
    private readonly Func<long?> _readTotalBytes;
    private long _lastTimestamp;
    private long _lastTotalBytes;
    private bool _hasBaseline;

    /// <summary>
    ///     Initializes a new instance of the <see cref="IoThroughputCounter" /> class around a
    ///     caller-supplied byte-count source. Exposed so tests can supply a deterministic source.
    /// </summary>
    /// <param name="readTotalBytes">Reads the process's cumulative transfer count.</param>
    internal IoThroughputCounter(Func<long?> readTotalBytes)
    {
        _readTotalBytes = readTotalBytes;
    }

    /// <summary>
    ///     Creates a counter that reports the process's write throughput, or null when the current
    ///     platform has no implementation.
    /// </summary>
    internal static IoThroughputCounter? CreateForWrites()
    {
        var source = CreateTotalBytesSource(writes: true);
        return source is null ? null : new IoThroughputCounter(source);
    }

    /// <summary>
    ///     Creates a counter that reports the process's read throughput, or null when the current
    ///     platform has no implementation.
    /// </summary>
    internal static IoThroughputCounter? CreateForReads()
    {
        var source = CreateTotalBytesSource(writes: false);
        return source is null ? null : new IoThroughputCounter(source);
    }

    /// <summary>
    ///     Returns the average throughput in bytes per second since the previous call.
    /// </summary>
    internal double NextValue()
    {
        try
        {
            var total = _readTotalBytes();
            if (total is null) return 0;

            var now = Stopwatch.GetTimestamp();
            if (!_hasBaseline)
            {
                _lastTimestamp = now;
                _lastTotalBytes = total.Value;
                _hasBaseline = true;
                return 0;
            }

            var elapsedSeconds =
                (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            var deltaBytes = total.Value - _lastTotalBytes;

            _lastTimestamp = now;
            _lastTotalBytes = total.Value;

            if (elapsedSeconds <= 0 || deltaBytes <= 0) return 0;

            return deltaBytes / elapsedSeconds;
        }
        catch
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // No unmanaged resources to release.
    }

    private static long GetWindowsProcessTotalBytes(bool writes)
    {
        return GetProcessIoCounters(GetCurrentProcess(), out var counters)
            ? (long)(writes ? counters.WriteTransferCount : counters.ReadTransferCount)
            : 0;
    }

    private static long GetLinuxProcessTotalBytes(bool writes)
    {
        const string ioPath = "/proc/self/io";
        if (!File.Exists(ioPath)) return 0;

        var wanted = writes ? "write_bytes:" : "read_bytes:";
        foreach (var line in File.ReadLines(ioPath))
        {
            if (!line.StartsWith(wanted, StringComparison.Ordinal)) continue;

            var value = line[wanted.Length..].Trim();
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
        }

        return 0;
    }

    private static Func<long?>? CreateTotalBytesSource(bool writes)
    {
        if (OperatingSystem.IsWindows())
        {
            return () => GetWindowsProcessTotalBytes(writes);
        }

        if (OperatingSystem.IsLinux())
        {
            return () => GetLinuxProcessTotalBytes(writes);
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters counters);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}