using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace BatchConvertToCHD.Services;

/// <summary>
/// Provides a thread-safe singleton <see cref="HttpClient"/> configured with TLS 1.2/1.3
/// and tolerant SSL certificate validation for use across the application.
/// </summary>
internal static class AppHttpClient
{
    private static SocketsHttpHandler? _handler;
    private static HttpClient? _client;
    private static readonly Lock Lock = new();
    private static readonly ILogger Logger = Log.ForContext(typeof(AppHttpClient));

    /// <summary>
    /// Gets the shared singleton <see cref="HttpClient"/> instance. Creates and configures it
    /// on first access with double-checked locking for thread safety.
    /// </summary>
    internal static HttpClient Client
    {
        get
        {
            lock (Lock)
            {
                if (_client == null)
                {
                    _handler = new SocketsHttpHandler
                    {
                        SslOptions = new SslClientAuthenticationOptions
                        {
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            RemoteCertificateValidationCallback = ServerCertificateValidationCallback
                        },
                        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
                    };
                    _client = new HttpClient(_handler);
                    _client.DefaultRequestHeaders.Add("Accept", "application/json");
                }

                return _client;
            }
        }
    }

    private static bool ServerCertificateValidationCallback(object sender, X509Certificate? certificate,
        X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        if (sslPolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            var subject = (certificate as X509Certificate2)?.Subject ?? certificate?.Subject ?? "unknown";
            Logger.Warning(
                "SSL certificate name mismatch for {Subject}. The server certificate does not match the expected hostname. This may be caused by a proxy or firewall intercepting the connection. Allowing the connection to proceed.",
                subject);
            return true;
        }

        Logger.Warning("SSL certificate validation error: {Errors}. The connection will be rejected.", sslPolicyErrors);
        return false;
    }

    /// <summary>
    /// Disposes the shared <see cref="HttpClient"/> and its underlying handler.
    /// Subsequent access to <see cref="Client"/> will create a new instance.
    /// </summary>
    internal static void Dispose()
    {
        lock (Lock)
        {
            _client?.Dispose();
            _client = null;
            _handler?.Dispose();
            _handler = null;
        }
    }
}