namespace BatchConvertToCHD.Utilities;

/// <summary>
///     Decodes an MP3 file to a 16-bit PCM WAV file.
/// </summary>
internal interface IMp3Decoder
{
    /// <summary>
    ///     Decodes <paramref name="mp3Path" /> into <paramref name="wavPath" />.
    ///     Throws when the file cannot be decoded.
    /// </summary>
    /// <param name="mp3Path">Path of the MP3 file to decode.</param>
    /// <param name="wavPath">Destination path for the decoded 16-bit PCM WAV file.</param>
    /// <param name="onLog">Optional logging callback.</param>
    /// <param name="token">Cancellation token.</param>
    Task DecodeAsync(
        string mp3Path,
        string wavPath,
        Action<string>? onLog,
        CancellationToken token
    );
}