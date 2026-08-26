namespace BatchConvertToCHD.Utilities;

/// <summary>
/// Transforms a resolved reference during normalization (used e.g. to map MP3 tracks to decoded WAV files).
/// Return null to keep the reference as resolved by the filesystem.
/// </summary>
internal delegate (string Name, string? TrackType)? CueFileLineTransform(
    CueFileReference reference
);