using PBPSharp.Models;

namespace BatchConvertToCHD.Tests;

public class SfoDataTests
{
    [Fact]
    public void DefaultValuesAreCorrect()
    {
        var sfo = new SfoData();
        Assert.Equal(0u, sfo.Magic);
        Assert.Equal(0u, sfo.Version);
        Assert.Equal(0u, sfo.KeyTableOffset);
        Assert.Equal(0u, sfo.DataTableOffset);
        Assert.NotNull(sfo.Entries);
        Assert.Empty(sfo.Entries);
        Assert.Equal(0u, sfo.Size);
    }

    [Fact]
    public void PropertiesCanBeSet()
    {
        var sfo = new SfoData
        {
            Magic = 0x46535000,
            Version = 1,
            KeyTableOffset = 100,
            DataTableOffset = 200,
            Size = 1024,
        };

        Assert.Equal(0x46535000u, sfo.Magic);
        Assert.Equal(1u, sfo.Version);
        Assert.Equal(100u, sfo.KeyTableOffset);
        Assert.Equal(200u, sfo.DataTableOffset);
        Assert.Equal(1024u, sfo.Size);
    }

    [Fact]
    public void GetStringReturnsNullForMissingKey()
    {
        var sfo = new SfoData();
        Assert.Null(sfo.GetString("NONEXISTENT"));
    }

    [Fact]
    public void GetStringReturnsValueForExistingKey()
    {
        var sfo = new SfoData
        {
            Entries = new List<SfoEntry>
            {
                new()
                {
                    Key = "TITLE",
                    Format = 0x0204,
                    Value = "Test Game",
                },
            },
        };

        Assert.Equal("Test Game", sfo.GetString("TITLE"));
    }

    [Fact]
    public void GetStringReturnsNullForNonStringEntry()
    {
        var sfo = new SfoData
        {
            Entries = new List<SfoEntry>
            {
                new()
                {
                    Key = "BOOTABLE",
                    Format = 0x0404,
                    Value = 1u,
                },
            },
        };

        Assert.Null(sfo.GetString("BOOTABLE"));
    }

    [Fact]
    public void GetUInt32ReturnsNullForMissingKey()
    {
        var sfo = new SfoData();
        Assert.Null(sfo.GetUInt32("NONEXISTENT"));
    }

    [Fact]
    public void GetUInt32ReturnsValueForExistingKey()
    {
        var sfo = new SfoData
        {
            Entries = new List<SfoEntry>
            {
                new()
                {
                    Key = "BOOTABLE",
                    Format = 0x0404,
                    Value = 1u,
                },
            },
        };

        Assert.Equal(1u, sfo.GetUInt32("BOOTABLE"));
    }

    [Fact]
    public void GetUInt32ReturnsNullForStringEntry()
    {
        var sfo = new SfoData
        {
            Entries = new List<SfoEntry>
            {
                new()
                {
                    Key = "TITLE",
                    Format = 0x0204,
                    Value = "Test Game",
                },
            },
        };

        Assert.Null(sfo.GetUInt32("TITLE"));
    }

    [Fact]
    public void GetStringReturnsFirstMatchingEntry()
    {
        var sfo = new SfoData
        {
            Entries = new List<SfoEntry>
            {
                new()
                {
                    Key = "TITLE",
                    Format = 0x0204,
                    Value = "First",
                },
                new()
                {
                    Key = "TITLE",
                    Format = 0x0204,
                    Value = "Second",
                },
            },
        };

        Assert.Equal("First", sfo.GetString("TITLE"));
    }

    [Fact]
    public void KeysClassHasExpectedConstants()
    {
        Assert.Equal("BOOTABLE", SfoData.Keys.Bootable);
        Assert.Equal("CATEGORY", SfoData.Keys.Category);
        Assert.Equal("DISC_ID", SfoData.Keys.DiscId);
        Assert.Equal("DISC_VERSION", SfoData.Keys.DiscVersion);
        Assert.Equal("LICENSE", SfoData.Keys.License);
        Assert.Equal("PARENTAL_LEVEL", SfoData.Keys.ParentalLevel);
        Assert.Equal("PSP_SYSTEM_VER", SfoData.Keys.PspSystemVer);
        Assert.Equal("REGION", SfoData.Keys.Region);
        Assert.Equal("TITLE", SfoData.Keys.Title);
    }

    [Fact]
    public void EntriesCanBeReplaced()
    {
        var sfo = new SfoData();
        Assert.Empty(sfo.Entries);

        sfo.Entries = new List<SfoEntry>
        {
            new() { Key = "TEST", Value = "value" },
        };

        Assert.Single(sfo.Entries);
    }
}