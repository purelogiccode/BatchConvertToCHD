using ISZSharp;

namespace BatchConvertToCHD.Tests;

public class IszHeaderTests
{
    [Fact]
    public void EveryFieldIsReadAtItsDocumentedOffset()
    {
        // The whole format hangs off these offsets. A single wrong one produces a header that parses
        // and an image that is quietly wrong, so each field gets its own recognisable value.
        var bytes = IszImageBuilder.BuildHeader(
            2048,
            0x00ABCDEF,
            0,
            0x1122334455667788,
            0x00112233,
            0x00040000,
            3,
            7,
            0x00001234,
            0x00005678,
            0x00009ABC,
            0xDEADBEEF
        );

        var header = IszHeader.TryRead(bytes);

        Assert.NotNull(header);
        Assert.Equal(48, header.HeaderSize);
        Assert.Equal(1, header.Version);
        Assert.Equal(0xDEADBEEFu, header.VolumeSerialNumber);
        Assert.Equal(2048, header.SectorSize);
        Assert.Equal(0x00ABCDEFu, header.TotalSectors);
        Assert.Equal(0, header.PasswordMode);
        Assert.Equal(0x1122334455667788, header.SegmentSize);
        Assert.Equal(0x00112233u, header.ChunkCount);
        Assert.Equal(0x00040000u, header.ChunkSize);
        Assert.Equal(3, header.PointerLength);
        Assert.Equal(7, header.SegmentNumber);
        Assert.Equal(0x00001234u, header.ChunkTableOffset);
        Assert.Equal(0x00005678u, header.SegmentTableOffset);
        Assert.Equal(0x00009ABCu, header.DataOffset);
    }

    [Fact]
    public void ImageSizeIsSectorsTimesSectorSize()
    {
        var header = IszHeader.TryRead(BuildValid(2048, 337_216));

        Assert.NotNull(header);
        Assert.Equal(337_216L * 2048, header.ImageSizeBytes);
    }

    [Fact]
    public void ImageSizeDoesNotOverflowOnADualLayerDvd()
    {
        // 8.5 GB of 2048-byte sectors overflows a 32-bit product, which would make the size check
        // reject a perfectly good image.
        var header = IszHeader.TryRead(BuildValid(2048, 4_173_824));

        Assert.NotNull(header);
        Assert.Equal(8_547_991_552L, header.ImageSizeBytes);
    }

    [Fact]
    public void BytesWithoutTheSignatureAreNotAHeader()
    {
        var bytes = BuildValid();
        bytes[1] = (byte)'X';

        Assert.Null(IszHeader.TryRead(bytes));
        Assert.False(IszHeader.HasSignature(bytes));
    }

    [Fact]
    public void ShortInputIsNotAHeader()
    {
        Assert.Null(IszHeader.TryRead(BuildValid().AsSpan(0, 20)));
    }

    [Fact]
    public void SegmentedFlagFollowsTheSegmentTableOffset()
    {
        var whole = IszHeader.TryRead(BuildValid());
        var split = IszHeader.TryRead(BuildValid(segmentTableOffset: 48));

        Assert.NotNull(whole);
        Assert.NotNull(split);
        Assert.False(whole.IsSegmented);
        Assert.True(split.IsSegmented);
    }

