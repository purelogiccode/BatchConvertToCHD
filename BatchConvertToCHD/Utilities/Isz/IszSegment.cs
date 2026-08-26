namespace BatchConvertToCHD.Utilities.Isz;

/// <summary>
/// One entry of an ISZ segment definition table, describing a single file of a split image.
///
/// The table is only present when the image was split; a whole-file image has none and its data
/// runs from the header's data offset to the end of the file.
/// </summary>
/// <param name="Size">Size of the segment file in bytes.</param>
/// <param name="ChunkCount">Chunks that begin in this segment.</param>
/// <param name="FirstChunkNumber">Index of the first chunk in this segment.</param>
/// <param name="ChunkOffset">Offset within this segment where its first chunk starts.</param>
/// <param name="LeftSize">Bytes of a chunk that spill over into the next segment.</param>
internal sealed record IszSegment(long Size, int ChunkCount, int FirstChunkNumber, int ChunkOffset, int LeftSize)
{
    /// <summary>Bytes per entry in the segment definition table.</summary>
    internal const int EntryLength = 24;

    /// <summary>
    /// A terminating entry, which the spec defines as one with a zero size. A table holds one of
    /// these after the real entries, so the reader knows where the list ends.
    /// </summary>
    internal bool IsTerminator => Size == 0;
}