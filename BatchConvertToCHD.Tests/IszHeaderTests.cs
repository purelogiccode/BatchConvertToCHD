using BatchConvertToCHD.Utilities.Isz;

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
            0xDEADBEEF,
            48,
            1
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
        var header = IszHeader.TryRead(BuildValid(totalSectors: 337_216, sectorSize: 2048));

        Assert.NotNull(header);
        Assert.Equal(337_216L * 2048, header.ImageSizeBytes);
    }

    [Fact]
    public void ImageSizeDoesNotOverflowOnADualLayerDvd()
    {
        // 8.5 GB of 2048-byte sectors overflows a 32-bit product, which would make the size check
        // reject a perfectly good image.
        var header = IszHeader.TryRead(BuildValid(totalSectors: 4_173_824, sectorSize: 2048));

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
    public void HeaderWithNoChunkTableIsRefused()
    {
        var header = IszHeader.TryRead(BuildValid(chunkTableOffset: 0));

        Assert.NotNull(header);
        Assert.Contains(
            "no chunk table",
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
        uint segmentTableOffset = 0
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
            1,
            chunkTableOffset,
            segmentTableOffset,
            96,
            IszImageBuilder.DefaultVolumeSerial
        );
    }
}