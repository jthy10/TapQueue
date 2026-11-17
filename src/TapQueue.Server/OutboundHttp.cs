namespace TapQueue.Server;

/// <summary>
/// How the server reaches identity services on the internet (Microsoft Graph for Entra ID, and the
/// single sign-on provider). One place, so tests can answer for those services themselves.
/// </summary>
public sealed class OutboundHttp
{
    /// <summary>Makes the handler each <see cref="Create"/> uses. Tests swap in a fake.</summary>
    public Func<HttpMessageHandler> Handler { get; init; } = () => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    public HttpClient Create(TimeSpan? timeout = null) => new(Handler()) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
}
