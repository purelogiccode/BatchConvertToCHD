using System.Runtime.InteropServices;

namespace BatchConvertToCHD.Tests;

public class AppConfigTests
{
    [Fact]
    public void IsArm64ReturnsBool()
    {
        var result = AppConfig.IsArm64;
        var expected = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ChdmanExeNameMatchesArchitecture()
    {
        if (AppConfig.IsArm64)
        {
            Assert.Equal("chdman_arm64.exe", AppConfig.ChdmanExeName);
        }
        else
        {
            Assert.Equal("chdman.exe", AppConfig.ChdmanExeName);
        }
    }

    [Fact]
    public void BugReportApiUrlIsNotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(AppConfig.BugReportApiUrl));
    }

    [Fact]
    public void BugReportApiKeyIsNotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(AppConfig.BugReportApiKey));
    }

    [Fact]
    public void ApplicationStatsApiUrlIsNotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(AppConfig.ApplicationStatsApiUrl));
    }

    [Fact]
    public void ApplicationStatsApiKeyIsNotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(AppConfig.ApplicationStatsApiKey));
    }

    [Fact]
    public void ApplicationNameIsCorrect()
    {
        Assert.Equal("BatchConvertToCHD", AppConfig.ApplicationName);
    }

    [Fact]
    public void WriteSpeedUpdateIntervalMsIsPositive()
    {
        Assert.True(AppConfig.WriteSpeedUpdateIntervalMs > 0);
    }

    [Fact]
    public void MaxConversionTimeoutHoursIsPositive()
    {
        Assert.True(AppConfig.MaxConversionTimeoutHours > 0);
    }

    [Fact]
    public void GitHubApiLatestReleaseUrlsPointToPureLogicCodeRepository()
    {
        var sources = AppConfig.GitHubApiLatestReleaseUrls;

        // The ownership transfer to the purelogiccode organization completed, so update checks
        // target that repository only.
        var source = Assert.Single(sources);
        Assert.Equal(AppConfig.PrimaryGitHubApiLatestReleaseUrl, source);
        Assert.Contains(
            "/purelogiccode/BatchConvertToCHD/",
            source,
            StringComparison.Ordinal
        );
        Assert.EndsWith(
            "/releases/latest",
            source,
            StringComparison.Ordinal
        );
        Assert.StartsWith("https://api.github.com/repos/", source, StringComparison.Ordinal);
    }
}