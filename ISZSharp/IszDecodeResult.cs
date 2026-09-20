namespace ISZSharp;

/// <summary>
///     Outcome of decompressing an ISZ image.
/// </summary>
/// <param name="Success">True when <paramref name="OutputPath" /> holds the complete image.</param>
/// <param name="OutputPath">The written image, or null on failure.</param>
/// <param name="SectorSize">Sector size the header declared, useful for classifying the output.</param>
/// <param name="FailureReason">User-facing explanation, or null on success.</param>
public sealed record IszDecodeResult(
    bool Success,
    string? OutputPath,
    int SectorSize,
    string? FailureReason
)
{
    /// <summary>A result for a completed decode.</summary>
    /// <param name="outputPath">The written image.</param>
    /// <param name="sectorSize">Sector size the header declared.</param>
    public static IszDecodeResult Succeeded(string outputPath, int sectorSize)
    {
        return new IszDecodeResult(true, outputPath, sectorSize, null);
    }

    /// <summary>A result for a decode that did not complete, carrying the user-facing reason.</summary>
    /// <param name="reason">Why the image could not be restored.</param>
    public static IszDecodeResult Failed(string reason)
    {
        return new IszDecodeResult(false, null, 0, reason);
    }
}