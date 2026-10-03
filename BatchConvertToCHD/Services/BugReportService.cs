using System.Globalization;
// ReSharper disable once RedundantUsingDirective
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace BatchConvertToCHD.Services;

/// <summary>
///     Service responsible for sending bug reports to the BugReport API
/// </summary>
internal class BugReportService
{
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
        "free on",
        "No supported primary files found in archive",
        "chdman.exe not found",
        // Encoder-presence notices depend on the user's installation, not app logic.
        "chdman.exe was not found",
        "Not a valid CHD file",
        "Invalid or corrupt data",
        "Cannot open file",
        "Partial extraction:",
        // chdman failing on a user file is routine and the built-in CHDSharp fallback usually
        // succeeds. When both encoders fail, the classified LogError below reports the real cause.
        "chdman failed for",
        "Falling back to the built-in CHDSharp",
        // chdman-side failures on user data: its exit summary and C++ runtime crashes.
        // CHDSharp and PBPSharp extraction failures are intentionally NOT excluded —
        // their maintainer wants extraction bugs (with debug details) in the bug API.
        "Fatal error occurred",
        "cannot create std::vector",
        // chdman.cpp:1517 wraps every failure inside its compression phase ("Error during
        // compression: <OS error>"); the message is the user's storage failing, not app logic.
        "Error during compression",
        // chdman rejecting the user's cue/image with a parse error ("Error parsing input file
        // (...: Unsupported format / Invalid data)") and I/O failures on failing disks are
        // user-data/environment problems; the app already shows per-cause guidance.
        "Error parsing input file",
        "failed due to an I/O error",
        // Skip notices for files whose content does not match their extension (incomplete
        // downloads etc.); skipping is the intended behaviour.
        "and it is not a usable disc image",
        // A file that is already a CHD is skipped by design (the batch would otherwise feed its
        // own outputs back in); the user is told to copy it to the output folder instead.
        "this file is already a CHD",
        "CRITICAL ERROR: The following required component",
        "referenced files are missing",
        "could not be resolved",
        // An Alcohol descriptor without its data file (incomplete set, renamed file).
        "the .mdf data file was not found",
        "MP3 audio track could not be decoded",
        "is not divisible by",
        // A truncated ECM stream is a user-data problem; the decoder already tells the user to
        // re-download the file.
        "ends part way through a block",
        // Corrupt Alcohol descriptors (including MDS v2 files whose encrypted descriptor fails to
        // decipher or decompress) are user-data problems.
        "so it is corrupt or truncated",
        "could not be read as an Alcohol descriptor",
        // An MDS v2/MDX image whose track data is password-protected and the user supplied no
        // password (the app cannot prompt); the message is actionable.
        "track data is encrypted",
        // chdman reporting that a drive or device disappeared mid-conversion (unplugged USB drive,
        // dropped network share) is an environment problem, not an app bug.
        "A device which does not exist was specified",
        // The conversion-time counterpart of the startup probe: the bundled chdman build cannot run
        // on this CPU/Windows version, which is an installation problem.
        "may be incompatible with this computer's CPU",
        // An image recovered from ECM/ISZ/archive/parts whose size fits no sector layout chdman can
        // read is a user-data problem (truncated or damaged download), not an app bug.
        "not a whole number of 2352-byte",
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
        // Split volume sets of 7z/zip are extracted in-app; these skips are user-data problems
        // (missing volumes, no convertible image inside, no 7za on disk).
        "the extracted set contained no supported disc image",
        "The split archive may be corrupted or incomplete",
        "the split archive cannot be extracted",
        "CCDSharp: Conversion error",
        "File not found, skipping:",
        // CHD open/read failures during extraction are user-data problems (corrupt CHD files).
        "Failed to open '",
        // File move failures after conversion are environment issues (locked files, permissions).
        "Failed to move temp output to destination",
        "Failed to move CHDSharp output to destination",
        // Encoder start failures depend on the user's installation.
        "Failed to start chdman",
        "Failed to start CHDSharp",
        // The startup probe crashing with a negative exit code (0xC0000139 entry point not
        // found, illegal instruction, ...) means the bundled chdman build is incompatible with
        // the user's CPU/Windows version - an installation problem, not app logic.
        "terminated abnormally during the startup check",
        // The output folder's drive is gone (USB unplugged, network drive dropped).
        "output folder is not available",
        // Writing into the output folder is denied (root of C:\, "Program Files", read-only
        // drive); the app already shows one actionable dialog per batch start.
        "output folder is not writable",
        // Direct-stream extraction over a network share can hit a transient SMB hiccup
        // ("An unexpected network error occurred." / French "Erreur réseau inattendue.");
        // the extractor retries via a local temp copy, so this intermediate notice is
        // not an app bug.
        "Direct extraction failed",
        "will fall back to temp-copy extraction"
    ];

    private static readonly string[] DirectlyReportedMessagePatterns =
    [
        "AppDomain.UnhandledException",
        "Dispatcher.UnhandledException",
        "TaskScheduler.UnobservedTaskException"
    ];
    private readonly HttpClient _httpClient;

    /// <summary>Gets the API endpoint URL. Exposed for diagnostics and tests.</summary>
    internal string ApiUrl { get; }

    /// <summary>Gets the API key used for authentication. Exposed for diagnostics and tests.</summary>
    internal string ApiKey { get; }

    /// <summary>Gets the application name sent with reports. Exposed for diagnostics and tests.</summary>
    internal string ApplicationName { get; }

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
        ApiUrl = apiUrl ?? throw new ArgumentNullException(nameof(apiUrl));
        ApiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        ApplicationName =
            applicationName ?? throw new ArgumentNullException(nameof(applicationName));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    ///     Returns whether the message matches a known-noise pattern that must never be sent to the
    ///     bug-report API (user-data, environment and installation problems).
    /// </summary>
    /// <param name="message">The rendered log message to test.</param>
    /// <returns><see langword="true" /> when the message must not be reported.</returns>
    internal static bool IsExcludedFromBugReport(string message)
    {
        foreach (var pattern in ExcludedMessagePatterns)
        {
            if (message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Returns whether the message is an unhandled-exception log entry that the application
    ///     reports through its own exception handlers. Those handlers send the report directly
    ///     (synchronously for fatal AppDomain exceptions), so the Serilog sink must not forward the
    ///     same event as well or every crash lands in the bug tracker twice.
    /// </summary>
    /// <param name="message">The rendered log message to test.</param>
    /// <returns><see langword="true" /> when the sink must not forward the message.</returns>
    internal static bool IsDirectlyReportedByApp(string message)
    {
        foreach (var pattern in DirectlyReportedMessagePatterns)
        {
            if (message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Sends a bug report to the API with full environment and exception details
    /// </summary>
    /// <param name="message">A summary of the error or bug report</param>
    /// <param name="ex">The exception object, if available</param>
    /// <param name="token">The cancellation token to observe</param>
    /// <returns>A task representing the asynchronous operation</returns>
    internal virtual async Task<bool> SendBugReportAsync(
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
                applicationName = ApplicationName,
                version = versionString,
                userInfo = Environment.UserName,
                environment = AppConfig.BugReportEnvironment,
                stackTrace
            };

            var content = JsonContent.Create(requestPayload);

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("X-API-KEY", ApiKey);
            request.Content = content;

            using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception sendEx)
        {
            Log.Debug(sendEx, "Failed to send bug report");
            return false;
        }
    }

    /// <summary>
    ///     Builds a formatted report string with all details for the message field
    /// </summary>
    internal string BuildFormattedReport(string message, Exception? ex)
    {
        var sb = new StringBuilder();

        // === Environment Details ===
        sb.AppendLine("=== Environment Details ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application Name: {ApplicationName}");
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
        var osName = OperatingSystem.IsWindows()
            ? "Windows"
            : OperatingSystem.IsMacOS()
                ? "MacOsX"
                : "Linux";
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{osName} Version: {Environment.OSVersion.Version}"
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
    ///     Appends exception details to the StringBuilder
    /// </summary>
    internal static void AppendExceptionDetails(StringBuilder sb, Exception exception, int level = 0)
    {
        const int maxDepth = 5;
        while (level < maxDepth)
        {
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
                foreach (var line in lines) sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}  {line}");
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
                level++;
                continue;
            }

            break;
        }
    }

    /// <summary>
    ///     Gets exception stack trace for structured API fields
    /// </summary>
    internal static string GetExceptionStackTrace(Exception? ex)
    {
        if (ex == null)
            return "N/A";

        var sb = new StringBuilder();
        AppendExceptionDetails(sb, ex);
        return sb.ToString();
    }

    /// <summary>
    ///     Gets environment details for structured API fields
    /// </summary>
    internal static string GetApplicationVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
    }
}