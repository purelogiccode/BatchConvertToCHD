using BatchConvertToCHD.Models;

namespace BatchConvertToCHD.Tests;

public class PbpExtractionResultTests
{
    [Fact]
    public void DefaultValuesAreCorrect()
    {
        var result = new PbpExtractionResult();
        Assert.False(result.Success);
        Assert.NotNull(result.CueFilePaths);
        Assert.Empty(result.CueFilePaths);
        Assert.Null(result.OutputFolder);
    }

    [Fact]
    public void SuccessPropertyCanBeSet()
    {
        var result = new PbpExtractionResult { Success = true };
        Assert.True(result.Success);
    }

    [Fact]
    public void CueFilePathsCanBeSet()
    {
        var files = new List<string> { "file1.cue", "file2.cue" };
        var result = new PbpExtractionResult { CueFilePaths = files };
        Assert.Equal(2, result.CueFilePaths.Count);
        Assert.Contains("file1.cue", result.CueFilePaths, StringComparer.Ordinal);
        Assert.Contains("file2.cue", result.CueFilePaths, StringComparer.Ordinal);
    }

    [Fact]
    public void OutputFolderCanBeSet()
    {
        var result = new PbpExtractionResult { OutputFolder = @"C:\output" };
        Assert.Equal(@"C:\output", result.OutputFolder);
    }

    [Fact]
    public void AllPropertiesCanBeSetTogether()
    {
        var result = new PbpExtractionResult
        {
            Success = true,
            CueFilePaths = ["game.cue"],
            OutputFolder = @"C:\extracted",
        };
        Assert.True(result.Success);
        Assert.Single(result.CueFilePaths);
        Assert.Equal("game.cue", result.CueFilePaths[0]);
        Assert.Equal(@"C:\extracted", result.OutputFolder);
    }
}
