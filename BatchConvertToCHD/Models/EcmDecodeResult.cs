namespace BatchConvertToCHD.Models;

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
    /// <summary>Creates a successful result for the image written to <paramref name="outputPath" />.</summary>
    /// <param name="outputPath">Path of the restored image.</param>
    /// <param name="bytesWritten">Size of the restored image.</param>
    /// <returns>A successful <see cref="EcmDecodeResult" />.</returns>
    internal static EcmDecodeResult Succeeded(string outputPath, long bytesWritten)
    {
        return new EcmDecodeResult(true, outputPath, bytesWritten, null);
    }

    /// <summary>Creates a failed result carrying a user-facing explanation.</summary>
    /// <param name="reason">User-facing explanation of the failure.</param>
    /// <returns>A failed <see cref="EcmDecodeResult" />.</returns>
    internal static EcmDecodeResult Failed(string reason)
    {
        return new EcmDecodeResult(false, null, 0, reason);
    }
}