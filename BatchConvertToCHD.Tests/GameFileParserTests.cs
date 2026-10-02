using System.Text;
using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class GameFileParserTests : IDisposable
{
    private readonly string _tempDir;

    static GameFileParserTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public GameFileParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"GameFileParserTests_{Guid.NewGuid():N}");
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

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncQuotedFileReturnsReferencedFiles()
    {
        var cuePath = Path.Combine(_tempDir, "game.cue");
        const string content =
            "FILE \"track1.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00";
        await File.WriteAllTextAsync(cuePath, content);

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, "track1.bin"), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncUnquotedFileReturnsReferencedFiles()
    {
        var cuePath = Path.Combine(_tempDir, "game.cue");
        const string content =
            "FILE track1.bin BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00";
        await File.WriteAllTextAsync(cuePath, content);

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, "track1.bin"), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncMissingFileReturnsEmptyList()
    {
        var cuePath = Path.Combine(_tempDir, "missing.cue");
        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReferencedFilesFromGdiAsyncQuotedFileReturnsReferencedFiles()
    {
        var gdiPath = Path.Combine(_tempDir, "game.gdi");
        const string content =
            "3\n1 0 4 2352 track01.bin 0\n2 45000 4 2352 \"track 02.raw\" 0\n3 50000 4 2352 track03.bin 0";
        await File.WriteAllTextAsync(gdiPath, content, Encoding.UTF8);

        var result = await GameFileParser.GetReferencedFilesFromGdiAsync(
            gdiPath,
            static _ => { },
            CancellationToken.None
        );

        Assert.Equal(3, result.Count);
        Assert.Equal(Path.Combine(_tempDir, "track01.bin"), result[0]);
        Assert.Equal(Path.Combine(_tempDir, "track 02.raw"), result[1]);
        Assert.Equal(Path.Combine(_tempDir, "track03.bin"), result[2]);
    }

    [Fact]
    public async Task GetReferencedFilesFromGdiAsyncSpacedFilenameReturnsReferencedFiles()
    {
        var gdiPath = Path.Combine(_tempDir, "game.gdi");
        const string content = "2\n1 0 4 2352 track one.bin 0\n2 45000 4 2352 track two.bin 0";
        await File.WriteAllTextAsync(gdiPath, content, Encoding.UTF8);

        var result = await GameFileParser.GetReferencedFilesFromGdiAsync(
            gdiPath,
            static _ => { },
            CancellationToken.None
        );

        Assert.Equal(2, result.Count);
        Assert.Equal(Path.Combine(_tempDir, "track one.bin"), result[0]);
        Assert.Equal(Path.Combine(_tempDir, "track two.bin"), result[1]);
    }

    [Fact]
    public async Task GetReferencedFilesFromGdiAsyncMissingFileReturnsEmptyList()
    {
        var gdiPath = Path.Combine(_tempDir, "missing.gdi");
        var result = await GameFileParser.GetReferencedFilesFromGdiAsync(
            gdiPath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReferencedFilesFromTocAsyncQuotedFileReturnsReferencedFiles()
    {
        var tocPath = Path.Combine(_tempDir, "game.toc");
        const string content =
            "FILE \"track1.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00";
        await File.WriteAllTextAsync(tocPath, content);

        var result = await GameFileParser.GetReferencedFilesFromTocAsync(
            tocPath,
            static _ => { },
            CancellationToken.None
        );

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, "track1.bin"), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromTocAsyncMissingFileReturnsEmptyList()
    {
        var tocPath = Path.Combine(_tempDir, "missing.toc");
        var result = await GameFileParser.GetReferencedFilesFromTocAsync(
            tocPath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncEmptyFileReturnsNoFiles()
    {
        var cuePath = Path.Combine(_tempDir, "empty.cue");
        await File.WriteAllTextAsync(cuePath, string.Empty);

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncNoFileLinesReturnsNoFiles()
    {
        var cuePath = Path.Combine(_tempDir, "game.cue");
        const string content =
            "TITLE \"Game\"\nPERFORMER \"Artist\"\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00";
        await File.WriteAllTextAsync(cuePath, content);

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncMultipleFileLinesReturnsAll()
    {
        var cuePath = Path.Combine(_tempDir, "game.cue");
        const string content =
            "FILE \"track1.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\nFILE \"track2.bin\" WAVE\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00";
        await File.WriteAllTextAsync(cuePath, content);

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Equal(2, result.Count);
        Assert.Contains(Path.Combine(_tempDir, "track1.bin"), result, StringComparer.Ordinal);
        Assert.Contains(Path.Combine(_tempDir, "track2.bin"), result, StringComparer.Ordinal);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncFileLineWithoutTypeSkips()
    {
        var cuePath = Path.Combine(_tempDir, "game.cue");
        const string content = "FILE ";
        await File.WriteAllTextAsync(cuePath, content);

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncUnquotedMp3Format()
    {
        var cuePath = Path.Combine(_tempDir, "game.cue");
        const string content = "FILE track1.mp3 MP3\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00";
        await File.WriteAllTextAsync(cuePath, content);

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, "track1.mp3"), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromGdiAsyncInvalidHeaderLineHandled()
    {
        var gdiPath = Path.Combine(_tempDir, "game.gdi");
        const string content = "invalid\nnot enough fields";
        await File.WriteAllTextAsync(gdiPath, content, Encoding.UTF8);

        var result = await GameFileParser.GetReferencedFilesFromGdiAsync(
            gdiPath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReferencedFilesFromGdiAsyncSkipLessThan5Parts()
    {
        var gdiPath = Path.Combine(_tempDir, "game.gdi");
        const string content = "2\n1 0 4\n2 45000 4 2352 track2.bin 0";
        await File.WriteAllTextAsync(gdiPath, content, Encoding.UTF8);

        var result = await GameFileParser.GetReferencedFilesFromGdiAsync(
            gdiPath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, "track2.bin"), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromGdiAsyncEmptyFileReturnsNoFiles()
    {
        var gdiPath = Path.Combine(_tempDir, "game.gdi");
        await File.WriteAllTextAsync(gdiPath, string.Empty, Encoding.UTF8);

        var result = await GameFileParser.GetReferencedFilesFromGdiAsync(
            gdiPath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReferencedFilesFromTocAsyncEmptyFileReturnsNoFiles()
    {
        var tocPath = Path.Combine(_tempDir, "empty.toc");
        await File.WriteAllTextAsync(tocPath, string.Empty);

        var result = await GameFileParser.GetReferencedFilesFromTocAsync(
            tocPath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReferencedFilesFromTocAsyncNoFileLinesReturnsNoFiles()
    {
        var tocPath = Path.Combine(_tempDir, "game.toc");
        const string content = "CATALOG \"12345\"\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00";
        await File.WriteAllTextAsync(tocPath, content);

        var result = await GameFileParser.GetReferencedFilesFromTocAsync(
            tocPath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReferencedFilesFromTocAsyncFileLineWithoutTypeSkips()
    {
        var tocPath = Path.Combine(_tempDir, "game.toc");
        const string content = "FILE ";
        await File.WriteAllTextAsync(tocPath, content);

        var result = await GameFileParser.GetReferencedFilesFromTocAsync(
            tocPath,
            static _ => { },
            CancellationToken.None
        );
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReferencedFilesFromTocAsyncUnquotedFileReturnsReferencedFiles()
    {
        var tocPath = Path.Combine(_tempDir, "game.toc");
        const string content =
            "FILE track1.bin BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00";
        await File.WriteAllTextAsync(tocPath, content);

        var result = await GameFileParser.GetReferencedFilesFromTocAsync(
            tocPath,
            static _ => { },
            CancellationToken.None
        );

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, "track1.bin"), result[0]);
    }

    [Fact]
    public async Task ReadLinesWithDetectedEncodingAsyncReportsBomPresence()
    {
        const string content =
            "FILE \"track1.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00";

        // UTF-8 with BOM (what many Windows-authored cue files have; chdman chokes on the BOM).
        var bomPath = Path.Combine(_tempDir, "bom.cue");
        await File.WriteAllTextAsync(bomPath, content, Encoding.UTF8);

        var (bomLines, bomEncoding, hasBom) =
            await GameFileParser.ReadLinesWithDetectedEncodingAsync(
                bomPath,
                CancellationToken.None
            );
        Assert.True(hasBom);
        Assert.Equal("utf-8", bomEncoding.WebName, true);
        Assert.Contains(
            bomLines,
            static l => l.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase)
        );

        // UTF-8 without BOM.
        var plainPath = Path.Combine(_tempDir, "plain.cue");
        await File.WriteAllTextAsync(plainPath, content, new UTF8Encoding(false));

        var (_, plainEncoding, plainHasBom) =
            await GameFileParser.ReadLinesWithDetectedEncodingAsync(
                plainPath,
                CancellationToken.None
            );
        Assert.False(plainHasBom);
        Assert.Equal("utf-8", plainEncoding.WebName, true);

        // Legacy code page (Shift-JIS) — no BOM.
        var sjisPath = Path.Combine(_tempDir, "sjis.cue");
        await File.WriteAllTextAsync(
            sjisPath,
            "FILE \"日本語.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00",
            Encoding.GetEncoding(932)
        );

        var (_, sjisEncoding, sjisHasBom) = await GameFileParser.ReadLinesWithDetectedEncodingAsync(
            sjisPath,
            CancellationToken.None
        );
        Assert.False(sjisHasBom);
        Assert.Equal(932, sjisEncoding.CodePage);
    }
}