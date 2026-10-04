using CHDStudio.Utilities;
using CHDSharp.Encoder.Models;
using CHDSharp.Models;

namespace CHDStudio.Tests;

/// <summary>
///     Tests for the ten-percent-step progress logger used by the built-in CHDSharp conversion,
///     verification and extraction paths.
/// </summary>
public class ChdSharpProgressLoggerTests
{
    [Fact]
    public void Report_LogsOncePerTenPercentStep()
    {
        var messages = new List<string>();
        var logger = new ChdSharpProgressLogger(messages.Add, "Verifying");

        for (var processed = 0; processed <= 100; processed++)
        {
            logger.Report(
                new ChdProgress(processed, 100, processed, 100, TimeSpan.Zero)
            );
        }

        Assert.Equal(
            ["10", "20", "30", "40", "50", "60", "70", "80", "90", "100"],
            messages.Select(ExtractPercent),
            StringComparer.Ordinal
        );
        Assert.All(messages, m => Assert.StartsWith(" CHDSHARP: Verifying, ", m, StringComparison.Ordinal));
    }

    [Fact]
    public void Report_RepeatedPercentWithinStepIsLoggedOnce()
    {
        var messages = new List<string>();
        var logger = new ChdSharpProgressLogger(messages.Add, "Extracting");

        logger.ReportBytes(100, 1000);
        logger.ReportBytes(150, 1000);
        logger.ReportBytes(200, 1000);

        Assert.Equal(["10", "20"], messages.Select(ExtractPercent), StringComparer.Ordinal);
    }

    [Fact]
    public void ReportBytes_ZeroTotalReportsHundred()
    {
        var messages = new List<string>();
        var logger = new ChdSharpProgressLogger(messages.Add, "Extracting");

        logger.ReportBytes(0, 0);

        Assert.Single(messages);
        Assert.Contains("100% complete", messages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ReportBytes_NewRegionRestartsMilestones()
    {
        var messages = new List<string>();
        var logger = new ChdSharpProgressLogger(messages.Add, "Hashing");

        logger.ReportBytes(100, 100);
        logger.ReportBytes(10, 100);
        logger.ReportBytes(50, 100);
        logger.ReportBytes(100, 100);

        Assert.Equal(["100", "10", "50", "100"], messages.Select(ExtractPercent), StringComparer.Ordinal);
    }

    [Fact]
    public void ReportBytes_IncludesByteCounts()
    {
        var messages = new List<string>();
        var logger = new ChdSharpProgressLogger(messages.Add, "Verifying");

        logger.ReportBytes(1_000_000, 10_000_000);

        Assert.Single(messages);
        Assert.Contains("(1,000,000 / 10,000,000 bytes)", messages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ReportHunk_LogsCumulativeRatioAtSteps()
    {
        var messages = new List<string>();
        var logger = new ChdSharpProgressLogger(messages.Add, "Compressing");

        for (uint index = 0; index < 10; index++)
        {
            logger.ReportHunk(new HunkProgress(index, 10, 100, 50, 0, "zlib", 0.5));
        }

        Assert.Equal(10, messages.Count);
        Assert.Contains("Compressing, 100% complete", messages[^1], StringComparison.Ordinal);
        Assert.Contains("ratio=50.0%", messages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void ReportHunk_ZeroHunkCountReportsHundred()
    {
        var messages = new List<string>();
        var logger = new ChdSharpProgressLogger(messages.Add, "Compressing");

        logger.ReportHunk(new HunkProgress(0, 0, 100, 50, 0, "zlib", 0.5));

        Assert.Single(messages);
        Assert.Contains("100% complete", messages[0], StringComparison.Ordinal);
    }

    private static string ExtractPercent(string message)
    {
        var start = message.IndexOf(", ", StringComparison.Ordinal) + 2;
        var end = message.IndexOf('%');
        return message[start..end];
    }
}
