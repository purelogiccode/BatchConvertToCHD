namespace BatchConvertToCHD.Utilities.Isz;

/// <summary>
/// Outcome of decompressing an ISZ image.
/// </summary>
/// <param name="Success">True when <paramref name="OutputPath"/> holds the complete image.</param>
/// <param name="OutputPath">The written image, or null on failure.</param>
/// <param name="SectorSize">Sector size the header declared, useful for classifying the output.</param>
/// <param name="FailureReason">User-facing explanation, or null on success.</param>
internal sealed record IszDecodeResult(
    bool Success,
    string? OutputPath,
    int SectorSize,
    string? FailureReason
)
{
    internal static IszDecodeResult Succeeded(string outputPath, int sectorSize)
    {
        return new IszDecodeResult(true, outputPath, sectorSize, null);
    }

    internal static IszDecodeResult Failed(string reason)
    {
        return new IszDecodeResult(false, null, 0, reason);
    }
}