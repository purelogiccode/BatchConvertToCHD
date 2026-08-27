using System.Text.Json;
using BatchConvertToCHD.Models;

namespace BatchConvertToCHD.Tests;

public class GitHubReleaseTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void DefaultValuesAreEmpty()
    {
        var release = new GitHubRelease();
        Assert.Equal(string.Empty, release.TagName);
        Assert.Equal(string.Empty, release.HtmlUrl);
        Assert.Equal(string.Empty, release.Name);
        Assert.Equal(string.Empty, release.Body);
        Assert.False(release.Prerelease);
        Assert.False(release.Draft);
    }

    [Fact]
    public void PropertiesCanBeSet()
    {
        var release = new GitHubRelease
        {
            TagName = "v1.0.0",
            HtmlUrl = "https://github.com/repo/releases/v1.0.0",
            Name = "Release 1.0.0",
            Body = "Release notes",
            Prerelease = true,
            Draft = false,
        };
        Assert.Equal("v1.0.0", release.TagName);
        Assert.Equal("https://github.com/repo/releases/v1.0.0", release.HtmlUrl);
        Assert.Equal("Release 1.0.0", release.Name);
        Assert.Equal("Release notes", release.Body);
        Assert.True(release.Prerelease);
        Assert.False(release.Draft);
    }

    [Fact]
    public void DeserializeValidReleaseJson()
    {
        const string json = """
                            {
                                "tag_name": "v2.0.0",
                                "html_url": "https://github.com/test/repo/releases/tag/v2.0.0",
                                "name": "Version 2.0.0",
                                "body": "Major update with new features",
                                "prerelease": false,
                                "draft": false
                            }
                            """;

        var options = JsonOptions;
        var release = JsonSerializer.Deserialize<GitHubRelease>(json, options);

        Assert.NotNull(release);
        Assert.Equal("v2.0.0", release.TagName);
        Assert.Equal("https://github.com/test/repo/releases/tag/v2.0.0", release.HtmlUrl);
        Assert.Equal("Version 2.0.0", release.Name);
        Assert.Equal("Major update with new features", release.Body);
        Assert.False(release.Prerelease);
        Assert.False(release.Draft);
    }

    [Fact]
    public void DeserializePrereleaseJson()
    {
        const string json = """
                            {
                                "tag_name": "v3.0.0-beta",
                                "html_url": "https://github.com/test/repo/releases/tag/v3.0.0-beta",
                                "name": "Beta Release",
                                "body": "Pre-release testing",
                                "prerelease": true,
                                "draft": true
                            }
                            """;

        var options = JsonOptions;
        var release = JsonSerializer.Deserialize<GitHubRelease>(json, options);

        Assert.NotNull(release);
        Assert.True(release.Prerelease);
        Assert.True(release.Draft);
    }

    [Fact]
    public void DeserializeMinimalJsonSetsDefaults()
    {
        const string json = "{}";

        var options = JsonOptions;
        var release = JsonSerializer.Deserialize<GitHubRelease>(json, options);

        Assert.NotNull(release);
        Assert.Equal(string.Empty, release.TagName);
        Assert.False(release.Prerelease);
        Assert.False(release.Draft);
    }
}