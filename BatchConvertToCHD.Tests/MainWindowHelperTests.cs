namespace BatchConvertToCHD.Tests;

/// <summary>
/// Tests for MainWindow helper methods used by the conversion pipeline.
/// </summary>
public class MainWindowHelperTests : IDisposable
{
    private readonly string _tempDir;

    public MainWindowHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"MainWindowHelperTests_{Guid.NewGuid():N}");
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

    [Fact]
    public async Task StripUtf8BomIfPresentAsync_RemovesBom()
    {
        var path = Path.Combine(_tempDir, "bom.cue");
        await File.WriteAllTextAsync(path, "FILE \"track1.bin\" BINARY", System.Text.Encoding.UTF8);

        Assert.Equal(0xEF, (await File.ReadAllBytesAsync(path))[0]);

        await MainWindow.StripUtf8BomIfPresentAsync(path, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..], "BOM must be removed");
        Assert.StartsWith(
            "FILE \"track1.bin\" BINARY",
            System.Text.Encoding.UTF8.GetString(bytes),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task StripUtf8BomIfPresentAsync_LeavesBomFreeFileUntouched()
    {
        var path = Path.Combine(_tempDir, "plain.cue");
        const string content = "FILE \"track1.bin\" BINARY";
        await File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(false));

        await MainWindow.StripUtf8BomIfPresentAsync(path, CancellationToken.None);

        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task StripUtf8BomIfPresentAsync_MissingFileIsIgnored()
    {
        // Best-effort helper: must not throw for a missing file.
        await MainWindow.StripUtf8BomIfPresentAsync(
            Path.Combine(_tempDir, "nope.cue"),
            CancellationToken.None
        );
    }

    [Fact]
    public void SelectChdmanErrorLine_SkipsProgressLinesAndReturnsLastRealError()
    {
        const string errorText =
            "Compressing, 0.0% complete... (ratio=100.0%)\r\n"
            + "Output bytes: 1234\r\n"
            + "ERROR: couldn't find bin file [track1.bin]";

        var line = MainWindow.SelectChdmanErrorLine(errorText);

        Assert.Equal("ERROR: couldn't find bin file [track1.bin]", line);
    }

    [Fact]
    public void SelectChdmanErrorLine_ProgressOnly_ReturnsLastLine()
    {
        const string errorText =
            "Compressing, 10.0% complete... (ratio=95.0%)\n" + "Converting, 20.0% complete...";

        var line = MainWindow.SelectChdmanErrorLine(errorText);

        Assert.Equal("Converting, 20.0% complete...", line);
    }

    [Fact]
    public void SelectChdmanErrorLine_SingleErrorLineIsReturned()
    {
        var line = MainWindow.SelectChdmanErrorLine(
            "Unit size must be specified if no output parent CHD is supplied"
        );

        Assert.Equal("Unit size must be specified if no output parent CHD is supplied", line);
    }

    [Fact]
    public void SelectChdmanErrorLine_EmptyInputReturnsEmpty()
    {
        Assert.Equal(string.Empty, MainWindow.SelectChdmanErrorLine(string.Empty));
        Assert.Equal(string.Empty, MainWindow.SelectChdmanErrorLine(" \r\n \n "));
    }

    [Fact]
    public void SelectChdmanErrorLine_SkipsFatalErrorSummaryAndReturnsRealCause()
    {
        // chdman prints the actual cause before its "Fatal error occurred: N" exit summary.
        const string errorText =
            "Compressing, 0.0% complete... (ratio=100.0%)\r\n"
            + "ERROR: Input file is not a valid CD image\r\n"
            + "Fatal error occurred: 1";

        var line = MainWindow.SelectChdmanErrorLine(errorText);

        Assert.Equal("ERROR: Input file is not a valid CD image", line);
    }

    [Fact]
    public void SelectChdmanErrorLine_FatalSummaryOnly_ReturnsDescriptiveMessage()
    {
        const string errorText =
            "Compressing, 0.0% complete... (ratio=100.0%)\n" + "Fatal error occurred: 1";

        var line = MainWindow.SelectChdmanErrorLine(errorText);

        Assert.Equal(
            "chdman encountered an error. The file may be corrupted, in an unsupported format, or a required codec may be missing.",
            line
        );
    }

    [Fact]
    public void SelectChdmanErrorLine_DoesNotSkipUnhandledExceptionLine()
    {
        // chdman C++ runtime crash lines are real causes and must be kept.
        const string errorText =
            "Unhandled exception: cannot create std::vector larger than max_size()";

        var line = MainWindow.SelectChdmanErrorLine(errorText);

        Assert.Equal("Unhandled exception: cannot create std::vector larger than max_size()", line);
    }

    [Fact]
    public void GetChdExtractionErrorMessage_DecompressionFailure_AddsGuidance()
    {
        var message = MainWindow.GetChdExtractionErrorMessage(
            "Failed to read hunk 0: Chderrdecompressionerror"
        );

        Assert.Contains("A/V (laserdisc)", message, StringComparison.Ordinal);
        Assert.Contains("Retrying with chdman", message, StringComparison.Ordinal);
        Assert.StartsWith(
            "Failed to read hunk 0: Chderrdecompressionerror",
            message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void GetChdExtractionErrorMessage_OtherFailures_AreUnchanged()
    {
        const string message = "No files extracted.";

        Assert.Equal(message, MainWindow.GetChdExtractionErrorMessage(message));
    }

    [Fact]
    public void BuildChdmanExtractArgs_ExtractCd_PinsBinAndForces()
    {
        var args = MainWindow.BuildChdmanExtractArgs(
            "extractcd",
            @"D:\roms\game.chd",
            @"D:\out\game.cue"
        );

        Assert.Contains("extractcd -i \"D:\\roms\\game.chd\"", args, StringComparison.Ordinal);
        Assert.Contains("-o \"D:\\out\\game.cue\"", args, StringComparison.Ordinal);
        Assert.Contains("-ob \"D:\\out\\game.bin\"", args, StringComparison.Ordinal);
        Assert.EndsWith("-f", args, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("extractdvd", ".iso")]
    [InlineData("extracthd", ".img")]
    [InlineData("extractld", ".avi")]
    [InlineData("extractraw", ".raw")]
    public void BuildChdmanExtractArgs_OtherCommands_InputOutputForce(string command, string ext)
    {
        var output = $@"D:\out\game{ext}";
        var args = MainWindow.BuildChdmanExtractArgs(command, @"D:\roms\game.chd", output);

        Assert.Contains($"{command} -i \"D:\\roms\\game.chd\"", args, StringComparison.Ordinal);
        Assert.Contains($"-o \"{output}\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-ob", args, StringComparison.Ordinal);
        Assert.EndsWith("-f", args, StringComparison.Ordinal);
    }
}
