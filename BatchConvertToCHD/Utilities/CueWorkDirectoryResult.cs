namespace BatchConvertToCHD.Utilities;

/// <summary>
///     The outcome of <see cref="CueWorkDirectory.PrepareAsync" />.
/// </summary>
/// <param name="WorkCuePath">
///     Path of the canonicalized cue inside the work directory, or null when no work directory was
///     needed.
/// </param>
/// <param name="WorkDir">The work directory (must be deleted by the caller), or null.</param>
/// <param name="UnresolvedNames">Referenced names that could not be resolved against the filesystem.</param>
internal sealed record CueWorkDirectoryResult(
    string? WorkCuePath,
    string? WorkDir,
    IReadOnlyList<string> UnresolvedNames
);