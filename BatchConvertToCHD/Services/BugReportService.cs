using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace BatchConvertToCHD.Services;

/// <summary>
/// Service responsible for sending bug reports to the BugReport API
/// </summary>
internal class BugReportService
{
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _applicationName;
    private readonly HttpClient _httpClient;

    private static readonly string[] ExcludedMessagePatterns =
    [
        "Failed to record usage statistics",
        "Temp drive (",
        "Output drive (",
        "drive has ",
        "drive (",
        "input files total",
        "CHD files total",
        "You may run out of disk space",
        "Temporary files are created during conversion",
        "CHD compression usually reduces",
        "Extracted files are typically larger",
        "disk space",
        "disk full",
        "No supported primary files found in archive",
        "chdman.exe not found",
        "Not a valid CHD file",
        "Invalid or corrupt data",
        "Cannot open file",
        "Partial extraction:",
        // chdman-side failures on user data: its exit summary and C++ runtime crashes.
        // CHDSharp and PBPSharp extraction failures are intentionally NOT excluded —
        // their maintainer wants extraction bugs (with debug details) in the bug API.
        "Fatal error occurred",
        "cannot create std::vector",
        "CRITICAL ERROR: The following required component is missing",
        "referenced files are missing",
        "could not be resolved",
        "MP3 audio track could not be decoded",
        "is not divisible by",
        "could not validate referenced files",
        "The file or directory is corrupted and unreadable",
        "Retry via temp failed",
        "archive file may be corrupted",
        "archive is invalid or corrupt",
        "archive file appears to be incomplete",
        "archive file may be corrupted or in an unsupported format",
        "archive file may be corrupted or unsupported",
        "multi-part RAR with a missing volume",
        "unavailable network location",
        "Archive is encrypted",
        "compression method that is not supported",
        "CCDSharp: Conversion error",
        "File not found, skipping:",
    ];

    internal static bool IsExcludedFromBugReport(string message)
    {
        foreach (var pattern in ExcludedMessagePatterns)
        {
            if (message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal BugReportService(string apiUrl, string apiKey, string applicationName)
        : this(apiUrl, apiKey, applicationName, AppHttpClient.Client)
    {
    }

    internal BugReportService(
        string apiUrl,
        string apiKey,
        string applicationName,
        HttpClient httpClient
    )
    {
        _apiUrl = apiUrl ?? throw new ArgumentNullException(nameof(apiUrl));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _applicationName =
            applicationName ?? throw new ArgumentNullException(nameof(applicationName));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Sends a bug report to the API with full environment and exception details
    /// </summary>
    /// <param name="message">A summary of the error or bug report</param>
    /// <param name="ex">The exception object, if available</param>
    /// <param name="token">The cancellation token to observe</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public virtual async Task<bool> SendBugReportAsync(
        string message,
        Exception? ex = null,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();

        if (IsExcludedFromBugReport(message))
            return false;

        try
        {
            var formattedMessage = BuildFormattedReport(message, ex);

            var versionString = GetApplicationVersion();

            var stackTrace = GetExceptionStackTrace(ex);

            var requestPayload = new
            {
                message = formattedMessage,
                applicationName = _applicationName,
                version = versionString,
                userInfo = Environment.UserName,
                environment = AppConfig.BugReportEnvironment,
                stackTrace,
            };

            var content = JsonContent.Create(requestPayload);

            using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl);
            request.Headers.Add("X-API-KEY", _apiKey);
            request.Content = content;

            var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception sendEx)
        {
            Serilog.Log.Debug(sendEx, "Failed to send bug report");
            return false;
        }
    }

    /// <summary>
    /// Builds a formatted report string with all details for the message field
    /// </summary>
    private string BuildFormattedReport(string message, Exception? ex)
    {
        var sb = new StringBuilder();

        // === Environment Details ===
        sb.AppendLine("=== Environment Details ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Name: {_applicationName}");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"Application Version: {Assembly.GetExecutingAssembly().GetName().Version}"
        );
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS Version: {Environment.OSVersion}");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"Architecture: {RuntimeInformation.ProcessArchitecture}"
        );
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"OS Architecture: {RuntimeInformation.OSArchitecture}"
        );
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"Bitness: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}"
        );
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"Windows Version: {Environment.OSVersion.Version}"
        );
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"Processor Count: {Environment.ProcessorCount}"
        );
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"Base Directory: {AppDomain.CurrentDomain.BaseDirectory}"
        );
        sb.AppendLine(CultureInfo.InvariantCulture, $"Temp Path: {Path.GetTempPath()}");

        // === Error Details ===
        sb.AppendLine();
        sb.AppendLine("=== Error Details ===");
        sb.AppendLine(message);

        // === Exception Details ===
        if (ex != null)
        {
            sb.AppendLine();
            sb.AppendLine("=== Exception Details ===");
            AppendExceptionDetails(sb, ex);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends exception details to the StringBuilder
    /// </summary>
    private static void AppendExceptionDetails(StringBuilder sb, Exception exception, int level = 0)
    {
        const int maxDepth = 5;
        while (true)
        {
            if (level >= maxDepth)
                break;

            var indent = new string(' ', level * 2);

            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}Type: {exception.GetType().FullName}"
            );
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Message: {exception.Message}");
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}Source: {exception.Source ?? "N/A"}"
            );
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}StackTrace:");
            if (!string.IsNullOrEmpty(exception.StackTrace))
            {
                var lines = exception.StackTrace.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries
                );
                foreach (var line in lines)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}  {line}");
                }
            }
            else
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{indent}  (No stack trace available)"
                );
            }

            // If there's an inner exception, include it too
            if (exception.InnerException != null)
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}Inner Exception:");
                exception = exception.InnerException;
                level += 1;
                continue;
            }

            break;
        }
    }

    /// <summary>
    /// Gets exception stack trace for structured API fields
    /// </summary>
    private static string GetExceptionStackTrace(Exception? ex)
    {
        if (ex == null)
            return "N/A";

        var sb = new StringBuilder();
        AppendExceptionDetails(sb, ex);
        return sb.ToString();
    }

    /// <summary>
    /// Gets environment details for structured API fields
    /// </summary>
    private static string GetApplicationVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
    }
}