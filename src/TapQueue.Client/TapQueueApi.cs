using System.Net;
using System.Net.Http.Json;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Client;

/// <summary>Talks to the TapQueue server, signing in again automatically if the session lapses.</summary>
public sealed class TapQueueApi(ClientConfig config) : IDisposable
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromMinutes(5),
    };

    public ClientSessionResponse? Session { get; private set; }

    /// <summary>How the server wants the tray to sign in (<see cref="ClientSignIn"/>), as of the last sign-in.</summary>
    public string SignInMode { get; private set; } = ClientSignIn.Pc;

    /// <summary>With <see cref="ClientSignIn.Domain"/>: the domain account this PC user signed in with, if any.</summary>
    public SavedSignIn? Remembered { get; private set; } = SavedSignIn.Load(config.ServerUrl);

    /// <summary>
    /// Signs in the way the server asks: as the PC's user, or with the remembered domain sign-in.
    /// Throws <see cref="SignInRequiredException"/> if it wants a domain account and there's none (or
    /// the server forgot it).
    /// </summary>
    public async Task<ClientSessionResponse> SignInAsync(CancellationToken ct = default)
    {
        SignInMode = await GetSignInModeAsync(ct);
        if (SignInMode != ClientSignIn.Domain)
            return await CreateSessionAsync(new ClientSessionRequest(
                config.EffectiveUsername,
                string.IsNullOrWhiteSpace(config.Token) ? null : config.Token.Trim(),
                Environment.UserName,
                Environment.MachineName,
                TapQueueVersion.Current,
                ClientPlatform.Current), ct);

        if (Remembered is null)
            throw new SignInRequiredException("Sign in with your domain account.");
        try
        {
            return await CreateSessionAsync(Request(Remembered.Username, rememberToken: Remembered.RememberToken), ct);
        }
        catch (TapQueueApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            Forget();
            throw new SignInRequiredException(ex.Message);
        }
    }

    /// <summary>
    /// "Sign in as…": checks the domain account's password and remembers the sign-in (not the password).
    /// Whoever was signed in before is signed out once it works, so a mistyped password changes nothing.
    /// </summary>
    public async Task<ClientSessionResponse> SignInWithPasswordAsync(string username, string password, CancellationToken ct = default)
    {
        var (previous, previousRemembered) = (Session, Remembered);
        var session = await CreateSessionAsync(Request(username.Trim(), password: password), ct);
        SignInMode = ClientSignIn.Domain;
        if (session.RememberToken is { } token)
        {
            Remembered = new SavedSignIn(config.ServerUrl, session.User.Username, token);
            Remembered.Save();
        }
        if (previous is not null)
            await EndOnServerAsync(previous, previousRemembered?.RememberToken, ct);
        return session;
    }

    /// <summary>Ends the session and forgets the remembered sign-in, here and on the server (if it can be reached).</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var (session, remembered) = (Session, Remembered);
        Forget();
        Session = null;
        if (session is not null)
            await EndOnServerAsync(session, remembered?.RememberToken, ct);
    }

    private async Task EndOnServerAsync(ClientSessionResponse session, string? rememberToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/client/sign-out")
            {
                Content = JsonContent.Create(new ClientSignOutRequest(rememberToken), options: TapQueueJson.Options),
            };
            request.Headers.Authorization = new("Bearer", session.SessionToken);
            using var response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Signed out here either way; the server drops the session when its heartbeats stop.
        }
    }

    private void Forget()
    {
        Remembered = null;
        SavedSignIn.Delete();
    }

    /// <summary>Servers older than 0.6 don't say, and only know the PC's user.</summary>
    private async Task<string> GetSignInModeAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/v1/client/sign-in", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return ClientSignIn.Pc;
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ClientSignInInfoDto>(TapQueueJson.Options, ct))?.Mode ?? ClientSignIn.Pc;
    }

    private static ClientSessionRequest Request(string username, string? password = null, string? rememberToken = null) =>
        new(username, null, Environment.UserName, Environment.MachineName, TapQueueVersion.Current, ClientPlatform.Current, password, rememberToken);

    private async Task<ClientSessionResponse> CreateSessionAsync(ClientSessionRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/client/session", request, TapQueueJson.Options, ct);
        await EnsureSuccessAsync(response, ct);
        Session = (await response.Content.ReadFromJsonAsync<ClientSessionResponse>(TapQueueJson.Options, ct))!;
        return Session;
    }

    public Task HeartbeatAsync(CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Post, "api/v1/client/heartbeat", null, ct);

    public async Task<List<JobDto>> GetHeldJobsAsync(CancellationToken ct = default) =>
        await SendAsync<List<JobDto>>(HttpMethod.Get, "api/v1/me/jobs", null, ct) ?? [];

    public async Task<List<PrinterDto>> GetPrintersAsync(CancellationToken ct = default) =>
        await SendAsync<List<PrinterDto>>(HttpMethod.Get, "api/v1/printers", null, ct) ?? [];

    public async Task<ReleaseResponse> ReleaseAsync(string printerId, IReadOnlyList<long>? jobIds, CancellationToken ct = default) =>
        (await SendAsync<ReleaseResponse>(HttpMethod.Post, "api/v1/me/release", new ReleaseRequest(printerId, jobIds), ct))!;

    public Task CancelJobAsync(long jobId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/v1/me/jobs/{jobId}", null, ct);

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (Session is null)
                await SignInAsync(ct);
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new("Bearer", Session!.SessionToken);
            if (body is not null)
                request.Content = JsonContent.Create(body, body.GetType(), options: TapQueueJson.Options);
            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                await SignInAsync(ct);
                continue;
            }
            await EnsureSuccessAsync(response, ct);
            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
                return default;
            return await response.Content.ReadFromJsonAsync<T>(TapQueueJson.Options, ct);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            message = (await response.Content.ReadFromJsonAsync<ErrorResponse>(TapQueueJson.Options, ct))?.Error;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
        }
        throw new TapQueueApiException(response.StatusCode, message ?? $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    public void Dispose() => _http.Dispose();
}

public class TapQueueApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>The server wants a domain account (<see cref="ClientSignIn.Domain"/>) and none is remembered: use "Sign in as…".</summary>
public sealed class SignInRequiredException(string message) : TapQueueApiException(HttpStatusCode.Unauthorized, message);
