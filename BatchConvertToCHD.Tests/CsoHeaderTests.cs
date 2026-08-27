using CSOSharp.Models;

namespace BatchConvertToCHD.Tests;

public class CsoHeaderTests
{
    [Fact]
    public void MagicValueIsCorrect()
    {
        Assert.Equal(0x4F534943u, CsoHeader.MagicValue);
    }

    [Fact]
    public void ExpectedHeaderSizeIs24()
    {
        Assert.Equal(24, CsoHeader.ExpectedHeaderSize);
    }

    [Fact]
    public void DefaultHeaderIsNotValid()
    {
        var header = default(CsoHeader);
        Assert.False(header.IsValid);
        Assert.Equal(0u, header.Magic);
    }

    [Fact]
    public void ConstructorSetsAllProperties()
    {
        var header = new CsoHeader(0x4F534943u, 24, 1024 * 1024, 2048, 1, 0);

        Assert.Equal(0x4F534943u, header.Magic);
        Assert.Equal(24u, header.HeaderSize);
        Assert.Equal(1024UL * 1024, header.UncompressedSize);
        Assert.Equal(2048u, header.BlockSize);
        Assert.Equal(1, header.Version);
        Assert.Equal(0, header.IndexOffsetShift);
        Assert.True(header.IsValid);
    }

    [Fact]
    public void TotalBlocksCalculatedCorrectly()
    {
        var header = new CsoHeader(0x4F534943u, 24, 2048 * 100, 2048, 1, 0);
        Assert.Equal(100u, header.TotalBlocks);
    }

    [Fact]
    public void TotalBlocksZeroWhenBlockSizeIsZero()
    {
        var header = new CsoHeader(0x4F534943u, 24, 1024, 0, 1, 0);
        Assert.Equal(0u, header.TotalBlocks);
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(2, false, true)]
    [InlineData(0, false, false)]
    [InlineData(3, false, false)]
    public void VersionPropertiesReturnCorrectValues(byte version, bool expectedV1, bool expectedV2)
    {
        var header = new CsoHeader(0x4F534943u, 24, 2048, 2048, version, 0);
        Assert.Equal(expectedV1, header.IsV1);
        Assert.Equal(expectedV2, header.IsV2);
    }

    [Fact]
    public void IsValidReturnsTrueForCorrectMagic()
    {
        var header = new CsoHeader(CsoHeader.MagicValue, 24, 2048, 2048, 1, 0);
        Assert.True(header.IsValid);
    }

    [Fact]
    public void IsValidReturnsFalseForWrongMagic()
    {
        var header = new CsoHeader(0x12345678u, 24, 2048, 2048, 1, 0);
        Assert.False(header.IsValid);
    }

    [Fact]
    public void IndexOffsetShiftIsPreserved()
    {
        var header = new CsoHeader(0x4F534943u, 24, 2048, 2048, 1, 11);
        Assert.Equal(11, header.IndexOffsetShift);
    }
}