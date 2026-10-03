using CHDSharp.Encoder;
using CHDSharp.Encoder.Models;

namespace BatchConvertToCHD.Services;

/// <summary>
///     In-process CHD encoder backed by the CHDSharp library. Replaces the bundled CHDSharp
///     command-line tool so every platform has an encoder without shipping a native executable;
///     on Windows it is the automatic fallback behind chdman.
/// </summary>
internal static class ChdSharpEncoderService
{
    /// <summary>CD hunk size: 8 frames (chdman's <c>createcd</c> default).</summary>
    private const uint CdHunkBytes = 19584;

    /// <summary>CD frame size with subcode (chdman's <c>createcd</c> unit size).</summary>
    private const uint CdUnitBytes = 2448;

    /// <summary>Default hunk size for raw images (chdman's raw/DVD/HDD default).</summary>
    private const uint RawHunkBytes = 4096;

    /// <summary>DVD sector size (chdman's <c>createdvd</c> unit size).</summary>
    private const uint DvdUnitBytes = 2048;

    /// <summary>Hard-disk sector size (chdman's <c>createhd</c> default).</summary>
    private const uint HardDiskUnitBytes = 512;

    /// <summary>Raw CD sector size used by <c>createraw -us 2352</c>.</summary>
    private const uint RawCdUnitBytes = 2352;

    /// <summary>Default CD codecs (chdman's <c>createcd</c> defaults).</summary>
    private static readonly uint[] CdCodecs = ChdCodecs.ParseCodecTags("cdlz,cdzl,cdfl");

    /// <summary>Default raw/DVD/HDD codecs (chdman's defaults for those commands).</summary>
    private static readonly uint[] RawCodecs = ChdCodecs.ParseCodecTags("lzma,zlib,huff,flac");

    /// <summary>
    ///     Encodes <paramref name="inputPath" /> into <paramref name="outputPath" /> using the same
    ///     commands and defaults as chdman: <c>createcd</c>, <c>createdvd</c>, <c>createhd</c> and
    ///     <c>createraw</c>. The output is written to a temporary staging path by the caller and
    ///     moved into place on success.
    /// </summary>
    /// <param name="command">The chdman-compatible command selecting the encoding mode.</param>
    /// <param name="inputPath">Path of the prepared source (cue/gdi/toc/iso/img/raw).</param>
    /// <param name="outputPath">Path of the CHD file to create (created/overwritten).</param>
    /// <param name="rawUnits2352">
    ///     True when the source is a raw 2352-byte-sector image (<c>createraw -us 2352</c>).
    /// </param>
    /// <param name="taskCount">Parallel compression workers (chdman's <c>-np</c>).</param>
    /// <param name="token">Cancels the encode; throws <see cref="OperationCanceledException" />.</param>
    /// <exception cref="ArgumentException">The command is not a supported creation command.</exception>
    internal static void Encode(
        string command,
        string inputPath,
        string outputPath,
        bool rawUnits2352,
        int taskCount,
        CancellationToken token
    )
    {
        var options = new ChdEncodeOptions { TaskCount = Math.Clamp(taskCount, 1, 64) };

        switch (command)
        {
            case "createcd":
                // EncodeCd parses CUE/GDI/TOC/ISO descriptors and writes standard 2448-byte CD
                // frames; a 2352-byte source has its 96-byte subcode portion zero-filled by the
                // reader, so the raw-units flag is not needed here.
                ChdEncoder.EncodeCd(
                    inputPath,
                    outputPath,
                    CdHunkBytes,
                    CdUnitBytes,
                    CdCodecs,
                    options,
                    token
                );
                break;

            case "createdvd":
                // createdvd always stamps the 'DVD ' metadata tag (that is what makes it a DVD).
                options.Metadata = [MetadataWriter.BuildDvdMetadata()];
                ChdEncoder.EncodeRaw(
                    inputPath,
                    outputPath,
                    RawHunkBytes,
                    DvdUnitBytes,
                    RawCodecs,
                    options,
                    token
                );
                break;

            case "createhd":
            {
                // chdman createhd guesses CHS geometry from the image size, writes 'GDDD' metadata
                // and makes the logical size the geometry product (which rounds sub-geometry
                // inputs up past the actual file length).
                var fileSize = (ulong)new FileInfo(inputPath).Length;
                var geometry = MetadataWriter.GuessChs(fileSize, HardDiskUnitBytes);
                options.Metadata =
                [
                    MetadataWriter.BuildHardDiskMetadata(
                        geometry.Cylinders,
                        geometry.Heads,
                        geometry.Sectors,
                        HardDiskUnitBytes
                    )
                ];
                options.LogicalLengthBytes = (long)(
                    (ulong)geometry.Cylinders * geometry.Heads * geometry.Sectors * HardDiskUnitBytes
                );

                ChdEncoder.EncodeRaw(
                    inputPath,
                    outputPath,
                    RawHunkBytes,
                    HardDiskUnitBytes,
                    RawCodecs,
                    options,
                    token
                );
                break;
            }

            case "createraw":
            {
                var unitBytes = rawUnits2352 ? RawCdUnitBytes : HardDiskUnitBytes;
                var hunkBytes = Math.Max(RawHunkBytes / unitBytes * unitBytes, unitBytes);
                ChdEncoder.EncodeRaw(
                    inputPath,
                    outputPath,
                    hunkBytes,
                    unitBytes,
                    RawCodecs,
                    options,
                    token
                );
                break;
            }

            default:
                throw new ArgumentException(
                    $"Unsupported encoder command '{command}'.",
                    nameof(command)
                );
        }
    }
}