// ReSharper disable once RedundantUsingDirective

using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace BatchConvertToCHD.Services;

/// <summary>
///     Provides a thread-safe singleton <see cref="HttpClient" /> configured with TLS 1.2/1.3
///     and tolerant SSL certificate validation for use across the application.
/// </summary>
internal static class AppHttpClient
{
    private static readonly Lock Lock = new();
    private static readonly ILogger Logger = Log.ForContext(typeof(AppHttpClient));

    /// <summary>
    ///     Gets the shared singleton <see cref="HttpClient" /> instance. Creates and configures it
    ///     on first access with double-checked locking for thread safety.
    /// </summary>
    internal static HttpClient Client
    {
        get
        {
            lock (Lock)
            {
                if (ExistingClient == null)
                {
                    ExistingHandler = new SocketsHttpHandler
                    {
                        SslOptions = new SslClientAuthenticationOptions
                        {
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            RemoteCertificateValidationCallback =
                                ServerCertificateValidationCallback
                        },
                        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
                    };
                    ExistingClient = new HttpClient(ExistingHandler);
                    ExistingClient.DefaultRequestHeaders.Add("Accept", "application/json");
                }

                return ExistingClient;
            }
        }
    }

    /// <summary>
    ///     Gets the shared handler once it has been created, or <see langword="null" />. Exposed for
    ///     diagnostics and tests; accessing <see cref="Client" /> creates it on demand.
    /// </summary>
    internal static SocketsHttpHandler? ExistingHandler { get; private set; }

    /// <summary>
    ///     Gets the shared client once it has been created, or <see langword="null" />. Exposed for
    ///     diagnostics and tests; accessing <see cref="Client" /> creates it on demand.
    /// </summary>
    internal static HttpClient? ExistingClient { get; private set; }

    /// <summary>
    ///     TLS validation callback: accepts a hostname mismatch only when no other validation error
    ///     is present (typically a corporate proxy or firewall re-signing traffic), and rejects all
    ///     other validation errors.
    /// </summary>
    /// <param name="sender">The request sender.</param>
    /// <param name="certificate">The server certificate.</param>
    /// <param name="chain">The certificate chain.</param>
    /// <param name="sslPolicyErrors">The validation errors found.</param>
    /// <returns><see langword="true" /> when the connection may proceed.</returns>
    private static bool ServerCertificateValidationCallback(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors
    )
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        if ((sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None)
        {
            var subject =
                (certificate as X509Certificate2)?.Subject ?? certificate?.Subject ?? "unknown";
            Logger.Warning(
                "SSL certificate name mismatch for {Subject} - server certificate does not match the expected hostname, possibly a proxy or firewall; allowing the connection to proceed",
                subject
            );
            return true;
        }

        Logger.Warning(
            "SSL certificate validation error {Errors} - connection will be rejected",
            sslPolicyErrors
        );
        return false;
    }

    /// <summary>
    ///     Disposes the shared <see cref="HttpClient" /> and its underlying handler.
    ///     Subsequent access to <see cref="Client" /> will create a new instance.
    /// </summary>
    internal static void Dispose()
    {
        lock (Lock)
        {
            ExistingClient?.Dispose();
            ExistingClient = null;
            ExistingHandler?.Dispose();
            ExistingHandler = null;
        }
    }
}