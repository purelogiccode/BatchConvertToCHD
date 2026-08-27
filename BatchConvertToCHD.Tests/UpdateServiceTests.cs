using System.Net;
using BatchConvertToCHD.Services;

namespace BatchConvertToCHD.Tests;

public class UpdateServiceTests
{
    [Fact]
    public void ConstructorStoresApplicationName()
    {
        var service = new UpdateService("MyApp");
        var field = typeof(UpdateService).GetField(
            "_applicationName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );
        Assert.NotNull(field);
        Assert.Equal("MyApp", field.GetValue(service));
    }

    [Fact]
    public void InternalConstructorStoresApplicationNameAndHttpClient()
    {
        using var httpClient = new HttpClient();
        var service = new UpdateService("MyApp", httpClient);

        var nameField = typeof(UpdateService).GetField(
            "_applicationName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );
        var httpField = typeof(UpdateService).GetField(
            "_httpClient",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );

        Assert.Equal("MyApp", nameField!.GetValue(service));
        Assert.Same(httpClient, httpField!.GetValue(service));
    }

    #region ParseVersionFromTag

    [Theory]
    [InlineData("v1.0.0", "1.0.0")]
    [InlineData("v1.2.3-beta", "1.2.3-beta")]
    [InlineData("release1.5.0", "1.5.0")]
    [InlineData("version2.0", "2.0")]
    [InlineData("V3.0.1", "3.0.1")]
    [InlineData("no_version_here", "")]
    [InlineData("", "")]
    [InlineData("   v4.5.6   ", "4.5.6")]
    [InlineData("1.0", "1.0")]
    [InlineData("v2024.1", "2024.1")]
    public void ParseVersionFromTagExtractsCorrectly(string input, string expected)
    {
        var result = UpdateService.ParseVersionFromTag(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("v")]
    [InlineData("release")]
    [InlineData("version")]
    public void ParseVersionFromTagPrefixOnlyReturnsEmpty(string prefixOnly)
    {
        var result = UpdateService.ParseVersionFromTag(prefixOnly);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ParseVersionFromTagNullReturnsEmpty()
    {
        var result = UpdateService.ParseVersionFromTag(null!);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ParseVersionFromTagLeadingUnderscoreAndHyphenStripped()
    {
        var result = UpdateService.ParseVersionFromTag("_v-1.0.0");
        Assert.Equal("1.0.0", result);
    }

    #endregion

    #region TryNormalizeVersions

    [Fact]
    public void TryNormalizeVersionsReturnsTrueForValidVersions()
    {
        var current = new Version(2, 7);
        var result = UpdateService.TryNormalizeVersions(
            current,
            "3.0.0",
            out var normalizedCurrent,
            out var normalizedRemote
        );

        Assert.True(result);
        Assert.Equal(new Version(2, 7, 0, 0), normalizedCurrent);
        Assert.Equal(new Version(3, 0, 0, 0), normalizedRemote);
    }

    [Fact]
    public void TryNormalizeVersionsHandlesFullVersions()
    {
        var current = new Version(2, 7, 0, 1);
        var result = UpdateService.TryNormalizeVersions(
            current,
            "2.7.0.1",
            out var normalizedCurrent,
            out var normalizedRemote
        );

        Assert.True(result);
        Assert.Equal(new Version(2, 7, 0, 1), normalizedCurrent);
        Assert.Equal(new Version(2, 7, 0, 1), normalizedRemote);
    }

    [Fact]
    public void TryNormalizeVersionsReturnsFalseForNullCurrentVersion()
    {
        var result = UpdateService.TryNormalizeVersions(
            null,
            "1.0.0",
            out var normalizedCurrent,
            out var normalizedRemote
        );

        Assert.False(result);
        Assert.Null(normalizedCurrent);
        Assert.Null(normalizedRemote);
    }

    [Theory]
    [InlineData("not_a_version")]
    [InlineData("")]
    [InlineData("abc.def")]
    public void TryNormalizeVersionsReturnsFalseForInvalidRemoteTag(string invalidTag)
    {
        var current = new Version(2, 7);
        var result = UpdateService.TryNormalizeVersions(
            current,
            invalidTag,
            out var normalizedCurrent,
            out var normalizedRemote
        );

        Assert.False(result);
        Assert.Null(normalizedCurrent);
        Assert.Null(normalizedRemote);
    }

    [Fact]
    public void TryNormalizeVersionsDefaultsNegativeBuildToZero()
    {
        var current = new Version(1, 0);
        var result = UpdateService.TryNormalizeVersions(
            current,
            "1.0.0",
            out var normalizedCurrent,
            out _
        );

        Assert.True(result);
        Assert.Equal(0, normalizedCurrent!.Build);
        Assert.Equal(0, normalizedCurrent.Revision);
    }

    #endregion

    #region CheckForNewVersionAsync - HTTP scenarios

    private static HttpClient CreateHttpClient(HttpStatusCode statusCode, string responseContent)
    {
        var handler = new FakeHttpMessageHandler(statusCode, responseContent);
        return new HttpClient(handler);
    }

    private const string NewReleaseJson = """
                                          {
                                              "tag_name": "v3.0.0",
                                              "html_url": "https://github.com/test/repo/releases/tag/v3.0.0",
                                              "name": "Version 3.0.0",
                                              "body": "New features",
                                              "prerelease": false,
                                              "draft": false
                                          }
                                          """;

    private const string SameReleaseJson = """
                                           {
                                               "tag_name": "v2.7.0",
                                               "html_url": "https://github.com/test/repo/releases/tag/v2.7.0",
                                               "name": "Version 2.7.0",
                                               "body": "Current version",
                                               "prerelease": false,
                                               "draft": false
                                           }
                                           """;

    private const string DraftReleaseJson = """
                                            {
                                                "tag_name": "v3.0.0",
                                                "html_url": "https://github.com/test/repo/releases/tag/v3.0.0",
                                                "name": "Version 3.0.0",
                                                "body": "Draft release",
                                                "prerelease": false,
                                                "draft": true
                                            }
                                            """;

    private const string PrereleaseJson = """
                                          {
                                              "tag_name": "v3.0.0-beta",
                                              "html_url": "https://github.com/test/repo/releases/tag/v3.0.0-beta",
                                              "name": "Beta Release",
                                              "body": "Pre-release",
                                              "prerelease": true,
                                              "draft": false
                                          }
                                          """;

    private const string MinimalReleaseJson = """
                                              {
                                                  "tag_name": "v3.0.0",
                                                  "html_url": "https://github.com/test/repo/releases/tag/v3.0.0",
                                                  "name": "v3.0.0"
                                              }
                                              """;

    [Fact]
    public async Task CheckForNewVersionAsync_NewVersionAvailable_NotifiesUser()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, NewReleaseJson);
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m => m.Contains("Checking for updates", StringComparison.Ordinal)
        );
        Assert.Contains(
            logMessages,
            static m => m.Contains("Current version:", StringComparison.Ordinal)
        );
        Assert.Contains(
            logMessages,
            static m => m.Contains("Latest version:", StringComparison.Ordinal)
        );
        Assert.Contains(
            statusMessages,
            static m => m.Contains("Update available", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task CheckForNewVersionAsync_UpToDate_LogsUpToDate()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, SameReleaseJson);
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m => m.Contains("up to date", StringComparison.Ordinal)
        );
        Assert.Contains(
            statusMessages,
            static m => m.Contains("up to date", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            statusMessages,
            static m => m.Contains("Update available", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task CheckForNewVersionAsync_DraftRelease_Skips()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, DraftReleaseJson);
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(static _ => { }),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(logMessages, static m => m.Contains("draft", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckForNewVersionAsync_Prerelease_Skips()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, PrereleaseJson);
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(static _ => { }),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m => m.Contains("prerelease", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task CheckForNewVersionAsync_RateLimitExceeded_HandlesGracefully()
    {
        using var httpClient = CreateHttpClient(
            HttpStatusCode.Forbidden,
            """{ "message": "API rate limit exceeded for user." }"""
        );
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m => m.Contains("rate limit exceeded", StringComparison.Ordinal)
        );
        Assert.Contains(
            statusMessages,
            static m => m.Contains("rate limit", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task CheckForNewVersionAsync_HttpError_ReportsBug()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.BadRequest, "Bad request");
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();
        string? reportedError = null;

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(
                        (msg, _) =>
                        {
                            reportedError = msg;
                            return Task.CompletedTask;
                        }
                    ),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m => m.Contains("Update check failed", StringComparison.Ordinal)
        );
        Assert.NotNull(reportedError);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task CheckForNewVersionAsync_ServerErrors_HandledGracefullyWithoutBugReport(
        HttpStatusCode statusCode
    )
    {
        using var httpClient = CreateHttpClient(statusCode, "Server error");
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();
        var bugReportCalled = false;

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(
                        (_, _) =>
                        {
                            bugReportCalled = true;
                            return Task.CompletedTask;
                        }
                    ),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m => m.Contains("server error", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            statusMessages,
            static m => m.Contains("server error", StringComparison.OrdinalIgnoreCase)
        );
        Assert.False(bugReportCalled, "Bug report should NOT be called for server errors (5xx)");
    }

    [Fact]
    public async Task CheckForNewVersionAsync_GatewayTimeout_LogsAndSkips()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.GatewayTimeout, "Gateway Timeout");
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m =>
                m.Contains("504", StringComparison.Ordinal)
                || m.Contains("server error", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            statusMessages,
            static m => m.Contains("server error", StringComparison.OrdinalIgnoreCase)
        );
        Assert.DoesNotContain(
            statusMessages,
            static m => m.Contains("failed", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task CheckForNewVersionAsync_BadGateway_LogsAndSkips()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.BadGateway, "Bad Gateway");
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m =>
                m.Contains("502", StringComparison.Ordinal)
                || m.Contains("server error", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            statusMessages,
            static m => m.Contains("server error", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task CheckForNewVersionAsync_NetworkFailure_LogsAndReports()
    {
        var handler = new FakeHttpMessageHandler(static _ =>
            throw new HttpRequestException("Network unreachable")
        );
        using var httpClient = new HttpClient(handler);
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();
        var bugReportCalled = false;

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(
                        (_, _) =>
                        {
                            bugReportCalled = true;
                            return Task.CompletedTask;
                        }
                    ),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m => m.Contains("Network unreachable", StringComparison.Ordinal)
        );
        Assert.Contains(
            statusMessages,
            static m => m.Contains("network", StringComparison.Ordinal)
        );
        Assert.False(bugReportCalled);
    }

    [Fact]
    public async Task CheckForNewVersionAsync_GenericException_ReportsBug()
    {
        var handler = new FakeHttpMessageHandler(static _ =>
            throw new InvalidOperationException("Unexpected error")
        );
        using var httpClient = new HttpClient(handler);
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();
        string? reportedError = null;

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(
                        (msg, _) =>
                        {
                            reportedError = msg;
                            return Task.CompletedTask;
                        }
                    ),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m => m.Contains("Update check failed", StringComparison.Ordinal)
        );
        Assert.Contains(
            statusMessages,
            static m => m.Contains("Update check failed", StringComparison.Ordinal)
        );
        Assert.NotNull(reportedError);
    }

    [Fact]
    public async Task CheckForNewVersionAsync_OlderRemoteVersion_DoesNotNotify()
    {
        using var httpClient = CreateHttpClient(
            HttpStatusCode.OK,
            """
            {
                "tag_name": "v1.0.0",
                "html_url": "https://github.com/test/repo/releases/tag/v1.0.0",
                "name": "Old version",
                "body": "Old release",
                "prerelease": false,
                "draft": false
            }
            """
        );
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m => m.Contains("up to date", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            statusMessages,
            static m => m.Contains("Update available", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task CheckForNewVersionAsync_RemoteVersionHigherByMinor_Notifies()
    {
        using var httpClient = CreateHttpClient(
            HttpStatusCode.OK,
            """
            {
                "tag_name": "v2.8.0",
                "html_url": "https://github.com/test/repo/releases/tag/v2.8.0",
                "name": "Minor update",
                "body": "Minor release",
                "prerelease": false,
                "draft": false
            }
            """
        );
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(
            statusMessages,
            static m => m.Contains("Update available", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task CheckForNewVersionAsync_RemoteVersionHigherByMajor_Notifies()
    {
        using var httpClient = CreateHttpClient(
            HttpStatusCode.OK,
            """
            {
                "tag_name": "v3.0.0",
                "html_url": "https://github.com/test/repo/releases/tag/v3.0.0",
                "name": "Major update",
                "body": "Major release",
                "prerelease": false,
                "draft": false
            }
            """
        );
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();

        var currentVersion = new Version(1, 0, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(
            statusMessages,
            static m => m.Contains("Update available", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task CheckForNewVersionAsync_InvalidTag_SkipsWithLog()
    {
        using var httpClient = CreateHttpClient(
            HttpStatusCode.OK,
            """
            {
                "tag_name": "no_version_prefix",
                "html_url": "https://github.com/test/repo/releases/tag/noversion",
                "name": "No version",
                "body": "Invalid",
                "prerelease": false,
                "draft": false
            }
            """
        );
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(static _ => { }),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m => m.Contains("Could not compare versions", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task CheckForNewVersionAsync_MinimalReleaseJson_NewerVersion_Notifies()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, MinimalReleaseJson);
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();

        var currentVersion = new Version(2, 0, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(
            statusMessages,
            static m => m.Contains("Update available", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task CheckForNewVersionAsync_ForbiddenWithoutRateLimit_ThrowsHttpError()
    {
        using var httpClient = CreateHttpClient(HttpStatusCode.Forbidden, "Access denied");
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();
        var statusMessages = new List<string>();

        var currentVersion = new Version(2, 7, 0);

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    currentVersion,
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Contains(
            logMessages,
            static m => m.Contains("rate limit exceeded", StringComparison.Ordinal)
        );
        Assert.Contains(
            statusMessages,
            static m => m.Contains("rate limit", StringComparison.Ordinal)
        );
    }

    #endregion

    #region CheckForNewVersionAsync - release source failures

    [Fact]
    public async Task CheckForNewVersionAsync_ReleaseNotFound_ReportsFailureAndNotifiesOnce()
    {
        // The configured repository has no reachable releases page; the check fails exactly
        // once and routes the error to the bug-report callback.
        var bugReportCount = 0;
        var handler = new FakeHttpMessageHandler(static _ => new HttpResponseMessage(
            HttpStatusCode.NotFound
        )
        {
            Content = new StringContent("""{ "message": "Not Found" }"""),
        });
        using var httpClient = new HttpClient(handler);
        var service = new UpdateService("TestApp", httpClient);
        var statusMessages = new List<string>();

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    new Version(2, 7, 0),
                    (Action<string>)(static _ => { }),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(
                        (_, _) =>
                        {
                            bugReportCount++;
                            return Task.CompletedTask;
                        }
                    ),
                ]
            )!;
        await task;

        Assert.Contains(
            statusMessages,
            static m => m.Contains("failed", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal(1, bugReportCount);
    }

    [Fact]
    public async Task CheckForNewVersionAsync_ServerError_SkipsCheckWithoutBugReport()
    {
        var bugReportCount = 0;
        var handler = new FakeHttpMessageHandler(static _ => new HttpResponseMessage(
            HttpStatusCode.InternalServerError
        )
        {
            Content = new StringContent("Server error"),
        });
        using var httpClient = new HttpClient(handler);
        var service = new UpdateService("TestApp", httpClient);
        var statusMessages = new List<string>();

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    new Version(2, 7, 0),
                    (Action<string>)(static _ => { }),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(
                        (_, _) =>
                        {
                            bugReportCount++;
                            return Task.CompletedTask;
                        }
                    ),
                ]
            )!;
        await task;

        Assert.Contains(
            statusMessages,
            static m => m.Contains("skipped", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal(0, bugReportCount);
    }

    [Fact]
    public async Task CheckForNewVersionAsync_SourceUnreachable_ReportsNetworkFailureWithoutBugReport()
    {
        var bugReportCount = 0;
        var handler = new FakeHttpMessageHandler(static _ =>
            throw new HttpRequestException("Name resolution failed")
        );
        using var httpClient = new HttpClient(handler);
        var service = new UpdateService("TestApp", httpClient);
        var statusMessages = new List<string>();

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    new Version(2, 7, 0),
                    (Action<string>)(static _ => { }),
                    (Action<string>)(statusMessages.Add),
                    (Func<string, Exception?, Task>)(
                        (_, _) =>
                        {
                            bugReportCount++;
                            return Task.CompletedTask;
                        }
                    ),
                ]
            )!;
        await task;

        Assert.Contains(
            statusMessages,
            static m => m.Contains("network", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal(0, bugReportCount);
    }

    [Fact]
    public async Task CheckForNewVersionAsync_RateLimit_SkipsCheck()
    {
        // Rate limits are per IP and shared across api.github.com URLs, so retrying another
        // source cannot help; a single request must be made.
        var requestCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{ "message": "API rate limit exceeded for user." }"""
                ),
            };
        });
        using var httpClient = new HttpClient(handler);
        var service = new UpdateService("TestApp", httpClient);
        var logMessages = new List<string>();

        var method = typeof(UpdateService).GetMethod(
            "CheckForNewVersionAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [
                typeof(HttpClient),
                typeof(Version),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(Func<string, Exception?, Task>),
            ]
        );
        Assert.NotNull(method);

        var task = (Task)
            method.Invoke(
                service,
                [
                    httpClient,
                    new Version(2, 7, 0),
                    (Action<string>)(logMessages.Add),
                    (Action<string>)(static _ => { }),
                    (Func<string, Exception?, Task>)(static (_, _) => Task.CompletedTask),
                ]
            )!;
        await task;

        Assert.Equal(1, requestCount);
        Assert.Contains(
            logMessages,
            static m => m.Contains("rate limit exceeded", StringComparison.Ordinal)
        );
    }

    #endregion
}