using CCDSharp.Models;
using CCDSharp.Parsers;

namespace BatchConvertToCHD.Tests;

public class CcdParserTests : IDisposable
{
    private const string FullCcd = """
        [CloneCD]
        Version=3

        [Disc]
        TocEntries=3
        Sessions=1
        DataTracksScrambled=0
        CDTextLength=0
        CATALOG=1234567890123

        [Session 1]
        [TRACK 1]
        MODE=1
        INDEX 1=0

        [TRACK 2]
        MODE=0
        INDEX 0=100
        INDEX 1=150
        FLAGS=DCP
        ISRC=ABC123456789
        """;

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"CcdParserTests_{Guid.NewGuid():N}"
    );

    public CcdParserTests()
    {
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

    private static DiscImage Parse(string content)
    {
        using var reader = new StringReader(content);
        return CcdParser.Parse(reader);
    }

    private string WriteCcd(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ParseReadsCloneCdVersion()
    {
        var disc = Parse(FullCcd);

        Assert.Equal(3, disc.Version);
    }

    [Fact]
    public void ParseReadsDiscFields()
    {
        var disc = Parse(FullCcd);

        Assert.Equal(3, disc.TocEntries);
        Assert.Equal(1, disc.Sessions);
        Assert.False(disc.DataTracksScrambled);
        Assert.Equal(0, disc.CdTextLength);
        Assert.Equal("1234567890123", disc.Catalog);
    }

    [Fact]
    public void ParseReadsAllTracks()
    {
        var disc = Parse(FullCcd);

        Assert.Equal(2, disc.Tracks.Count);
        Assert.Equal(1, disc.Tracks[0].Number);
        Assert.Equal(2, disc.Tracks[1].Number);
    }

    [Fact]
    public void ParseMapsModeOneToMode1()
    {
        var disc = Parse(FullCcd);

        Assert.Equal(TrackMode.Mode1, disc.Tracks[0].Mode);
        Assert.Equal("MODE1/2352", disc.Tracks[0].CueTrackType);
    }

    [Fact]
    public void ParseMapsModeZeroToAudio()
    {
        var disc = Parse(FullCcd);

        Assert.Equal(TrackMode.Audio, disc.Tracks[1].Mode);
        Assert.True(disc.Tracks[1].IsAudio);
    }

    [Fact]
    public void ParseReadsTrackIndexes()
    {
        var disc = Parse(FullCcd);

        Assert.Equal(0, disc.Tracks[0].Index01Lba);
        Assert.Equal(100, disc.Tracks[1].Indexes[0]);
        Assert.Equal(150, disc.Tracks[1].Index01Lba);
    }

    [Fact]
    public void ParseReadsFlagsAndIsrc()
    {
        var disc = Parse(FullCcd);

        Assert.Equal("DCP", disc.Tracks[1].Flags);
        Assert.Equal("ABC123456789", disc.Tracks[1].Isrc);
    }

    [Fact]
    public void ParseMapsModeTwoToMode2()
    {
        var disc = Parse(
            """
            [TRACK 1]
            MODE=2
            INDEX 1=0
            """
        );

        Assert.Equal(TrackMode.Mode2, disc.Tracks[0].Mode);
        Assert.Equal("MODE2/2352", disc.Tracks[0].CueTrackType);
    }

    [Fact]
    public void ParseUnknownModeFallsBackToMode1()
    {
        var disc = Parse(
            """
            [TRACK 1]
            MODE=9
            INDEX 1=0
            """
        );

        Assert.Equal(TrackMode.Mode1, disc.Tracks[0].Mode);
    }

    [Fact]
    public void ParseTrackNumberGapCreatesPlaceholderTracks()
    {
        var disc = Parse(
            """
            [TRACK 3]
            MODE=1
            INDEX 1=0
            """
        );

        Assert.Equal(3, disc.Tracks.Count);
        Assert.Equal(1, disc.Tracks[0].Number);
        Assert.Equal(2, disc.Tracks[1].Number);
        Assert.Equal(3, disc.Tracks[2].Number);
        Assert.Equal(TrackMode.Mode1, disc.Tracks[2].Mode);
    }

    [Fact]
    public void ParseSessionSectionResetsTrackContext()
    {
        var disc = Parse(
            """
            [TRACK 1]
            MODE=1
            INDEX 1=0
            [Session 2]
            INDEX 1=999
            """
        );

        Assert.Equal(0, disc.Tracks[0].Index01Lba);
    }

    [Fact]
    public void ParseEntrySectionResetsTrackContext()
    {
        var disc = Parse(
            """
            [TRACK 1]
            MODE=1
            INDEX 1=0
            [Entry 0]
            INDEX 1=999
            """
        );

        Assert.Equal(0, disc.Tracks[0].Index01Lba);
    }

    [Fact]
    public void ParseEmptyContentReturnsEmptyDisc()
    {
        var disc = Parse(string.Empty);

        Assert.Empty(disc.Tracks);
        Assert.Equal(0, disc.Version);
        Assert.Null(disc.Catalog);
    }

    [Fact]
    public void ParseIgnoresBlankLinesAndUnknownFields()
    {
        var disc = Parse(
            """

            [CloneCD]
            Version=3

            UnknownField=1

            [TRACK 1]
            MODE=1
            SOMETHING=else
            INDEX 1=42
            """
        );

        Assert.Equal(3, disc.Version);
        Assert.Equal(42, disc.Tracks[0].Index01Lba);
    }

    [Fact]
    public void ParseDuplicateIndexKeepsLastValue()
    {
        var disc = Parse(
            """
            [TRACK 1]
            MODE=1
            INDEX 1=10
            INDEX 1=20
            """
        );

        Assert.Equal(20, disc.Tracks[0].Index01Lba);
    }

    [Fact]
    public void ParseDataTracksScrambledOneIsTrue()
    {
        var disc = Parse(
            """
            [Disc]
            DataTracksScrambled=1
            """
        );

        Assert.True(disc.DataTracksScrambled);
    }

    [Fact]
    public void ParseFlagsAreTrimmed()
    {
        var disc = Parse(
            """
            [TRACK 1]
            MODE=1
            INDEX 1=0
            FLAGS=  DCP 4CH
            """
        );

        Assert.Equal("DCP 4CH", disc.Tracks[0].Flags);
    }

    [Fact]
    public void ParseIsrcIsTrimmed()
    {
        var disc = Parse(
            """
            [TRACK 1]
            MODE=1
            INDEX 1=0
            ISRC=  ABC123456789
            """
        );

        Assert.Equal("ABC123456789", disc.Tracks[0].Isrc);
    }

    [Fact]
    public void ParseFileThrowsFileNotFoundExceptionWhenMissing()
    {
        var missing = Path.Combine(_tempDir, "missing.ccd");

        Assert.Throws<FileNotFoundException>(() => CcdParser.Parse(missing));
    }

    [Fact]
    public void ParseFileReadsContentFromDisk()
    {
        var path = WriteCcd("game.ccd", FullCcd);

        var disc = CcdParser.Parse(path);

        Assert.Equal(2, disc.Tracks.Count);
        Assert.Equal(path, disc.FilePath);
    }

    [Fact]
    public void ParseFileResolvesImgAndSubPaths()
    {
        var path = WriteCcd("game.ccd", FullCcd);
        var imgPath = Path.Combine(_tempDir, "game.img");
        var subPath = Path.Combine(_tempDir, "game.sub");
        File.WriteAllBytes(imgPath, [1, 2, 3]);
        File.WriteAllBytes(subPath, [4, 5, 6]);

        var disc = CcdParser.Parse(path);

        Assert.Equal(imgPath, disc.ImgFilePath);
        Assert.Equal(subPath, disc.SubFilePath);
    }

    [Fact]
    public void ParseFileWithoutCompanionFilesLeavesPathsNull()
    {
        var path = WriteCcd("lonely.ccd", FullCcd);

        var disc = CcdParser.Parse(path);

        Assert.Null(disc.ImgFilePath);
        Assert.Null(disc.SubFilePath);
    }

    [Fact]
    public void ParseReaderWithoutPathLeavesFilePathsNull()
    {
        var disc = Parse(FullCcd);

        Assert.Null(disc.FilePath);
        Assert.Null(disc.ImgFilePath);
        Assert.Null(disc.SubFilePath);
    }

    [Fact]
    public void ParseReaderWithPathResolvesCompanions()
    {
        var path = WriteCcd("reader.ccd", FullCcd);
        File.WriteAllBytes(Path.Combine(_tempDir, "reader.img"), [1]);

        using var reader = new StringReader(FullCcd);
        var disc = CcdParser.Parse(reader, path);

        Assert.Equal(path, disc.FilePath);
        Assert.Equal(Path.Combine(_tempDir, "reader.img"), disc.ImgFilePath);
        Assert.Null(disc.SubFilePath);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(74, 0, 0, 74)]
    [InlineData(75, 0, 1, 0)]
    [InlineData(4499, 0, 59, 74)]
    [InlineData(4500, 1, 0, 0)]
    [InlineData(4501, 1, 0, 1)]
    [InlineData(12345, 2, 44, 45)]
    [InlineData(450000, 100, 0, 0)]
    public void LbaToMsfConvertsCorrectly(int lba, int minutes, int seconds, int frames)
    {
        var result = CcdParser.LbaToMsf(lba);

        Assert.Equal((minutes, seconds, frames), result);
    }

    [Theory]
    [InlineData(0, 0, 0, "00:00:00")]
    [InlineData(0, 0, 5, "00:00:05")]
    [InlineData(1, 2, 3, "01:02:03")]
    [InlineData(12, 34, 56, "12:34:56")]
    [InlineData(99, 59, 74, "99:59:74")]
    public void FormatMsfPadsAllComponents(int minutes, int seconds, int frames, string expected)
    {
        Assert.Equal(expected, CcdParser.FormatMsf(minutes, seconds, frames));
    }
}
