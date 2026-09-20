using System.Text;
using ISZSharp;

namespace BatchConvertToCHD.Tests;

public class IszDecoderTests : IDisposable
{
    private const int SectorSize = 2048;
    private const int ChunkSize = 8192;

    private readonly string _tempDir;

    public IszDecoderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"IszDecoderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    private async Task AssertRoundTripsAsync(
        byte[] image,
        Func<int, int> flagForChunk,
        bool expectCompressed = false,
        bool writeChecksums = false,
        bool zeroChunksCarryLength = true
    )
    {
        var iszPath = Path.Combine(_tempDir, $"image_{Guid.NewGuid():N}.isz");
        var outputPath = Path.ChangeExtension(iszPath, ".iso");

        IszImageBuilder.WriteSingle(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            flagForChunk,
            writeChecksums: writeChecksums,
            zeroChunksCarryLength: zeroChunksCarryLength
        );

        if (expectCompressed)
        {
            // Guards against a fixture whose chunks all fell back to being stored verbatim, which
            // would leave the decompression path untested while the test still passed.
            Assert.True(
                new FileInfo(iszPath).Length < image.Length / 2,
                "the fixture did not actually compress, so the decompression path was not exercised"
            );
        }

        var result = await IszDecoder.DecodeAsync(iszPath, outputPath, Log, CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(outputPath, result.OutputPath);
        Assert.Equal(image, await File.ReadAllBytesAsync(outputPath));
    }

    /// <summary>
    ///     Builds an image of whole sectors. The content has to be compressible, because a chunk that
    ///     grows under compression is stored verbatim by any real writer and would leave the zlib and
    ///     bzip2 paths untested, and every sector has to be distinguishable, so a chunk boundary landing
    ///     in the wrong place shows up as a byte difference rather than a coincidence.
    /// </summary>
    private static byte[] BuildImage(int chunks, int extraSectors = 0, int sectorSize = SectorSize)
    {
        var length = (chunks * ChunkSize) + (extraSectors * sectorSize);
        length -= length % sectorSize;

        var image = new byte[length];

        for (var sector = 0; sector * sectorSize < image.Length; sector++)
        {
            var start = sector * sectorSize;
            var end = Math.Min(start + sectorSize, image.Length);

            // A repeating run keyed to the sector number: compressible, and unique per sector.
            for (var offset = start; offset < end; offset++)
                image[offset] = (byte)((sector * 7) + ((offset - start) % 19));

            var marker = Encoding.ASCII.GetBytes($"SECTOR{sector:D6}");
            marker.CopyTo(image, start);
        }

        return image;
    }

    private static void Log(string message)
    {
        /* the decoder's progress output is not under test */
    }

    #region Chunk table entries

    [Fact]
    public void ChunkEntryTypeComesFromTheTopTwoBits()
    {
        // A 3-byte pointer holding 0x1234 with each of the four flags in turn.
        foreach (
            var (flag, expected) in new[]
            {
                (0x00, IszChunkType.Zero),
                (0x40, IszChunkType.Stored),
                (0x80, IszChunkType.ZLib),
                (0xC0, IszChunkType.BZip2)
            }
        )
        {
            var table = new byte[] { 0x34, 0x12, (byte)flag };

            var (type, length) = IszDecoder.ReadChunkEntry(table, 0, 3);

            Assert.Equal(expected, type);
            Assert.Equal(0x1234, length);
        }
    }

    [Fact]
    public void ChunkEntryWidthsOtherThanThreeAreRead()
    {
        // 2-byte entry: 14 bits of length, top two bits the flag.
        var twoByte = new byte[] { 0xFF, 0x3F | 0x80 };
        var (twoType, twoLength) = IszDecoder.ReadChunkEntry(twoByte, 0, 2);
        Assert.Equal(IszChunkType.ZLib, twoType);
        Assert.Equal(0x3FFF, twoLength);

        // 4-byte entry: 30 bits of length.
        var fourByte = new byte[] { 0x00, 0x00, 0x01, 0x40 | 0x40 };
        var (fourType, fourLength) = IszDecoder.ReadChunkEntry(fourByte, 0, 4);
        Assert.Equal(IszChunkType.Stored, fourType);
        Assert.Equal(0x00010000, fourLength);
    }

    [Fact]
    public void ChunkEntriesAreReadAtTheirIndex()
    {
        var table = new byte[] { 0x01, 0x00, 0x40, 0x02, 0x00, 0x80, 0x03, 0x00, 0xC0 };

        Assert.Equal((IszChunkType.Stored, 1), IszDecoder.ReadChunkEntry(table, 0, 3));
        Assert.Equal((IszChunkType.ZLib, 2), IszDecoder.ReadChunkEntry(table, 1, 3));
        Assert.Equal((IszChunkType.BZip2, 3), IszDecoder.ReadChunkEntry(table, 2, 3));
    }

    [Fact]
    public async Task TheChunkTableIsStoredObfuscated()
    {
        // The spec does not mention it, but UltraISO XORs both tables with the complement of "IsZ!"
        // and every independent reader undoes it. A decoder that read the plain bytes would see
        // nonsense lengths, so the obfuscation is asserted here rather than only through round trips.
        var image = BuildImage(4);
        var iszPath = Path.Combine(_tempDir, "obfuscated.isz");
        IszImageBuilder.WriteSingle(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiData
        );

        var header = await IszDecoder.TryReadHeaderAsync(iszPath, CancellationToken.None);
        Assert.NotNull(header);

        var bytes = await File.ReadAllBytesAsync(iszPath);
        var firstEntry = bytes.AsSpan((int)header.ChunkTableOffset, 3);
        var plainEntry = new byte[] { 0x00, 0x20, 0x40 };
        var mask = new byte[] { 0xB6, 0x8C, 0xA5 };

        for (var index = 0; index < plainEntry.Length; index++)
            Assert.Equal((byte)(plainEntry[index] ^ mask[index]), firstEntry[index]);
    }

    [Fact]
    public async Task AnImageWithNoChunkTableDecodes()
    {
        // The spec says a zero pointer offset means no chunk table; the data is one raw run.
        var image = BuildImage(8, 3);
        var iszPath = Path.Combine(_tempDir, "notable.isz");
        IszImageBuilder.WriteWithoutChunkTable(iszPath, image, SectorSize, ChunkSize);

        var outputPath = Path.Combine(_tempDir, "notable.iso");
        var result = await IszDecoder.DecodeAsync(iszPath, outputPath, Log, CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(image, await File.ReadAllBytesAsync(outputPath));
    }

    [Fact]
    public async Task AnImageWithNoChunkTableAndChecksumsDecodes()
    {
        var image = BuildImage(8, 3);
        var iszPath = Path.Combine(_tempDir, "notablecrc.isz");
        IszImageBuilder.WriteWithoutChunkTable(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            writeChecksums: true
        );

        var outputPath = Path.Combine(_tempDir, "notablecrc.iso");
        var result = await IszDecoder.DecodeAsync(iszPath, outputPath, Log, CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(image, await File.ReadAllBytesAsync(outputPath));
    }

    #endregion

    #region Segment naming

    [Fact]
    public void SegmentsAfterTheFirstAreNamedInTheSpecScheme()
    {
        // The spec: segment 1 is "game.isz", segment 2 is "game.i01", segment n is "game.i(n-1)".
        var first = Path.Combine(_tempDir, "game.isz");

        Assert.Equal(first, IszDecoder.GetSegmentPath(first, 0));
        Assert.Equal(Path.Combine(_tempDir, "game.i01"), IszDecoder.GetSegmentPath(first, 1));
        Assert.Equal(Path.Combine(_tempDir, "game.i02"), IszDecoder.GetSegmentPath(first, 2));
        Assert.Equal(Path.Combine(_tempDir, "game.i15"), IszDecoder.GetSegmentPath(first, 15));
    }

    [Fact]
    public void PartNamingSchemesFollowTheFirstSegment()
    {
        // UltraISO can also write "game.part01.isz"/"game.part02.isz" (and a three-digit form), and
        // both reference readers accept them, so the segment path follows whichever the first file
        // uses instead of forcing the spec scheme onto it.
        var twoDigit = Path.Combine(_tempDir, "game.part01.isz");

        Assert.Equal(twoDigit, IszDecoder.GetSegmentPath(twoDigit, 0));
        Assert.Equal(Path.Combine(_tempDir, "game.part02.isz"), IszDecoder.GetSegmentPath(twoDigit, 1));
        Assert.Equal(Path.Combine(_tempDir, "game.part10.isz"), IszDecoder.GetSegmentPath(twoDigit, 9));

        var threeDigit = Path.Combine(_tempDir, "game.part001.isz");

        Assert.Equal(
            Path.Combine(_tempDir, "game.part002.isz"),
            IszDecoder.GetSegmentPath(threeDigit, 1)
        );
        Assert.Equal(
            Path.Combine(_tempDir, "game.part012.isz"),
            IszDecoder.GetSegmentPath(threeDigit, 11)
        );
    }

    [Fact]
    public void DecodedNameKeepsTheStemAndBecomesAnIso()
    {
        Assert.Equal(
            "Breath of Fire IV.iso",
            IszDecoder.GetDecodedFileName(@"D:\roms\Breath of Fire IV.isz")
        );
    }

    #endregion

    #region Round trips

    [Fact]
    public async Task ZlibChunksRoundTrip()
    {
        await AssertRoundTripsAsync(
            BuildImage(16),
            static _ => IszImageBuilder.AdiZlib,
            true
        );
    }

    [Fact]
    public async Task Bzip2ChunksRoundTrip()
    {
        await AssertRoundTripsAsync(
            BuildImage(16),
            static _ => IszImageBuilder.AdiBz2,
            true
        );
    }

    [Fact]
    public async Task StoredChunksRoundTrip()
    {
        await AssertRoundTripsAsync(BuildImage(16), static _ => IszImageBuilder.AdiData);
    }

    [Fact]
    public async Task ZeroChunksBecomeZeroBytes()
    {
        // An image whose middle is blank, which is what ADI_ZERO exists for. The blank run has to
        // come back as zeros of exactly the right length or everything after it shifts.
        var image = BuildImage(16);
        Array.Clear(image, ChunkSize * 4, ChunkSize * 4);

        await AssertRoundTripsAsync(
            image,
            static index =>
                index is >= 4 and < 8 ? IszImageBuilder.AdiZero : IszImageBuilder.AdiZlib
        );
    }

    [Fact]
    public async Task MixedChunkTypesRoundTrip()
    {
        // Real images mix all four, and the reader has to switch between them without losing its
        // place in the data stream.
        var image = BuildImage(16);
        Array.Clear(image, ChunkSize * 3, ChunkSize);

        await AssertRoundTripsAsync(
            image,
            static index =>
                (index % 4) switch
                {
                    0 => IszImageBuilder.AdiZlib,
                    1 => IszImageBuilder.AdiBz2,
                    2 => IszImageBuilder.AdiData,
                    _ => index == 3 ? IszImageBuilder.AdiZero : IszImageBuilder.AdiZlib
                }
        );
    }

    [Fact]
    public async Task ATrailingPartialChunkIsNotPaddedOut()
    {
        // The last chunk of a real image is nearly always short. Writing a whole chunk's worth would
        // lengthen the image and change every hash of it.
        var image = BuildImage(8, 3);

        await AssertRoundTripsAsync(image, static _ => IszImageBuilder.AdiZlib);
    }

    [Fact]
    public async Task AChecksummedImageRoundTrips()
    {
        // UltraISO writes a 64-byte header with a CRC32 of the restored image; the decoder validates
        // it while writing, so a round trip through it proves the calculation matches the reference.
        await AssertRoundTripsAsync(
            BuildImage(16, 3),
            static _ => IszImageBuilder.AdiZlib,
            expectCompressed: true,
            writeChecksums: true
        );
    }

    [Fact]
    public async Task ZeroChunksWithNoLengthRecordedStillComeBackAsWholeChunks()
    {
        // UltraISO records a zero chunk's uncompressed length, but a writer may leave it zero. Both
        // forms have to yield a whole chunk of zeros; neither stores any bytes.
        var image = BuildImage(16);
        Array.Clear(image, ChunkSize * 4, ChunkSize * 4);

        await AssertRoundTripsAsync(
            image,
            static index =>
                index is >= 4 and < 8 ? IszImageBuilder.AdiZero : IszImageBuilder.AdiZlib,
            zeroChunksCarryLength: false
        );
    }

    [Fact]
    public async Task RawCdSectorSizeIsCarriedThrough()
    {
        // ISZ is documented for 2048-byte ISO images, but the sector size is a header field and the
        // decoder reports whatever it says so the result can be classified correctly.
        var image = BuildImage(4, sectorSize: 2352);
        var iszPath = Path.Combine(_tempDir, "raw.isz");
        IszImageBuilder.WriteSingle(
            iszPath,
            image,
            2352,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiZlib
        );

        var result = await IszDecoder.DecodeAsync(
            iszPath,
            Path.Combine(_tempDir, "raw.iso"),
            Log,
            CancellationToken.None
        );

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(2352, result.SectorSize);
    }

    #endregion

    #region Split segments

    [Fact]
    public async Task SegmentsAreReadInOrderWithAChunkStraddlingTheBoundary()
    {
        var image = BuildImage(16);
        var iszPath = Path.Combine(_tempDir, "split.isz");

        // Cut at an offset that is deliberately not a chunk boundary, which is the case the segment
        // table's "left_size" exists for.
        IszImageBuilder.WriteSplit(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiData,
            (ChunkSize * 4) + 111
        );

        Assert.True(File.Exists(IszImageBuilder.GetSecondSegmentPath(iszPath)));

        var outputPath = Path.Combine(_tempDir, "split.iso");
        var result = await IszDecoder.DecodeAsync(iszPath, outputPath, Log, CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(image, await File.ReadAllBytesAsync(outputPath));
    }

    [Fact]
    public async Task CompressedSegmentsAreReadInOrder()
    {
        var image = BuildImage(12);
        var iszPath = Path.Combine(_tempDir, "splitz.isz");
        IszImageBuilder.WriteSplit(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiZlib,
            200
        );

        var outputPath = Path.Combine(_tempDir, "splitz.iso");
        var result = await IszDecoder.DecodeAsync(iszPath, outputPath, Log, CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(image, await File.ReadAllBytesAsync(outputPath));
    }

    [Fact]
    public async Task AChecksummedSplitImageRoundTrips()
    {
        // The checksum covers the whole restored image, so it also proves the segments were read in
        // the right order and a chunk straddling the boundary was stitched without losing a byte.
        var image = BuildImage(16);
        var iszPath = Path.Combine(_tempDir, "splitcrc.isz");
        IszImageBuilder.WriteSplit(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiZlib,
            200,
            writeChecksums: true
        );

        var outputPath = Path.Combine(_tempDir, "splitcrc.iso");
        var result = await IszDecoder.DecodeAsync(iszPath, outputPath, Log, CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(image, await File.ReadAllBytesAsync(outputPath));
    }

    [Fact]
    public async Task APartNamedSplitImageIsReadInOrder()
    {
        var image = BuildImage(12);
        var iszPath = Path.Combine(_tempDir, "game.part01.isz");

        IszImageBuilder.WriteSplit(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiZlib,
            200
        );

        Assert.True(File.Exists(Path.Combine(_tempDir, "game.part02.isz")));

        var outputPath = Path.Combine(_tempDir, "game.iso");
        var result = await IszDecoder.DecodeAsync(iszPath, outputPath, Log, CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(image, await File.ReadAllBytesAsync(outputPath));
    }

    [Fact]
    public async Task AMissingSegmentIsNamedRatherThanHalfDecoded()
    {
        var image = BuildImage(16);
        var iszPath = Path.Combine(_tempDir, "incomplete.isz");
        IszImageBuilder.WriteSplit(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiData,
            ChunkSize * 4,
            writeSecondSegment: false
        );

        var outputPath = Path.Combine(_tempDir, "incomplete.iso");
        var result = await IszDecoder.DecodeAsync(iszPath, outputPath, Log, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(
            "incomplete.i01",
            result.FailureReason ?? string.Empty,
            StringComparison.Ordinal
        );
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ASegmentFromAnotherImageIsRejected()
    {
        // Segments are told apart only by the volume serial number, so mixing two rips of the same
        // game would otherwise splice them together silently.
        var image = BuildImage(16);
        var iszPath = Path.Combine(_tempDir, "foreign.isz");
        IszImageBuilder.WriteSplit(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiData,
            ChunkSize * 4,
            0x99887766
        );

        var result = await IszDecoder.DecodeAsync(
            iszPath,
            Path.Combine(_tempDir, "foreign.iso"),
            Log,
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains(
            "different ISZ image",
            result.FailureReason ?? string.Empty,
            StringComparison.Ordinal
        );
    }

    #endregion

    #region Refusals

    [Fact]
    public async Task AChecksumMismatchIsRefusedAndTheOutputDeleted()
    {
        // The 64-byte header carries a checksum of the restored image. A file whose data was damaged
        // in a way that still decodes to the right length must be refused, not converted.
        var image = BuildImage(8);
        var iszPath = Path.Combine(_tempDir, "badcrc.isz");
        IszImageBuilder.WriteSingle(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiData,
            writeChecksums: true
        );

        // Flip a stored byte well past the tables, leaving every length intact.
        var bytes = await File.ReadAllBytesAsync(iszPath);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(iszPath, bytes);

        var outputPath = Path.Combine(_tempDir, "badcrc.iso");
        var result = await IszDecoder.DecodeAsync(iszPath, outputPath, Log, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("checksum", result.FailureReason ?? string.Empty, StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task AFileThatIsNotAnIszIsReported()
    {
        var path = Path.Combine(_tempDir, "notisz.isz");
        await File.WriteAllBytesAsync(path, new byte[512]);

        var result = await IszDecoder.DecodeAsync(
            path,
            Path.Combine(_tempDir, "notisz.iso"),
            Log,
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains(
            "not an ISZ image",
            result.FailureReason ?? string.Empty,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task AnEncryptedImageIsRefusedWithoutWritingAnything()
    {
        var image = BuildImage(4);
        var iszPath = Path.Combine(_tempDir, "locked.isz");
        IszImageBuilder.WriteSingle(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiZlib,
            4
        );

        var outputPath = Path.Combine(_tempDir, "locked.iso");
        var result = await IszDecoder.DecodeAsync(iszPath, outputPath, Log, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("AES-256", result.FailureReason ?? string.Empty, StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ATruncatedFileIsReportedInsteadOfProducingAShortImage()
    {
        // A half-downloaded ISZ. The short image would convert and pass chdman, so the shortfall has
        // to be caught here.
        var image = BuildImage(16);
        var iszPath = Path.Combine(_tempDir, "cut.isz");
        IszImageBuilder.WriteSingle(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiData
        );

        await using (var file = new FileStream(iszPath, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(file.Length - (ChunkSize * 3));
        }

        var result = await IszDecoder.DecodeAsync(
            iszPath,
            Path.Combine(_tempDir, "cut.iso"),
            Log,
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains(
            "truncated",
            result.FailureReason ?? string.Empty,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ATruncatedChunkTableIsReported()
    {
        var image = BuildImage(16);
        var iszPath = Path.Combine(_tempDir, "notable.isz");
        IszImageBuilder.WriteSingle(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiData
        );

        await using (var file = new FileStream(iszPath, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(IszImageBuilder.HeaderLength + 4);
        }

        var result = await IszDecoder.DecodeAsync(
            iszPath,
            Path.Combine(_tempDir, "notable.iso"),
            Log,
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.Contains(
            "truncated",
            result.FailureReason ?? string.Empty,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task CorruptCompressedDataIsReported()
    {
        var image = BuildImage(8);
        var iszPath = Path.Combine(_tempDir, "damaged.isz");
        IszImageBuilder.WriteSingle(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiZlib
        );

        // Corrupt the deflate stream well past the tables.
        var bytes = await File.ReadAllBytesAsync(iszPath);
        for (
            var offset = bytes.Length / 2;
            offset < Math.Min(bytes.Length, (bytes.Length / 2) + 64);
            offset++
        )
        {
            bytes[offset] ^= 0xFF;
        }

        await File.WriteAllBytesAsync(iszPath, bytes);

        var result = await IszDecoder.DecodeAsync(
            iszPath,
            Path.Combine(_tempDir, "damaged.iso"),
            Log,
            CancellationToken.None
        );

        Assert.False(result.Success);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task HeaderIsReadableFromAWholeFileImage()
    {
        var image = BuildImage(8);
        var iszPath = Path.Combine(_tempDir, "probe.isz");
        IszImageBuilder.WriteSingle(
            iszPath,
            image,
            SectorSize,
            ChunkSize,
            3,
            static _ => IszImageBuilder.AdiZlib
        );

        var header = await IszDecoder.TryReadHeaderAsync(iszPath, CancellationToken.None);

        Assert.NotNull(header);
        Assert.Equal(image.Length, header.ImageSizeBytes);
        Assert.Equal(SectorSize, header.SectorSize);
        Assert.False(header.IsSegmented);
    }

    [Fact]
    public async Task HeaderReadOnANonIszReturnsNull()
    {
        var path = Path.Combine(_tempDir, "plain.bin");
        await File.WriteAllBytesAsync(path, new byte[64]);

        Assert.Null(await IszDecoder.TryReadHeaderAsync(path, CancellationToken.None));
    }

    #endregion
}