    [Theory]
    [InlineData(2, "AES-128")]
    [InlineData(3, "AES-192")]
    [InlineData(4, "AES-256")]
    [InlineData(1, "password")]
    public void EncryptedImagesAreRefusedAndNamed(int passwordMode, string expectedName)
    {
        var header = IszHeader.TryRead(BuildValid(passwordMode: passwordMode));

        Assert.NotNull(header);
        Assert.True(header.IsEncrypted);
        Assert.Equal(expectedName, header.EncryptionDescription);

        var reason = header.GetUnusableReason();
        Assert.NotNull(reason);
        Assert.Contains("encrypted", reason, StringComparison.Ordinal);
        Assert.Contains(expectedName, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainImageIsUsable()
    {
        var header = IszHeader.TryRead(BuildValid());

        Assert.NotNull(header);
        Assert.False(header.IsEncrypted);
        Assert.Null(header.GetUnusableReason());
    }

    [Fact]
    public void HeaderWithNoChunkTableIsUsable()
    {
        // The spec says a zero pointer offset means there is no chunk table and the data is one
        // uncompressed run, so this is a valid file rather than a damaged one.
        var header = IszHeader.TryRead(BuildValid(chunkTableOffset: 0));

        Assert.NotNull(header);
        Assert.Null(header.GetUnusableReason());
    }

    [Fact]
    public void HeaderWithNoChunkTableSkipsThePointerWidthCheck()
    {
        // No table means no entries, so an odd pointer width describes nothing and is ignored.
        var header = IszHeader.TryRead(BuildValid(pointerLength: 0, chunkTableOffset: 0));

        Assert.NotNull(header);
        Assert.Null(header.GetUnusableReason());
    }

    [Fact]
    public void ExtendedHeaderChecksumsAreReadAtTheirOffsets()
    {
        var bytes = IszImageBuilder.BuildHeader(
            2048,
            64,
            0,
            0,
            2,
            65536,
            3,
            1,
            64,
            0,
            96,
            IszImageBuilder.DefaultVolumeSerial,
            IszImageBuilder.ExtendedHeaderLength,
            1,
            0xAABBCCDD,
            0x00112233,
            0x11223344
        );

        var header = IszHeader.TryRead(bytes);

        Assert.NotNull(header);
        Assert.Equal(IszImageBuilder.ExtendedHeaderLength, header.HeaderSize);
        Assert.True(header.HasChecksums);
        Assert.Equal(0xAABBCCDDu, header.UncompressedCrc);
        Assert.Equal(0x00112233u, header.DataSize);
        Assert.Equal(0x11223344u, header.StoredCrc);
    }

    [Fact]
    public void AFortyEightByteHeaderCarriesNoChecksums()
    {
        var header = IszHeader.TryRead(BuildValid());

        Assert.NotNull(header);
        Assert.False(header.HasChecksums);
        Assert.Null(header.UncompressedCrc);
        Assert.Null(header.DataSize);
        Assert.Null(header.StoredCrc);
    }

    [Fact]
    public void ANonVersionOneHeaderIsRefused()
    {
        var header = IszHeader.TryRead(BuildValid(version: 2));

        Assert.NotNull(header);
        Assert.Contains(
            "version",
            header.GetUnusableReason() ?? string.Empty,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void ALaterSegmentIsRefusedInFavourOfTheFirst()
    {
        // Opening .i01 directly would decode from the middle of the chunk stream, so it is refused
        // with the name of the file that should be opened instead.
        var header = IszHeader.TryRead(BuildValid(segmentNumber: 2));

        Assert.NotNull(header);
        Assert.Contains(
            "first segment",
            header.GetUnusableReason() ?? string.Empty,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void HeaderDescribingNoImageIsRefused()
    {
        var header = IszHeader.TryRead(BuildValid(totalSectors: 0));

        Assert.NotNull(header);
        Assert.NotNull(header.GetUnusableReason());
    }

    [Fact]
    public void ImplausibleChunkSizeIsRefusedRatherThanAllocated()
    {
        // The chunk size decides a buffer allocation, so a damaged header must not be trusted with it.
        var header = IszHeader.TryRead(BuildValid(chunkSize: 0x7F000000));

        Assert.NotNull(header);
        Assert.Contains(
            "chunk size",
            header.GetUnusableReason() ?? string.Empty,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(255)]
    public void UnreadablePointerWidthsAreRefused(int pointerLength)
    {
        var header = IszHeader.TryRead(BuildValid(pointerLength: pointerLength));

        Assert.NotNull(header);
        Assert.Contains(
            "chunk table",
            header.GetUnusableReason() ?? string.Empty,
            StringComparison.Ordinal
        );
    }

    private static byte[] BuildValid(
        int sectorSize = 2048,
        uint totalSectors = 64,
        int passwordMode = 0,
        uint chunkSize = 65536,
        int pointerLength = 3,
        uint chunkTableOffset = 48,
        uint segmentTableOffset = 0,
        int segmentNumber = 0,
        int version = 1
    )
    {
        return IszImageBuilder.BuildHeader(
            sectorSize,
            totalSectors,
            passwordMode,
            0,
            2,
            chunkSize,
            pointerLength,
            segmentNumber,
            chunkTableOffset,
            segmentTableOffset,
            96,
            IszImageBuilder.DefaultVolumeSerial,
            version: version
        );
    }
}