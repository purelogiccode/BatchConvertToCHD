namespace PBPSharp;

using Models;

/// <summary>
///     Captures a human-readable description of the most recent PBP I/O or PSAR decompression
///     failure on the current thread. The <see cref="PbpError" /> codes returned by open and
///     extraction carry no underlying exception message, so this side channel attaches the failing
///     file, the OS error text, the block index and the raw bytes' preview for logs and bug
///     reports, mirroring CHDSharp's diagnostics channel. The detail is cleared when read, so a
///     stale failure is never reported twice.
/// </summary>
public static class PbpDiagnostics
{
    /// <summary>Per-thread storage for the most recent failure detail.</summary>
    [ThreadStatic] private static string? _detail;

    /// <summary>Records a failure detail for the current thread, overwriting any previous one.</summary>
    /// <param name="detail">The detail text to record.</param>
    public static void SetDetail(string detail)
    {
        _detail = detail;
    }

    /// <summary>
    ///     Returns the most recent failure detail recorded on the current thread (or <c>null</c> when
    ///     none was recorded) and clears it.
    /// </summary>
    /// <returns>The detail text, or null.</returns>
    public static string? TakeDetail()
    {
        var detail = _detail;
        _detail = null;
        return detail;
    }
}