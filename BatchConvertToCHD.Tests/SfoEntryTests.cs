using PBPSharp.Models;

namespace BatchConvertToCHD.Tests;

public class SfoEntryTests
{
    [Fact]
    public void DefaultValuesAreCorrect()
    {
        var entry = new SfoEntry();
        Assert.Equal(string.Empty, entry.Key);
        Assert.Equal(0, entry.Format);
        Assert.Equal(0u, entry.Length);
        Assert.Equal(0u, entry.MaxLength);
        Assert.Null(entry.Value);
    }

    [Fact]
    public void PropertiesCanBeSet()
    {
        var entry = new SfoEntry
        {
            Key = "TITLE",
            Format = 0x0204,
            Length = 10,
            MaxLength = 32,
            Value = "Test Game",
        };

        Assert.Equal("TITLE", entry.Key);
        Assert.Equal(0x0204, entry.Format);
        Assert.Equal(10u, entry.Length);
        Assert.Equal(32u, entry.MaxLength);
        Assert.Equal("Test Game", entry.Value);
    }

    [Fact]
    public void ValueCanHoldUInt32()
    {
        var entry = new SfoEntry
        {
            Key = "BOOTABLE",
            Format = 0x0404,
            Length = 4,
            MaxLength = 4,
            Value = 1u,
        };

        Assert.Equal(1u, entry.Value);
        Assert.IsType<uint>(entry.Value);
    }

    [Fact]
    public void ValueCanBeNull()
    {
        var entry = new SfoEntry { Key = "TEST", Value = null };

        Assert.Null(entry.Value);
    }

    [Theory]
    [InlineData(0x0204)]
    [InlineData(0x0404)]
    public void FormatValuesAreCorrect(ushort format)
    {
        var entry = new SfoEntry { Format = format };
        Assert.Equal(format, entry.Format);
    }
}