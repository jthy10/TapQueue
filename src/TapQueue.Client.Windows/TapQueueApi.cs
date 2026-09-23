using System.Net;
using System.Net.Http.Json;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Client.Windows;

/// <summary>Talks to the TapQueue server, signing in again automatically if the session lapses.</summary>
public sealed class TapQueueApi(ClientConfig config) : IDisposable
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromMinutes(5),
    };

    public ClientSessionResponse? Session { get; private set; }

    public async Task<ClientSessionResponse> SignInAsync(CancellationToken ct = default)
    {
        var request = new ClientSessionRequest(
            config.EffectiveUsername,
            string.IsNullOrWhiteSpace(config.Token) ? null : config.Token.Trim(),
            Environment.UserName,
            Environment.MachineName,
            TapQueueVersion.Current);

        using var response = await _http.PostAsJsonAsync("api/v1/client/session", request, TapQueueJson.Options, ct);
        await EnsureSuccessAsync(response, ct);
        Session = (await response.Content.ReadFromJsonAsync<ClientSessionResponse>(TapQueueJson.Options, ct))!;
        return Session;
    }

    /// <summary>Keeps the session alive. Null from servers older than 0.2 (they reply 204).</summary>
    public Task<ClientHeartbeatResponse?> HeartbeatAsync(CancellationToken ct = default) =>
        SendAsync<ClientHeartbeatResponse>(HttpMethod.Post, "api/v1/client/heartbeat", null, ct);

    /// <summary>Downloads a client build (<see cref="ClientBuildDto.DownloadPath"/>) into <paramref name="destination"/>.</summary>
    public async Task DownloadAsync(string path, Stream destination, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Get, path.TrimStart('/'), null, HttpCompletionOption.ResponseHeadersRead, ct);
        await response.Content.CopyToAsync(destination, ct);
    }

    public async Task<List<JobDto>> GetHeldJobsAsync(CancellationToken ct = default) =>
        await SendAsync<List<JobDto>>(HttpMethod.Get, "api/v1/me/jobs", null, ct) ?? [];

    public async Task<List<PrinterDto>> GetPrintersAsync(CancellationToken ct = default) =>
        await SendAsync<List<PrinterDto>>(HttpMethod.Get, "api/v1/printers", null, ct) ?? [];

    public async Task<ReleaseResponse> ReleaseAsync(string printerId, IReadOnlyList<long>? jobIds, CancellationToken ct = default) =>
        (await SendAsync<ReleaseResponse>(HttpMethod.Post, "api/v1/me/release", new ReleaseRequest(printerId, jobIds), ct))!;

    public Task CancelJobAsync(long jobId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/v1/me/jobs/{jobId}", null, ct);

    public Uri IppUrl(QueueDto queue) => new(_http.BaseAddress!, queue.IppPath.TrimStart('/'));

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, body, HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
            return default;
        return await response.Content.ReadFromJsonAsync<T>(TapQueueJson.Options, ct);
    }

    /// <summary>Sends with the session token, signing in again once if the session has lapsed. Throws on errors.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, HttpCompletionOption completion, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (Session is null)
                await SignInAsync(ct);
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new("Bearer", Session!.SessionToken);
            if (body is not null)
                request.Content = JsonContent.Create(body, body.GetType(), options: TapQueueJson.Options);
            var response = await _http.SendAsync(request, completion, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                await SignInAsync(ct);
                continue;
            }
            try
            {
                await EnsureSuccessAsync(response, ct);
            }
            catch
            {
                response.Dispose();
                throw;
            }
            return response;
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

public sealed class TapQueueApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
