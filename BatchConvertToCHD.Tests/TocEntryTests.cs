using PBPSharp.Models;

namespace BatchConvertToCHD.Tests;

public class TocEntryTests
{
    [Fact]
    public void DefaultValuesAreCorrect()
    {
        var entry = new TocEntry();
        Assert.Equal(0, (int)entry.TrackType);
        Assert.Equal(0, entry.TrackNo);
        Assert.Equal(0, entry.Minutes);
        Assert.Equal(0, entry.Seconds);
        Assert.Equal(0, entry.Frames);
    }

    [Fact]
    public void PropertiesCanBeSet()
    {
        var entry = new TocEntry
        {
            TrackType = TrackType.Data,
            TrackNo = 1,
            Minutes = 0,
            Seconds = 2,
            Frames = 0
        };

        Assert.Equal(TrackType.Data, entry.TrackType);
        Assert.Equal(1, entry.TrackNo);
        Assert.Equal(0, entry.Minutes);
        Assert.Equal(2, entry.Seconds);
        Assert.Equal(0, entry.Frames);
    }

    [Fact]
    public void TrackTypeDataValue()
    {
        Assert.Equal(0x41, (int)TrackType.Data);
    }

    [Fact]
    public void TrackTypeAudioValue()
    {
        Assert.Equal(0x01, (int)TrackType.Audio);
    }

    [Fact]
    public void TrackTypeCanBeAudio()
    {
        var entry = new TocEntry
        {
            TrackType = TrackType.Audio,
            TrackNo = 2,
            Minutes = 4,
            Seconds = 30,
            Frames = 25
        };

        Assert.Equal(TrackType.Audio, entry.TrackType);
        Assert.Equal(2, entry.TrackNo);
        Assert.Equal(4, entry.Minutes);
        Assert.Equal(30, entry.Seconds);
        Assert.Equal(25, entry.Frames);
    }
}