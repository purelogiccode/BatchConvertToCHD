using CCDSharp.Models;

namespace BatchConvertToCHD.Tests;

public class CcdModelTests
{
    [Fact]
    public void TrackIndex01LbaReturnsMinusOneWhenMissing()
    {
        var track = new Track();

        Assert.Equal(-1, track.Index01Lba);
    }

    [Fact]
    public void TrackIndex01LbaReturnsIndexOneValue()
    {
        var track = new Track();
        track.Indexes[1] = 1234;

        Assert.Equal(1234, track.Index01Lba);
    }

    [Fact]
    public void TrackCueTrackTypeMapsAudio()
    {
        var track = new Track { Mode = TrackMode.Audio };

        Assert.Equal("AUDIO", track.CueTrackType);
        Assert.True(track.IsAudio);
    }

    [Fact]
    public void TrackCueTrackTypeMapsMode1()
    {
        var track = new Track { Mode = TrackMode.Mode1 };

        Assert.Equal("MODE1/2352", track.CueTrackType);
        Assert.False(track.IsAudio);
    }

    [Fact]
    public void TrackCueTrackTypeMapsMode2()
    {
        var track = new Track { Mode = TrackMode.Mode2 };

        Assert.Equal("MODE2/2352", track.CueTrackType);
        Assert.False(track.IsAudio);
    }

    [Fact]
    public void TrackDefaultsAreEmpty()
    {
        var track = new Track();

        Assert.Equal(0, track.Number);
        Assert.Empty(track.Indexes);
        Assert.Null(track.Flags);
        Assert.Null(track.Isrc);
    }

    [Fact]
    public void DiscImageDefaultsAreEmpty()
    {
        var disc = new DiscImage();

        Assert.Equal(0, disc.Version);
        Assert.Equal(0, disc.TocEntries);
        Assert.Equal(0, disc.Sessions);
        Assert.False(disc.DataTracksScrambled);
        Assert.Equal(0, disc.CdTextLength);
        Assert.Null(disc.Catalog);
        Assert.Null(disc.FilePath);
        Assert.Null(disc.ImgFilePath);
        Assert.Null(disc.SubFilePath);
        Assert.Empty(disc.Tracks);
    }

    [Fact]
    public void TrackModeValuesMatchCloneCdSpec()
    {
        Assert.Equal(0, (int)TrackMode.Audio);
        Assert.Equal(1, (int)TrackMode.Mode1);
        Assert.Equal(2, (int)TrackMode.Mode2);
    }

    [Fact]
    public void SectorConstantsMatchCdStandard()
    {
        Assert.Equal(2352, SectorConstants.RawSectorSize);
        Assert.Equal(2048, SectorConstants.UserDataSize);
        Assert.Equal(15, SectorConstants.ModeOffset);
        Assert.Equal(16, SectorConstants.Mode1DataOffset);
        Assert.Equal(24, SectorConstants.Mode2Form1DataOffset);
        Assert.Equal(75, SectorConstants.FramesPerSecond);
        Assert.Equal(60, SectorConstants.SecondsPerMinute);
        Assert.Equal(4500, SectorConstants.FramesPerMinute);
        Assert.Equal(150, SectorConstants.LeadInSectors);
    }

    [Fact]
    public void SyncMarkIsTwelveBytesStartingAndEndingWithZero()
    {
        Assert.Equal(12, SectorConstants.SyncMark.Length);
        Assert.Equal(0x00, SectorConstants.SyncMark[0]);
        Assert.Equal(0xFF, SectorConstants.SyncMark[1]);
        Assert.Equal(0xFF, SectorConstants.SyncMark[10]);
        Assert.Equal(0x00, SectorConstants.SyncMark[11]);
    }
}