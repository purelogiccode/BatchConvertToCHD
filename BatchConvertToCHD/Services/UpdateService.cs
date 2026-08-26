using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using BatchConvertToCHD.Models;

namespace BatchConvertToCHD.Services;

/// <summary>
/// Service for checking and notifying about application updates from GitHub releases.
/// </summary>
internal class UpdateService
{
    private readonly string _applicationName;
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonSerializerOptions = new() { PropertyNameCaseInsensitive = true };

    internal UpdateService(string applicationName)
        : this(applicationName, AppHttpClient.Client)
    {
    }

    internal UpdateService(string applicationName, HttpClient httpClient)
    {
        _applicationName = applicationName;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Checks GitHub for a newer version of the application and prompts the user to download if available.
    /// </summary>
    /// <param name="onLog">Callback for logging messages.</param>
    /// <param name="onStatusUpdate">Callback for status bar updates.</param>
    /// <param name="onBugReport">Callback for reporting errors.</param>
    internal Task CheckForNewVersionAsync(Action<string> onLog, Action<string> onStatusUpdate,
        Func<string, Exception?, Task> onBugReport)
    {
        return CheckForNewVersionAsync(_httpClient, Assembly.GetExecutingAssembly().GetName().Version, onLog,
            onStatusUpdate, onBugReport);
    }

    /// <summary>
    /// Internal overload for testing that accepts a custom <see cref="HttpClient"/> and version.
    /// Performs the actual update check against the GitHub API - trying each configured release
    /// source in order (primary repository, then fallback) - compares versions, and prompts the
    /// user to download if a newer version is available.
    /// </summary>
    /// <param name="httpClient">The <see cref="HttpClient"/> to use for the request.</param>
    /// <param name="currentVersion">The current application version to compare against.</param>
    /// <param name="onLog">Callback for logging messages.</param>
    /// <param name="onStatusUpdate">Callback for status bar updates.</param>
    /// <param name="onBugReport">Callback for reporting errors.</param>
    internal async Task CheckForNewVersionAsync(HttpClient httpClient, Version? currentVersion, Action<string> onLog,
        Action<string> onStatusUpdate, Func<string, Exception?, Task> onBugReport)
    {
        try
        {
            onLog("Checking for updates on GitHub...");

            var sources = AppConfig.GitHubApiLatestReleaseUrls;

            for (var i = 0; i < sources.Count; i++)
            {
                var isLastSource = i == sources.Count - 1;
                var url = sources[i];

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(_applicationName);

                HttpResponseMessage response;
                try
                {
                    response = await httpClient.SendAsync(request).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    // Transport-level failure: worth retrying against the next source, which may
                    // resolve differently (DNS, redirect, CDN edge).
                    if (!isLastSource)
                    {
                        onLog($"Update source unreachable ({ex.Message}); trying the fallback source...");
                        continue;
                    }

                    throw;
                }

                if (response.StatusCode is System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.TooManyRequests)
                {
                    // Rate limits are per IP and shared by every api.github.com URL, so trying the
                    // fallback cannot help.
                    onLog("GitHub API rate limit exceeded. Skipping update check.");
                    onStatusUpdate("Update check skipped (rate limit)");
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;

                    if (statusCode is >= 500 and < 600 && !isLastSource)
                    {
                        onLog($"Update source returned a server error ({statusCode}); trying the fallback source...");
                        continue;
                    }

                    if (!isLastSource)
                    {
                        // Client errors such as 404 mean this repository has no reachable releases
                        // page (e.g. the ownership transfer has not completed yet), so fall through
                        // to the next source before giving up.
                        onLog($"Update source unavailable ({statusCode} from {url}); trying the fallback source...");
                        continue;
                    }

                    if (statusCode is >= 500 and < 600)
                    {
                        onLog($"Update check skipped: GitHub server error ({statusCode}).");
                        onStatusUpdate("Update check skipped (server error)");
                        return;
                    }

                    response.EnsureSuccessStatusCode();
                }

                var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var latestRelease = JsonSerializer.Deserialize<GitHubRelease>(responseBody, JsonSerializerOptions);
                if (latestRelease == null || latestRelease.Draft || latestRelease.Prerelease ||
                    string.IsNullOrWhiteSpace(latestRelease.TagName))
                {
                    onLog("Latest release is invalid, draft, or prerelease. Skipping.");
                    return;
                }

                var remoteVersionString = ParseVersionFromTag(latestRelease.TagName);

                if (!TryNormalizeVersions(currentVersion, remoteVersionString, out var normalizedCurrent,
                        out var normalizedRemote))
                {
                    onLog($"Could not compare versions. Current: {currentVersion}, Remote: {remoteVersionString}");
                    return;
                }

                onLog($"Current version: {normalizedCurrent}");
                onLog($"Latest version: {normalizedRemote}");

                if (normalizedRemote > normalizedCurrent)
                {
                    if (Application.Current != null)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            var result = MessageBox.Show(
                                $"A new version ({remoteVersionString}) of {_applicationName} is available!\n\nWould you like to go to the download page?",
                                "New Version Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                            if (result == MessageBoxResult.Yes)
                            {
                                try
                                {
                                    Process.Start(
                                        new ProcessStartInfo(latestRelease.HtmlUrl) { UseShellExecute = true });
                                }
                                catch (Exception urlEx)
                                {
                                    onLog($"Failed to open browser: {urlEx.Message}");
                                    _ = onBugReport("Failed to open browser", urlEx);

                                    try
                                    {
                                        Clipboard.SetText(latestRelease.HtmlUrl);
                                    }
                                    catch (Exception clipboardEx)
                                    {
                                        onLog($"Failed to copy URL to clipboard: {clipboardEx.Message}");
                                        _ = onBugReport("Failed to copy URL to clipboard", clipboardEx);
                                    }

                                    MessageBox.Show(
                                        $"Unable to open browser automatically. The update URL has been copied to your clipboard.\n\nURL: {latestRelease.HtmlUrl}\n\nPlease paste it into your browser manually.",
                                        "Browser Launch Failed",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Information);
                                }
                            }
                        });
                    }

                    onStatusUpdate($"Update available: v{remoteVersionString}");
                }
                else
                {
                    onLog("Application is up to date.");
                    onStatusUpdate("Application is up to date");
                }

                return;
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == null)
        {
            onLog($"Update check failed (Network/SSL): {ex.Message}");
            onStatusUpdate("Update check failed (network)");
        }
        catch (HttpRequestException ex)
        {
            onLog($"Update check failed: {ex.Message}");
            onStatusUpdate("Update check failed");
            await onBugReport("Update check failed", ex).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onLog($"Update check failed: {ex.Message}");
            onStatusUpdate("Update check failed");
            await onBugReport("Error checking for updates", ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Normalizes versions to ensure consistent comparison (handles 2-part vs 4-part versions).
    /// If Build or Revision is -1 (undefined), defaults to 0 to avoid ArgumentOutOfRangeException.
    /// Returns false if either version cannot be parsed.
    /// </summary>
    internal static bool TryNormalizeVersions(Version? current, string remoteTag, out Version? normalizedCurrent,
        out Version? normalizedRemote)
    {
        normalizedCurrent = null;
        normalizedRemote = null;

        if (current == null || !Version.TryParse(remoteTag, out var remoteVersion))
        {
            return false;
        }

        normalizedCurrent = new Version(
            current.Major,
            current.Minor,
            current.Build < 0 ? 0 : current.Build,
            current.Revision < 0 ? 0 : current.Revision);
        normalizedRemote = new Version(
            remoteVersion.Major,
            remoteVersion.Minor,
            remoteVersion.Build < 0 ? 0 : remoteVersion.Build,
            remoteVersion.Revision < 0 ? 0 : remoteVersion.Revision);

        return true;
    }

    /// <summary>
    /// Parses a semantic version string from a GitHub release tag name by stripping
    /// common prefixes such as "release", "version", or "v", and removing any leading
    /// non-digit characters.
    /// </summary>
    /// <param name="tagName">The raw tag name from the GitHub release (e.g., "v2.11.0").</param>
    /// <returns>The cleaned version string, or <see cref="string.Empty"/> if the input is null or whitespace.</returns>
    internal static string ParseVersionFromTag(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return string.Empty;
        }

        var tag = tagName.Trim();
        var prefixes = new[] { "release", "version", "v" };
        foreach (var prefix in prefixes)
        {
            if (tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                tag = tag[prefix.Length..];
                break;
            }
        }

        while (tag.Length > 0 && !char.IsDigit(tag[0]))
        {
            tag = tag[1..];
        }

        return tag;
    }
}