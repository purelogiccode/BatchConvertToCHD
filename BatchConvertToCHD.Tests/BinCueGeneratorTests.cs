using BatchConvertToCHD.Utilities;

namespace BatchConvertToCHD.Tests;

public class BinCueGeneratorTests : IDisposable
{
    private readonly string _tempDir;

    public BinCueGeneratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BinCueGeneratorTests_{Guid.NewGuid():N}");
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
    public void GetAutoCuePathEmbedsMarkerNextToBin()
    {
        var binPath = Path.Combine(_tempDir, "Game (USA).bin");

        var cuePath = BinCueGenerator.GetAutoCuePath(binPath);

        Assert.Equal(Path.Combine(_tempDir, "Game (USA).autocue.cue"), cuePath);
        Assert.True(BinCueGenerator.IsAutoCue(cuePath));
        Assert.False(BinCueGenerator.IsAutoCue(Path.Combine(_tempDir, "Game (USA).cue")));
    }

    [Fact]
    public void BuildCueContentUsesQuotedFileLineAndRequestedMode()
    {
        var content = BinCueGenerator.BuildCueContent("Game.bin", BinCueGenerator.Mode2);

        Assert.Contains("FILE \"Game.bin\" BINARY", content, StringComparison.Ordinal);
        Assert.Contains("TRACK 01 MODE2/2352", content, StringComparison.Ordinal);
    }

    [Fact]
    public void GetAlternateModeSwitchesBetweenMode1AndMode2()
    {
        Assert.Equal(
            BinCueGenerator.Mode1,
            BinCueGenerator.GetAlternateMode(BinCueGenerator.Mode2)
        );
        Assert.Equal(
            BinCueGenerator.Mode2,
            BinCueGenerator.GetAlternateMode(BinCueGenerator.Mode1)
        );
    }

    [Fact]
    public async Task ReadTrackModeAsyncReadsModeFromGeneratedCue()
    {
        var binPath = Path.Combine(_tempDir, "Game.bin");
        var cuePath = BinCueGenerator.GetAutoCuePath(binPath);
        await File.WriteAllTextAsync(
            cuePath,
            BinCueGenerator.BuildCueContent("Game.bin", BinCueGenerator.Mode2)
        );

        var mode = await BinCueGenerator.ReadTrackModeAsync(cuePath, CancellationToken.None);

        Assert.Equal(BinCueGenerator.Mode2, mode);
    }

    [Fact]
    public async Task RewriteCueAsyncSwitchesModeAndKeepsReferencedBinName()
    {
        var binPath = Path.Combine(_tempDir, "Game.bin");
        var cuePath = BinCueGenerator.GetAutoCuePath(binPath);
        await File.WriteAllTextAsync(
            cuePath,
            BinCueGenerator.BuildCueContent("Game.bin", BinCueGenerator.Mode2)
        );

        await BinCueGenerator.RewriteCueAsync(
            cuePath,
            BinCueGenerator.Mode1,
            CancellationToken.None
        );

        var content = await File.ReadAllTextAsync(cuePath);
        Assert.Contains("FILE \"Game.bin\" BINARY", content, StringComparison.Ordinal);
        Assert.Contains("TRACK 01 MODE1/2352", content, StringComparison.Ordinal);
        Assert.DoesNotContain("MODE2", content, StringComparison.Ordinal);
    }
}