using System.Runtime.InteropServices;

namespace CHDStudio.Tests;

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
            Assert.Equal("chdman_arm64.exe", AppConfig.ChdmanExeName);
        else
            Assert.Equal("chdman.exe", AppConfig.ChdmanExeName);
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
        Assert.Equal("CHDStudio", AppConfig.ApplicationName);
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
    public void GitHubApiLatestReleaseUrlPointsToPureLogicCodeRepository()
    {
        var source = AppConfig.PrimaryGitHubApiLatestReleaseUrl;

        // The ownership transfer to the purelogiccode organization completed, so update checks
        // target that repository only.
        Assert.Contains(
            "/purelogiccode/CHDStudio/",
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

    [Fact]
    public void SevenZipExeNameMatchesArchitecture()
    {
        if (AppConfig.IsArm64)
            Assert.Equal("7za_arm64.exe", AppConfig.SevenZipExeName);
        else
            Assert.Equal("7za.exe", AppConfig.SevenZipExeName);
    }

    [Fact]
    public void IsArm64OsMatchesRuntimeInformation()
    {
        var expected = RuntimeInformation.OSArchitecture == Architecture.Arm64;

        Assert.Equal(expected, AppConfig.IsArm64Os);
    }

    [Fact]
    public void ChdmanExeCandidatesFirstCandidateMatchesArchitecture()
    {
        var expected = AppConfig.IsArm64Os ? "chdman_arm64.exe" : "chdman.exe";

        Assert.Equal(expected, AppConfig.ChdmanExeCandidates[0]);
    }

    [Fact]
    public void SevenZipExeCandidatesFirstCandidateMatchesArchitecture()
    {
        var expected = AppConfig.IsArm64Os ? "7za_arm64.exe" : "7za.exe";

        Assert.Equal(expected, AppConfig.SevenZipExeCandidates[0]);
    }

    [Fact]
    public void ChdmanExeCandidatesContainBothBuildsOnArm64Os()
    {
        if (AppConfig.IsArm64Os)
        {
            Assert.Equal(2, AppConfig.ChdmanExeCandidates.Count);
            Assert.Contains("chdman_arm64.exe", AppConfig.ChdmanExeCandidates, StringComparer.Ordinal);
            Assert.Contains("chdman.exe", AppConfig.ChdmanExeCandidates, StringComparer.Ordinal);
        }
        else
        {
            Assert.Equal(["chdman.exe"], AppConfig.ChdmanExeCandidates);
        }
    }

    [Fact]
    public void SevenZipExeCandidatesContainBothBuildsOnArm64Os()
    {
        if (AppConfig.IsArm64Os)
        {
            Assert.Equal(2, AppConfig.SevenZipExeCandidates.Count);
            Assert.Contains("7za_arm64.exe", AppConfig.SevenZipExeCandidates, StringComparer.Ordinal);
            Assert.Contains("7za.exe", AppConfig.SevenZipExeCandidates, StringComparer.Ordinal);
        }
        else
        {
            Assert.Equal(["7za.exe"], AppConfig.SevenZipExeCandidates);
        }
    }

    [Fact]
    public void BugReportApiUrlPointsToSendEndpoint()
    {
        Assert.Equal(
            "https://www.purelogiccode.com/bugreport/api/send-bug-report",
            AppConfig.BugReportApiUrl
        );
    }

    [Fact]
    public void ApplicationStatsApiUrlPointsToStatsEndpoint()
    {
        Assert.Equal(
            "https://www.purelogiccode.com/ApplicationStats/stats",
            AppConfig.ApplicationStatsApiUrl
        );
    }

    [Fact]
    public void BugReportAndStatsUseTheSameSharedApiKey()
    {
        Assert.Equal(AppConfig.BugReportApiKey, AppConfig.ApplicationStatsApiKey);
    }

    [Fact]
    public void BugReportEnvironmentIsProductionOrDevelopment()
    {
        Assert.Contains(
            AppConfig.BugReportEnvironment,
            new[] { "Production", "Development" },
            StringComparer.Ordinal
        );
    }
}