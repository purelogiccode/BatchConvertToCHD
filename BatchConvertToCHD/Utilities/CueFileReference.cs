using System.IO;

namespace BatchConvertToCHD.Utilities;

/// <summary>
///     A file referenced by a CUE sheet FILE line.
/// </summary>
/// <param name="ReferencedName">File name exactly as written in the cue (may include a relative subdirectory).</param>
/// <param name="ResolvedName">Actual on-disk name (case- or zero-padding-resolved), or null when no file matches.</param>
/// <param name="FullPath">Full path of <paramref name="ReferencedName" /> in the cue's directory.</param>
/// <param name="TrackType">BINARY / WAVE / MP3 / AIFF / MOTOROLA / AUDIO, or null when the line has no type token.</param>
/// <param name="WasNameCorrected">
///     True when the on-disk name differs from the cue only by zero-padding (e.g. "(Track 02)"
///     vs "(Track 2)") and was corrected via <see cref="ResolvedName" />.
/// </param>
/// <param name="CueDirectory">
///     Directory holding the cue. <see cref="ResolvedName" /> is relative to it, so it is the
///     anchor for <see cref="ResolvedFullPath" />.
/// </param>
internal sealed record CueFileReference(
    string ReferencedName,
    string? ResolvedName,
    string FullPath,
    string? TrackType,
    bool WasNameCorrected = false,
    string? CueDirectory = null
)
{
    /// <summary>True when a file with this name (possibly case- or padding-resolved) was found for the cue.</summary>
    public bool IsResolved => ResolvedName is not null;

    /// <summary>
    ///     Full path of the resolved on-disk file, or <see cref="FullPath" /> when unresolved.
    ///     <see cref="ResolvedName" /> is relative to <see cref="CueDirectory" />, so that is what it is
    ///     combined with - anchoring on <see cref="FullPath" />'s directory would repeat any
    ///     subdirectory the reference already carried.
    /// </summary>
    public string ResolvedFullPath =>
        ResolvedName is null
            ? FullPath
            : Path.Combine(
                CueDirectory ?? Path.GetDirectoryName(FullPath) ?? string.Empty,
                ResolvedName
            );
}