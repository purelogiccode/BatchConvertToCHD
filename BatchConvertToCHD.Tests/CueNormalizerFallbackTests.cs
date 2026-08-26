using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

/// <summary>
/// Covers the FILE line resolution fallbacks for cues whose recorded name does not match anything
/// on disk. Every case here is taken from a real conversion failure.
/// </summary>
public class CueNormalizerFallbackTests : IDisposable
{
    private readonly string _tempDir;

    public CueNormalizerFallbackTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"CueNormalizerFallbackTests_{Guid.NewGuid():N}"
        );
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

    private string WriteFile(string name, string content = "")
    {
        var path = Path.Combine(_tempDir, name);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);

        return path;
    }

    [Fact]
    public async Task ForeignAbsolutePathResolvesToTheFileBesideTheCue()
    {
        // Dragon Quest VII disc 2 shipped with a cue still pointing at the ripper's desktop.
        var cue = WriteFile(
            "DQ7 Disc 2.cue",
            "FILE \"C:\\DOCUMENTS AND SETTINGS\\BILL\\DESKTOP\\DQ7 Disc 2.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n"
        );
        var bin = WriteFile("DQ7 Disc 2.bin", "data");

        var result = await CueNormalizer.NormalizeAsync(cue, CancellationToken.None);

        Assert.Empty(result.UnresolvedNames);
        var reference = Assert.Single(result.References);
        Assert.True(reference.IsResolved);
        Assert.Equal(bin, reference.ResolvedFullPath);
        Assert.Equal("DQ7 Disc 2.bin", reference.ResolvedName);
        Assert.True(result.NeedsRewrite);
    }

    [Fact]
    public async Task ExtensionSwapResolvesWhenTheRipWasResaved()
    {
        // Mega Man X4's cue asked for "Mega Man X4 (USA).bin" beside a .img of the same base name.
        var cue = WriteFile(
            "Mega Man X4.cue",
            "FILE \"Mega Man X4 (USA).bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n"
        );
        var img = WriteFile("Mega Man X4 (USA).img", "data");

        var result = await CueNormalizer.NormalizeAsync(cue, CancellationToken.None);

        Assert.Empty(result.UnresolvedNames);
        Assert.Equal(img, Assert.Single(result.References).ResolvedFullPath);
        Assert.True(result.ReferencesChanged);
    }

    [Fact]
    public async Task SingleFileCueWithOneDataFileResolvesByElimination()
    {
        // "Legend Of Legaia Iso" and "SOULEDGE.bin" bear no relation to the file on disk, but a cue
        // with one FILE line next to exactly one image can only mean that image.
        var cue = WriteFile(
            "Legend of Legaia.cue",
            "FILE \"Legend Of Legaia Iso\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n"
        );
        var image = WriteFile("Legend of Legaia (USA).img", "data");

        var result = await CueNormalizer.NormalizeAsync(cue, CancellationToken.None);

        Assert.Empty(result.UnresolvedNames);
        Assert.Equal(image, Assert.Single(result.References).ResolvedFullPath);
    }

    [Fact]
    public async Task EliminationIsNotUsedWhenSeveralDataFilesCouldMatch()
    {
        // Ambiguity must stay unresolved rather than be guessed at.
        var cue = WriteFile(
            "Game.cue",
            "FILE \"Whatever.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n"
        );
        WriteFile("DiscOne.img", "a");
        WriteFile("DiscTwo.img", "b");

        var result = await CueNormalizer.NormalizeAsync(cue, CancellationToken.None);

        Assert.Equal(["Whatever.bin"], result.UnresolvedNames);
    }

    [Fact]
    public async Task EliminationIsNotUsedForMultiFileCues()
    {
        // A split-track cue with a genuinely missing bin must report it, not substitute another track.
        var cue = WriteFile(
            "Split.cue",
            "FILE \"Split (Track 01).bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n"
                + "FILE \"Split (Track 02).bin\" BINARY\r\n  TRACK 02 AUDIO\r\n    INDEX 01 00:00:00\r\n"
        );
        WriteFile("Split (Track 01).bin", "a");

        var result = await CueNormalizer.NormalizeAsync(cue, CancellationToken.None);

        Assert.Equal(["Split (Track 02).bin"], result.UnresolvedNames);
    }

    [Fact]
    public async Task MissingAudioTrackIsNeverAnsweredWithTheDiscImage()
    {
        // Elimination is restricted to the data track: a lost WAVE track must not silently resolve to
        // the disc image sitting next to it.
        var cue = WriteFile(
            "Audio.cue",
            "FILE \"missing-music.wav\" WAVE\r\n  TRACK 01 AUDIO\r\n    INDEX 01 00:00:00\r\n"
        );
        WriteFile("Audio.img", "data");

        var result = await CueNormalizer.NormalizeAsync(cue, CancellationToken.None);

        Assert.Equal(["missing-music.wav"], result.UnresolvedNames);
    }

    [Fact]
    public async Task ExactMatchStillWinsAndNeedsNoRewrite()
    {
        // The fallbacks must not disturb a cue that was already correct.
        var cue = WriteFile(
            "Good.cue",
            "FILE \"Good.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n"
        );
        var bin = WriteFile("Good.bin", "data");

        var result = await CueNormalizer.NormalizeAsync(cue, CancellationToken.None);

        var reference = Assert.Single(result.References);
        Assert.Equal(bin, reference.ResolvedFullPath);
        Assert.False(reference.WasNameCorrected);
        Assert.False(result.ReferencesChanged);
    }

    [Fact]
    public async Task ReferenceInASubdirectoryResolvesToTheRealPath()
    {
        // ResolvedFullPath must not repeat the subdirectory the reference already carried.
        var cue = WriteFile(
            "Sub.cue",
            "FILE \"data/Sub.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n"
        );
        var bin = WriteFile(Path.Combine("data", "Sub.bin"), "data");

        var result = await CueNormalizer.NormalizeAsync(cue, CancellationToken.None);

        Assert.Empty(result.UnresolvedNames);
        Assert.Equal(bin, Assert.Single(result.References).ResolvedFullPath);
    }

    [Fact]
    public async Task UnresolvableReferenceIsStillReported()
    {
        var cue = WriteFile(
            "Empty.cue",
            "FILE \"nothing-here.bin\" BINARY\r\n  TRACK 01 MODE2/2352\r\n    INDEX 01 00:00:00\r\n"
        );

        var result = await CueNormalizer.NormalizeAsync(cue, CancellationToken.None);

        Assert.Equal(["nothing-here.bin"], result.UnresolvedNames);
        Assert.False(Assert.Single(result.References).IsResolved);
    }
}
