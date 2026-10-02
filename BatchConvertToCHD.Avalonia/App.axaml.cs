using System.Globalization;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BatchConvertToCHD.Dialogs;
using BatchConvertToCHD.Services;
using Serilog;
using Serilog.Events;

namespace BatchConvertToCHD;

/// <summary>
///     Application class for BatchConvertToCHD. Handles startup, exception handling,
///     single-instance enforcement and service initialization.
/// </summary>
public class App : Application
{
    private BugReportService? _bugReportService;
    private Mutex? _singleInstanceMutex;
    private StatsService? _statsService;

    /// <summary>
    ///     Provides a shared, static instance of the <see cref="BugReportService" /> for the entire
    ///     application, allowing any component to submit bug reports without dependency injection.
    ///     May be null before or after the application lifecycle.
    /// </summary>
    internal static BugReportService? SharedBugReportService { get; private set; }

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Initialize services
        SharedBugReportService = new BugReportService(
            AppConfig.BugReportApiUrl,
            AppConfig.BugReportApiKey,
            AppConfig.ApplicationName
        );
        _bugReportService = SharedBugReportService;

        _statsService = new StatsService(
            AppConfig.ApplicationStatsApiUrl,
            AppConfig.ApplicationStatsApiKey,
            AppConfig.ApplicationName
        );

        ConfigureSerilog();

        // Set up global exception handling
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        Dispatcher.UIThread.UnhandledException += DispatchUnhandledException;

        DeleteOldDllFiles();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += App_Exit;

            if (!TryAcquireSingleInstance())
            {
                // Another instance owns the mutex: show a notice and shut down after it closes.
                var dialog = new MessageDialog(
                    $"Another instance of {AppConfig.ApplicationName} is already running.",
                    AppConfig.ApplicationName,
                    MessageBoxButton.Ok,
                    MessageBoxImage.Information
                );
                dialog.Closed += (_, _) => desktop.Shutdown();
                desktop.MainWindow = dialog;
                base.OnFrameworkInitializationCompleted();
                return;
            }

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();

        // Record usage statistics on a background thread
        _ = _statsService?.RecordUsageAsync();
    }

    /// <summary>
    ///     Gracefully shuts down the desktop application from UI code.
    /// </summary>
    internal static void ShutdownApp()
    {
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private bool TryAcquireSingleInstance()
    {
        var name = OperatingSystem.IsWindows()
            ? $"Global\\{AppConfig.ApplicationName}_SingleInstance"
            : $"{AppConfig.ApplicationName}_SingleInstance";

        _singleInstanceMutex = new Mutex(false, name, out var createdNew);
        try
        {
            _singleInstanceMutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // Previous instance terminated abnormally; we now own the mutex
        }

        if (createdNew) return true;

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return false;
    }

    private void ConfigureSerilog()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppConfig.ApplicationName,
            "logs"
        );
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", AppConfig.ApplicationName)
            .Enrich.WithProperty(
                "Version",
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0"
            )
            .WriteTo.Debug(LogEventLevel.Debug, formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                Path.Combine(logDir, "BatchConvertToCHD-.log"),
                LogEventLevel.Debug,
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7
            )
            .WriteTo.Sink(new BugReportApiSink(_bugReportService!))
            .CreateLogger();

        Log.Information("=== Serilog initialized ===");
    }

    private static void DeleteOldDllFiles()
    {
        try
        {
            string[] dllFilesToDelete = ["7z_x64.dll", "7z_arm64.dll"];
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            foreach (var dllFile in dllFilesToDelete)
            {
                var filePath = Path.Combine(baseDirectory, dllFile);
                if (File.Exists(filePath)) File.Delete(filePath);
            }
        }
        catch
        {
            // Silently ignore errors when deleting old DLL files
        }
    }

    private void App_Exit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // ignored
        }
        finally
        {
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }

        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        Dispatcher.UIThread.UnhandledException -= DispatchUnhandledException;

        Log.CloseAndFlush();

        AppHttpClient.Dispose();

        _bugReportService = null;
        SharedBugReportService = null;
        _statsService = null;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "AppDomain.UnhandledException");
            ReportException(exception, "AppDomain.UnhandledException");
        }
    }

    private void DispatchUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Dispatcher.UnhandledException");
        ReportException(e.Exception, "Dispatcher.UnhandledException");
        e.Handled = true;
    }

    private void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e
    )
    {
        Log.Error(e.Exception, "TaskScheduler.UnobservedTaskException");
        ReportException(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    private void ReportException(Exception exception, string source)
    {
        try
        {
            if (string.Equals(source, "AppDomain.UnhandledException", StringComparison.Ordinal))
            {
                // Block synchronously — the process is about to terminate.
                Task.Run(() =>
                    {
                        var x = _bugReportService;
                        if (x != null)
                        {
                            return x.SendBugReportAsync(
                                $"Unhandled Exception from {source}",
                                exception
                            );
                        }

                        return Task.FromResult(false);
                    })
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                // Fire-and-forget for dispatcher/task exceptions — blocking would freeze the UI.
                _ = Task.Run(() =>
                {
                    var x = _bugReportService;
                    if (x != null)
                    {
                        return x.SendBugReportAsync(
                            $"Unhandled Exception from {source}",
                            exception
                        );
                    }

                    return Task.FromResult(false);
                });
            }
        }
        catch
        {
            // Silently ignore any errors in the reporting process
        }
    }
}