using System.Text;
using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class CueNormalizerTests : IDisposable
{
    private readonly string _tempDir;

    static CueNormalizerTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public CueNormalizerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CueNormalizerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            /* ignore */
        }

        GC.SuppressFinalize(this);
    }

    private string CreateFile(string name, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content, encoding ?? Encoding.UTF8);
        return path;
    }

    // ---- Encoding detection (GameFileParser) ----

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncKoreanCp949CueResolvesKoreanBin()
    {
        const string koreanName = "진설 사무라이 스피리츠 무사도열전.bin";
        CreateFile(koreanName, "dummy");
        var cuePath = CreateFile(
            "game.cue",
            $"FILE \"{koreanName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            Encoding.GetEncoding(949)
        );

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, koreanName), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncRussianCp1251CueResolvesCyrillicBin()
    {
        const string russianName = "Буря в пустыне-Vermilion Desert.bin";
        CreateFile(russianName, "dummy");
        var cuePath = CreateFile(
            "game.cue",
            $"FILE \"{russianName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            Encoding.GetEncoding(1251)
        );

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, russianName), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncJapaneseCp932CueResolvesJapaneseBin()
    {
        const string japaneseName = "バトルアスリーテス.bin";
        CreateFile(japaneseName, "dummy");
        var cuePath = CreateFile(
            "game.cue",
            $"FILE \"{japaneseName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            Encoding.GetEncoding(932)
        );

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, japaneseName), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncUtf8NoBomJapaneseCueResolves()
    {
        const string japaneseName = "バトルアスリーテス.bin";
        CreateFile(japaneseName, "dummy");
        var cuePath = CreateFile(
            "game.cue",
            $"FILE \"{japaneseName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            new UTF8Encoding(false)
        );

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, japaneseName), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncLatin1CueWithSjsValidBytesResolvesViaFilesystem()
    {
        // The 'é' (0xE9) followed by 'm' (0x6D) forms a *valid* Shift-JIS pair, so both CP1252
        // and CP932 decode this cue with zero replacement characters. The filesystem-resolution
        // scoring must pick CP1252 because only that decoding matches the file on disk.
        const string latinName = "Pokémon - Red.bin";
        CreateFile(latinName, "dummy");
        var cuePath = CreateFile(
            "game.cue",
            $"FILE \"{latinName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            Encoding.GetEncoding(1252)
        );

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, latinName), result[0]);
    }

    [Fact]
    public async Task GetReferencedFilesFromCueAsyncUtf32LeBomCueResolves()
    {
        const string name = "game.bin";
        CreateFile(name, "dummy");
        var cuePath = Path.Combine(_tempDir, "utf32.cue");
        const string content =
            $"FILE \"{name}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00";
        var bytes = new UTF32Encoding(false, true)
            .GetPreamble()
            .Concat(new UTF32Encoding(false, false).GetBytes(content))
            .ToArray();
        await File.WriteAllBytesAsync(cuePath, bytes);

        var result = await GameFileParser.GetReferencedFilesFromCueAsync(
            cuePath,
            static _ => { },
            CancellationToken.None
        );

        Assert.Single(result);
        Assert.Equal(Path.Combine(_tempDir, name), result[0]);
    }

    // ---- CueNormalizer: resolution ----

    [Fact]
    public async Task NormalizeAsyncCanonicalCueNeedsNoRewrite()
    {
        CreateFile("track1.bin", "dummy");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"track1.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00"
        );

        var result = await CueNormalizer.NormalizeAsync(cuePath, CancellationToken.None);

        Assert.False(result.NeedsRewrite);
        Assert.Empty(result.UnresolvedNames);
        Assert.Single(result.References);
        Assert.True(result.References[0].IsResolved);
        Assert.Equal("track1.bin", result.References[0].ResolvedName);
    }

    [Fact]
    public async Task NormalizeAsyncUnquotedFileLineIsRewrittenQuoted()
    {
        CreateFile("track1.bin", "dummy");
        var cuePath = CreateFile(
            "game.cue",
            "FILE track1.bin BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00"
        );

        var result = await CueNormalizer.NormalizeAsync(cuePath, CancellationToken.None);

        Assert.True(result.NeedsRewrite);
        Assert.Contains(
            "FILE \"track1.bin\" BINARY",
            result.CanonicalCueText,
            StringComparison.Ordinal
        );
        Assert.Contains("    INDEX 01 00:00:00", result.CanonicalCueText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalizeAsyncCaseMismatchResolvesToOnDiskName()
    {
        CreateFile("track1.bin", "dummy");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"TRACK1.BIN\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00"
        );

        var result = await CueNormalizer.NormalizeAsync(cuePath, CancellationToken.None);

        Assert.True(result.NeedsRewrite);
        Assert.True(result.References[0].IsResolved);
        Assert.Equal("track1.bin", result.References[0].ResolvedName);
        Assert.Contains(
            "FILE \"track1.bin\" BINARY",
            result.CanonicalCueText,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task NormalizeAsyncZeroPaddingMismatchResolvesToOnDiskName()
    {
        CreateFile("Game (Track 2).bin", "dummy");
        CreateFile("Game (Track 1).bin", "dummy");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"Game (Track 02).bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\nFILE \"Game (Track 01).bin\" BINARY\r\n  TRACK 02 AUDIO\r\n    INDEX 01 00:00:00"
        );

        var result = await CueNormalizer.NormalizeAsync(cuePath, CancellationToken.None);

        Assert.Equal(2, result.References.Count);
        Assert.Equal("Game (Track 2).bin", result.References[0].ResolvedName);
        Assert.Equal("Game (Track 1).bin", result.References[1].ResolvedName);
        Assert.Contains(
            "FILE \"Game (Track 2).bin\" BINARY",
            result.CanonicalCueText,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "FILE \"Game (Track 1).bin\" BINARY",
            result.CanonicalCueText,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task NormalizeAsyncMissingReferenceIsReportedUnresolved()
    {
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"missing.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00"
        );

        var result = await CueNormalizer.NormalizeAsync(cuePath, CancellationToken.None);

        Assert.Single(result.UnresolvedNames);
        Assert.Equal("missing.bin", result.UnresolvedNames[0]);
        Assert.False(result.References[0].IsResolved);
    }

    [Fact]
    public async Task NormalizeAsyncKoreanCp949CueResolvesAndRewrites()
    {
        const string koreanName = "진설 사무라이 스피리츠 무사도열전.bin";
        CreateFile(koreanName, "dummy");
        var cuePath = CreateFile(
            "game.cue",
            $"FILE \"{koreanName}\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00",
            Encoding.GetEncoding(949)
        );

        var result = await CueNormalizer.NormalizeAsync(cuePath, CancellationToken.None);

        Assert.Equal(Encoding.GetEncoding(949).CodePage, result.SourceEncoding.CodePage);
        Assert.True(result.References[0].IsResolved);
        Assert.Equal(koreanName, result.References[0].ResolvedName);
        Assert.Contains(
            $"FILE \"{koreanName}\" BINARY",
            result.CanonicalCueText,
            StringComparison.Ordinal
        );
    }

    // ---- CueNormalizer: transform hook (MP3 phase) ----

    [Fact]
    public async Task NormalizeAsyncTransformRewritesFileLineAndType()
    {
        CreateFile("track1.mp3", "dummy");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"track1.mp3\" MP3\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00"
        );

        var result = await CueNormalizer.NormalizeAsync(
            cuePath,
            CancellationToken.None,
            static r =>
                string.Equals(r.TrackType, "MP3", StringComparison.Ordinal)
                    ? ("track01.wav", "WAVE")
                    : null
        );

        Assert.True(result.NeedsRewrite);
        Assert.Contains(
            "FILE \"track01.wav\" WAVE",
            result.CanonicalCueText,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("track1.mp3", result.CanonicalCueText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalizeAsyncTransformNullKeepsOriginalReference()
    {
        CreateFile("track1.bin", "dummy");
        var cuePath = CreateFile(
            "game.cue",
            "FILE \"track1.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00"
        );

        var result = await CueNormalizer.NormalizeAsync(
            cuePath,
            CancellationToken.None,
            static r =>
                string.Equals(r.TrackType, "MP3", StringComparison.Ordinal)
                    ? ("track01.wav", "WAVE")
                    : null
        );

        Assert.False(result.NeedsRewrite);
        Assert.Equal("track1.bin", result.References[0].ResolvedName);
    }

    // ---- CueNormalizer: writing ----

    [Fact]
    public async Task WriteCanonicalCueAsyncWritesUtf8CrLfContent()
    {
        CreateFile("track1.bin", "dummy");
        var cuePath = CreateFile(
            "game.cue",
            "FILE track1.bin BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00"
        );
        var result = await CueNormalizer.NormalizeAsync(cuePath, CancellationToken.None);
        var outputPath = Path.Combine(_tempDir, "normalized.cue");

        await CueNormalizer.WriteCanonicalCueAsync(outputPath, result, CancellationToken.None);

        var written = await File.ReadAllTextAsync(outputPath, Encoding.UTF8);
        Assert.Contains(
            "FILE \"track1.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n",
            written,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "\n",
            written.Replace("\r\n", string.Empty),
            StringComparison.Ordinal
        );
    }
}