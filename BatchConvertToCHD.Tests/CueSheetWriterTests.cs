using PBPSharp;
using PBPSharp.Models;

namespace BatchConvertToCHD.Tests;

public class CueSheetWriterTests
{
    [Fact]
    public void GenerateCueSheetWithSingleDataTrack()
    {
        var toc = new List<TocEntry>
        {
            new()
            {
                TrackType = TrackType.Data,
                TrackNo = 1,
                Minutes = 0,
                Seconds = 2,
                Frames = 0,
            },
        };

        var cue = CueSheetWriter.GenerateCueSheet("game.bin", toc);

        Assert.Contains("FILE \"game.bin\" BINARY", cue, StringComparison.Ordinal);
        Assert.Contains("TRACK 01 MODE2/2352", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 00:02:00", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCueSheetWithAudioTrack()
    {
        var toc = new List<TocEntry>
        {
            new()
            {
                TrackType = TrackType.Audio,
                TrackNo = 2,
                Minutes = 4,
                Seconds = 30,
                Frames = 25,
            },
        };

        var cue = CueSheetWriter.GenerateCueSheet("game.bin", toc);

        Assert.Contains("TRACK 02 AUDIO", cue, StringComparison.Ordinal);
        // 4:30:25 - 150 frames (2 seconds) = 4:28:25
        Assert.Contains("INDEX 00 04:28:25", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 04:30:25", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCueSheetWithMultipleTracks()
    {
        var toc = new List<TocEntry>
        {
            new()
            {
                TrackType = TrackType.Data,
                TrackNo = 1,
                Minutes = 0,
                Seconds = 2,
                Frames = 0,
            },
            new()
            {
                TrackType = TrackType.Audio,
                TrackNo = 2,
                Minutes = 4,
                Seconds = 30,
                Frames = 0,
            },
            new()
            {
                TrackType = TrackType.Audio,
                TrackNo = 3,
                Minutes = 8,
                Seconds = 15,
                Frames = 50,
            },
        };

        var cue = CueSheetWriter.GenerateCueSheet("disc.bin", toc);

        Assert.Contains("FILE \"disc.bin\" BINARY", cue, StringComparison.Ordinal);
        Assert.Contains("TRACK 01 MODE2/2352", cue, StringComparison.Ordinal);
        Assert.Contains("TRACK 02 AUDIO", cue, StringComparison.Ordinal);
        Assert.Contains("TRACK 03 AUDIO", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 00:02:00", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 04:30:00", cue, StringComparison.Ordinal);
        Assert.Contains("INDEX 01 08:15:50", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCueSheetAudioTrackHasIndex00()
    {
        var toc = new List<TocEntry>
        {
            new()
            {
                TrackType = TrackType.Audio,
                TrackNo = 1,
                Minutes = 0,
                Seconds = 2,
                Frames = 0,
            },
        };

        var cue = CueSheetWriter.GenerateCueSheet("game.bin", toc);

        // Audio tracks should have INDEX 00 before INDEX 01
        var lines = cue.Split('\n');
        var index00Line = Array.FindIndex(
            lines,
            l => l.Trim().StartsWith("INDEX 00", StringComparison.Ordinal)
        );
        var index01Line = Array.FindIndex(
            lines,
            l => l.Trim().StartsWith("INDEX 01", StringComparison.Ordinal)
        );
        Assert.True(index00Line >= 0, "Audio track should have INDEX 00");
        Assert.True(index01Line > index00Line, "INDEX 01 should come after INDEX 00");
    }

    [Fact]
    public void GenerateCueSheetDataTrackDoesNotHaveIndex00()
    {
        var toc = new List<TocEntry>
        {
            new()
            {
                TrackType = TrackType.Data,
                TrackNo = 1,
                Minutes = 0,
                Seconds = 2,
                Frames = 0,
            },
        };

        var cue = CueSheetWriter.GenerateCueSheet("game.bin", toc);

        Assert.DoesNotContain("INDEX 00", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCueSheetEmptyToc()
    {
        TocEntry[] toc = [];
        var cue = CueSheetWriter.GenerateCueSheet("game.bin", toc);

        Assert.Contains("FILE \"game.bin\" BINARY", cue, StringComparison.Ordinal);
        Assert.DoesNotContain("TRACK", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCueSheetPreservesFileName()
    {
        var toc = new List<TocEntry>
        {
            new()
            {
                TrackType = TrackType.Data,
                TrackNo = 1,
                Minutes = 0,
                Seconds = 2,
                Frames = 0,
            },
        };

        var cue = CueSheetWriter.GenerateCueSheet("My Game (USA).bin", toc);
        Assert.Contains("FILE \"My Game (USA).bin\" BINARY", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCueSheetFormatsMsfCorrectly()
    {
        var toc = new List<TocEntry>
        {
            new()
            {
                TrackType = TrackType.Data,
                TrackNo = 1,
                Minutes = 1,
                Seconds = 2,
                Frames = 3,
            },
        };

        var cue = CueSheetWriter.GenerateCueSheet("game.bin", toc);
        Assert.Contains("INDEX 01 01:02:03", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCueSheetAudioTrackIndex00Subtracts150Frames()
    {
        // 150 frames = 2 seconds
        var toc = new List<TocEntry>
        {
            new()
            {
                TrackType = TrackType.Audio,
                TrackNo = 2,
                Minutes = 0,
                Seconds = 2,
                Frames = 0,
            },
        };

        var cue = CueSheetWriter.GenerateCueSheet("game.bin", toc);

        // INDEX 01 at 00:02:00
        Assert.Contains("INDEX 01 00:02:00", cue, StringComparison.Ordinal);
        // INDEX 00 at 00:02:00 - 150 frames = 00:00:00
        Assert.Contains("INDEX 00 00:00:00", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCueSheetAudioTrackIndex00DoesNotGoBelowZero()
    {
        // Track at 00:01:00 (75 frames), subtracting 150 would go negative
        var toc = new List<TocEntry>
        {
            new()
            {
                TrackType = TrackType.Audio,
                TrackNo = 2,
                Minutes = 0,
                Seconds = 1,
                Frames = 0,
            },
        };

        var cue = CueSheetWriter.GenerateCueSheet("game.bin", toc);

        // INDEX 00 should clamp to 00:00:00
        Assert.Contains("INDEX 00 00:00:00", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCueSheetTrackNumbersAreZeroPadded()
    {
        var toc = new List<TocEntry>
        {
            new()
            {
                TrackType = TrackType.Data,
                TrackNo = 1,
                Minutes = 0,
                Seconds = 2,
                Frames = 0,
            },
        };

        var cue = CueSheetWriter.GenerateCueSheet("game.bin", toc);
        Assert.Contains("TRACK 01", cue, StringComparison.Ordinal);
    }
}
