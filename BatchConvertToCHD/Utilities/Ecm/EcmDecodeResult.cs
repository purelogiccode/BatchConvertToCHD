namespace BatchConvertToCHD.Utilities.Ecm;

/// <summary>
///     Outcome of decoding an ECM file.
/// </summary>
/// <param name="Success">True when <paramref name="OutputPath" /> holds the complete image.</param>
/// <param name="OutputPath">The written image, or null on failure.</param>
/// <param name="BytesWritten">Size of the restored image.</param>
/// <param name="FailureReason">User-facing explanation, or null on success.</param>
internal sealed record EcmDecodeResult(
    bool Success,
    string? OutputPath,
    long BytesWritten,
    string? FailureReason
)
{
    internal static EcmDecodeResult Succeeded(string outputPath, long bytesWritten)
    {
        return new EcmDecodeResult(true, outputPath, bytesWritten, null);
    }

    internal static EcmDecodeResult Failed(string reason)
    {
        return new EcmDecodeResult(false, null, 0, reason);
    }
}