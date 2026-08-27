using PBPSharp.Models;

namespace BatchConvertToCHD.Tests;

public class PbpHeaderTests
{
    [Fact]
    public void MagicValueIsCorrect()
    {
        Assert.Equal(0x50425000u, PbpHeader.MagicValue);
    }

    [Fact]
    public void HeaderSizeIs40()
    {
        Assert.Equal(0x28, PbpHeader.HeaderSize);
    }

    [Fact]
    public void DefaultHeaderIsNotValid()
    {
        var header = default(PbpHeader);
        Assert.False(header.IsValid);
    }

    [Fact]
    public void ConstructorSetsAllProperties()
    {
        var header = new PbpHeader(1, 0x28, 0x100, 0x200, 0x300, 0x400, 0x500, 0x600, 0x700);

        Assert.Equal(1u, header.Version);
        Assert.Equal(0x28, header.SfoOffset);
        Assert.Equal(0x100, header.Icon0Offset);
        Assert.Equal(0x200, header.Icon1Offset);
        Assert.Equal(0x300, header.Pic0Offset);
        Assert.Equal(0x400, header.Pic1Offset);
        Assert.Equal(0x500, header.Snd0Offset);
        Assert.Equal(0x600, header.DataPspOffset);
        Assert.Equal(0x700, header.DataPsarOffset);
        Assert.True(header.IsValid);
    }

    [Fact]
    public void IsValidReturnsTrueAfterConstruction()
    {
        var header = new PbpHeader(1, 0x28, 0, 0, 0, 0, 0, 0, 0);
        Assert.True(header.IsValid);
    }

    [Fact]
    public void ZeroOffsetsAreValid()
    {
        var header = new PbpHeader(0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.True(header.IsValid);
        Assert.Equal(0u, header.Version);
        Assert.Equal(0, header.SfoOffset);
    }
}