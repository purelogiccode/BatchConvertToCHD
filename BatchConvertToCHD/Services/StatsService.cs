// ReSharper disable once RedundantUsingDirective

using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using Serilog;

namespace BatchConvertToCHD.Services;

/// <summary>
///     Records application usage statistics against the ApplicationStats API at startup.
///     Failures (offline, rate limiting, server errors) are logged and never surface to the user.
/// </summary>
internal class StatsService
{
    private static readonly ILogger Logger = Log.ForContext<StatsService>();
    private readonly HttpClient _httpClient;

    /// <summary>
    ///     Initializes a new instance of the <see cref="StatsService" /> class using the shared
    ///     <see cref="AppHttpClient" />.
    /// </summary>
    /// <param name="apiUrl">The stats endpoint URL.</param>
    /// <param name="apiKey">The bearer token used to authenticate the request.</param>
    /// <param name="applicationId">The application identifier recorded by the server.</param>
    internal StatsService(string apiUrl, string apiKey, string applicationId)
        : this(apiUrl, apiKey, applicationId, AppHttpClient.Client)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="StatsService" /> class with a caller-supplied
    ///     HTTP client (used by tests).
    /// </summary>
    /// <param name="apiUrl">The stats endpoint URL.</param>
    /// <param name="apiKey">The bearer token used to authenticate the request.</param>
    /// <param name="applicationId">The application identifier recorded by the server.</param>
    /// <param name="httpClient">The HTTP client used to send the request.</param>
    internal StatsService(string apiUrl, string apiKey, string applicationId, HttpClient httpClient)
    {
        ApiUrl = apiUrl;
        ApiKey = apiKey;
        ApplicationId = applicationId;
        _httpClient = httpClient;
    }

    /// <summary>Gets the stats endpoint URL. Exposed for diagnostics and tests.</summary>
    internal string ApiUrl { get; }

    /// <summary>Gets the API key used for authentication. Exposed for diagnostics and tests.</summary>
    internal string ApiKey { get; }

    /// <summary>Gets the application identifier sent with usage records. Exposed for diagnostics and tests.</summary>
    internal string ApplicationId { get; }

    /// <summary>
    ///     Sends one usage record for this application (id and version) to the stats endpoint.
    ///     Never throws; errors are logged at debug/information level.
    /// </summary>
    /// <returns>A task that completes when the request has finished or failed.</returns>
    internal async Task RecordUsageAsync()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

            var payload = new { applicationId = ApplicationId, version };

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("Authorization", $"Bearer {ApiKey}");
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);

            var statusCode = (int)response.StatusCode;

            if (statusCode == 429)
            {
                Logger.Debug(
                    "Usage statistics rate-limited (HTTP 429) - this is expected behavior"
                );
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                Logger.Information(
                    "Failed to record usage statistics: HTTP {StatusCode}",
                    statusCode
                );
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to record usage statistics (network error)");
        }
    }
}