using PBPSharp.Models;

namespace BatchConvertToCHD.Models;

/// <summary>
/// Represents the result of a PBP (PlayStation Portable) file extraction operation.
/// </summary>
internal sealed class PbpExtractionResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the extraction was successful.
    /// </summary>
    internal bool Success { get; set; }

    /// <summary>
    /// Gets the list of extracted CUE file paths.
    /// </summary>
    internal List<string> CueFilePaths { get; init; } = new();

    /// <summary>
    /// Gets or sets the output folder path where files were extracted.
    /// </summary>
    internal string? OutputFolder { get; set; }

    /// <summary>
    /// Gets or sets the underlying PBP error when extraction failed, or null on success.
    /// Lets callers distinguish "not a PlayStation disc image" (PSP homebrew) from real failures.
    /// </summary>
    internal PbpError? ErrorCode { get; set; }

    /// <summary>
    /// Gets or sets a human-readable description of the extraction failure, when one occurred.
    /// Preserved so callers can surface the real reason (e.g. "Failed to open PBP file: ...").
    /// </summary>
    internal string? Error { get; set; }
}