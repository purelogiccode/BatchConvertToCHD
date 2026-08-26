using System.Text;

namespace BatchConvertToCHD.Utilities;

/// <summary>
/// The result of normalizing a CUE sheet.
/// </summary>
/// <param name="SourceEncoding">Encoding the cue was decoded with.</param>
/// <param name="HasBom">True when the cue file started with an explicit BOM that was stripped during decoding.</param>
/// <param name="References">Every FILE line, with its resolved on-disk name.</param>
/// <param name="UnresolvedNames">Referenced names for which no matching file was found.</param>
/// <param name="CanonicalLines">The rewritten cue content (quoted FILE lines with resolved names).</param>
/// <param name="NeedsRewrite">True when the canonical content differs from the original file content.</param>
/// <param name="ReferencesChanged">True when any referenced file name was corrected (zero-padding) or renamed by the transform.</param>
internal sealed record CueNormalizationResult(
    Encoding SourceEncoding,
    bool HasBom,
    IReadOnlyList<CueFileReference> References,
    IReadOnlyList<string> UnresolvedNames,
    IReadOnlyList<string> CanonicalLines,
    bool NeedsRewrite,
    bool ReferencesChanged = false
)
{
    /// <summary>Canonical cue content joined with CRLF (the format chdman reads reliably).</summary>
    public string CanonicalCueText => string.Join("\r\n", CanonicalLines) + "\r\n";
}