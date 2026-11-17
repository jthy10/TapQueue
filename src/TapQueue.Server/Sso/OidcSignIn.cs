using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TapQueue.Server.Data;
using TapQueue.Shared;
using TapQueue.Shared.Api;

namespace TapQueue.Server.Sso;

/// <summary>How single sign-on is set up. Kept in the database (settings key "sso"); the client secret never leaves the server.</summary>
/// <param name="Authority">
/// The provider's issuer address, the one that has <c>/.well-known/openid-configuration</c> under it:
/// <c>https://login.microsoftonline.com/&lt;tenant-id&gt;/v2.0</c> for Entra ID, <c>https://accounts.google.com</c>,
/// <c>https://&lt;org&gt;.okta.com</c>.
/// </param>
/// <param name="UsernameClaim">The claim in the ID token that says who someone is, like jake@example.org.</param>
/// <param name="Label">What the button says after "Sign in with".</param>
/// <param name="Domain">
/// The organisation's own domain, like example.org: someone the provider calls jake@example.org is the
/// TapQueue user jake. Empty: a name has to be a TapQueue username exactly. Without this, anyone a
/// provider lets in (a guest, or any account at a public provider) whose name starts the same way
/// would be taken for that user.
/// </param>
public sealed record SsoSettings(bool Enabled = false, string Authority = "", string ClientId = "", string ClientSecret = "",
    string UsernameClaim = "preferred_username", string Label = "single sign-on", string Domain = "")
{
    public bool Usable => Enabled && Authority.Length > 0 && ClientId.Length > 0;

    public SsoSettingsDto ToDto(string redirectUri) => new(Enabled, Authority, ClientId, ClientSecret.Length > 0, UsernameClaim, Label, redirectUri, Domain);

    /// <summary>The TapQueue usernames <paramref name="name"/> (what the provider calls someone) may be: itself, and its first part when it's in <see cref="Domain"/>.</summary>
    public IEnumerable<string> UsernamesFor(string name)
    {
        yield return name;
        var at = name.LastIndexOf('@');
        if (Domain.Length > 0 && at > 0 && string.Equals(name[(at + 1)..], Domain, StringComparison.OrdinalIgnoreCase))
            yield return name[..at];
    }
}

public sealed class SsoStore(Database database)
{
    private const string Key = "sso";

    public SsoSettings Get() =>
        database.Scalar("SELECT value FROM settings WHERE key = $k", ("$k", Key)) is string json
            ? JsonSerializer.Deserialize<SsoSettings>(json, TapQueueJson.Options) ?? new SsoSettings()
            : new SsoSettings();

    public void Save(SsoSettings settings) =>
        database.Execute("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v",
            ("$k", Key), ("$v", JsonSerializer.Serialize(settings, TapQueueJson.Options)));
}

/// <summary>Single sign-on didn't work; the message is for the person trying (or the admin setting it up).</summary>
public sealed class SsoException(string message) : Exception(message);

/// <summary>Who the provider says signed in.</summary>
/// <param name="Name">The value of the username claim, like jake@example.org.</param>
/// <param name="ObjectId">Entra ID's object id for them (the <c>oid</c> claim), which is what a directory sync links users by.</param>
public sealed record SsoIdentity(string Name, string? ObjectId, string Target);

/// <summary>
/// Single sign-on with OpenID Connect, for the admin console and the web page: the authorization code
/// flow with PKCE, against any provider that publishes its configuration (Entra ID, Google, Okta,
/// Keycloak…). TapQueue sends the browser to the provider, and takes back an ID token that it checks
/// itself: the provider's signature (RS256), who issued it, that it was issued to this TapQueue, that
/// it hasn't expired, and that it answers the sign-in this browser started. Who that person is in
/// TapQueue, and what they may do, is decided here as for any other sign-in; the provider only says who they are.
/// </summary>
public sealed class OidcSignIn(SsoStore store, OutboundHttp outbound, ILogger<OidcSignIn> logger)
{
    public const string ConsoleTarget = "console";
    public const string PortalTarget = "portal";

    private static readonly TimeSpan PendingFor = TimeSpan.FromMinutes(10);
    private const int MaxPending = 5000;

    private sealed record Pending(string Nonce, string Verifier, string Target, string RedirectUri, string Authority, DateTimeOffset At);

    private sealed record Provider(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string JwksUri, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, Provider> _providers = new();
    private readonly ConcurrentDictionary<string, (JsonElement Keys, DateTimeOffset At)> _keys = new();

    /// <summary>Starts a sign-in: where to send the browser, and the state it must bring back.</summary>
    public async Task<(string Url, string State)> StartAsync(string target, string redirectUri, CancellationToken ct)
    {
        var settings = store.Get();
        if (!settings.Usable)
            throw new SsoException("Single sign-on isn't set up on this TapQueue server.");
        var provider = await ProviderAsync(settings.Authority, ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var (old, pending) in _pending)
            if (now - pending.At > PendingFor)
                _pending.TryRemove(old, out _);
        if (_pending.Count >= MaxPending)
            throw new SsoException("Too many sign-ins are under way. Try again in a few minutes.");

        var state = Tokens.New();
        var verifier = Tokens.New() + Tokens.New();
        var nonce = Tokens.New();
        _pending[state] = new Pending(nonce, verifier, target, redirectUri, settings.Authority, now);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = settings.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid profile email",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var url = provider.AuthorizationEndpoint + (provider.AuthorizationEndpoint.Contains('?') ? "&" : "?") +
                  string.Join("&", query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return (url, state);
    }

    /// <summary>The browser came back with a code: trades it for an ID token and checks that.</summary>
    public async Task<SsoIdentity> FinishAsync(string state, string code, CancellationToken ct)
    {
        if (!_pending.TryRemove(state, out var pending) || DateTimeOffset.UtcNow - pending.At > PendingFor)
            throw new SsoException("That sign-in took too long, or was already used. Try again.");
        var settings = store.Get();
        if (!settings.Usable || settings.Authority != pending.Authority)
            throw new SsoException("Single sign-on was changed while you were signing in. Try again.");
        var provider = await ProviderAsync(settings.Authority, ct);

        JsonElement tokens;
        try
        {
            using var http = outbound.Create();
            using var response = await http.PostAsync(provider.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = pending.RedirectUri,
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
                ["code_verifier"] = pending.Verifier,
            }), ct);
            tokens = await JsonAsync(response, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The single sign-on provider refused the code: {Error}", Text(tokens, "error_description") ?? Text(tokens, "error") ?? response.StatusCode.ToString());
                throw new SsoException("The sign-in provider didn't accept the sign-in. If this keeps happening, the client secret may be wrong or expired.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new SsoException($"Couldn't reach the sign-in provider: {ex.Message}");
        }
        if (Text(tokens, "id_token") is not { } idToken)
            throw new SsoException("The sign-in provider sent no ID token.");

        var claims = await ValidateAsync(idToken, provider, settings, pending.Nonce, ct);
        var name = Text(claims, settings.UsernameClaim) ?? Text(claims, "preferred_username") ?? Text(claims, "upn") ?? Text(claims, "email");
        if (string.IsNullOrWhiteSpace(name))
            throw new SsoException($"The sign-in provider didn't say who you are (no \"{settings.UsernameClaim}\" claim). Ask your TapQueue admin.");
        return new SsoIdentity(name.Trim(), Text(claims, "oid"), pending.Target);
    }

    /// <summary>Checks that the provider can be reached and publishes what's needed; for the settings page.</summary>
    public async Task<string> TestAsync(string authority, CancellationToken ct)
    {
        _providers.TryRemove(authority, out _);
        var provider = await ProviderAsync(authority, ct);
        return $"Reached the provider: {provider.Issuer}";
    }

    private async Task<JsonElement> ValidateAsync(string idToken, Provider provider, SsoSettings settings, string nonce, CancellationToken ct)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3)
            throw new SsoException("The sign-in provider's ID token isn't one.");
        JsonElement header, claims;
        byte[] signature;
        try
        {
            header = JsonDocument.Parse(FromBase64Url(parts[0])).RootElement;
            claims = JsonDocument.Parse(FromBase64Url(parts[1])).RootElement;
            signature = FromBase64Url(parts[2]);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new SsoException("The sign-in provider's ID token isn't one.");
        }
        if (Text(header, "alg") != "RS256")
            throw new SsoException($"The sign-in provider signs with {Text(header, "alg") ?? "nothing"}; TapQueue checks RS256 signatures only.");

        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var kid = Text(header, "kid");
        // A key we haven't seen may be one the provider just started signing with: ask once more.
        if (!Verify(await KeysAsync(provider, refresh: false, ct), kid, signed, signature) &&
            !Verify(await KeysAsync(provider, refresh: true, ct), kid, signed, signature))
            throw new SsoException("The ID token isn't signed by the sign-in provider.");

        var now = DateTimeOffset.UtcNow;
        var skew = TimeSpan.FromMinutes(2);
        if (Text(claims, "iss") != provider.Issuer)
            throw new SsoException("The ID token comes from a different provider than the one set up here.");
        var audience = claims.TryGetProperty("aud", out var aud)
            ? aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().Select(a => a.GetString()).ToList() : [aud.GetString()]
            : [];
        if (!audience.Contains(settings.ClientId))
            throw new SsoException("The ID token was issued to another application, not this TapQueue.");
        if (!claims.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var expires) || DateTimeOffset.FromUnixTimeSeconds(expires) < now - skew)
            throw new SsoException("The sign-in has expired. Try again.");
        if (claims.TryGetProperty("nbf", out var nbf) && nbf.TryGetInt64(out var notBefore) && DateTimeOffset.FromUnixTimeSeconds(notBefore) > now + skew)
            throw new SsoException("The ID token isn't valid yet. Check the server's clock.");
        if (Text(claims, "nonce") is not { } tokenNonce || !Tokens.FixedTimeEquals(tokenNonce, nonce))
            throw new SsoException("The ID token doesn't belong to this sign-in. Try again.");
        return claims;
    }

    private static bool Verify(JsonElement keys, string? kid, byte[] signed, byte[] signature)
    {
        if (!keys.TryGetProperty("keys", out var list) || list.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var key in list.EnumerateArray())
        {
            if (Text(key, "kty") != "RSA" || kid is not null && Text(key, "kid") is { } keyId && keyId != kid)
                continue;
            if (Text(key, "n") is not { } n || Text(key, "e") is not { } e)
                continue;
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters { Modulus = FromBase64Url(n), Exponent = FromBase64Url(e) });
                if (rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    return true;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Not a key we can use; try the next.
            }
        }
        return false;
    }

    private async Task<Provider> ProviderAsync(string authority, CancellationToken ct)
    {
        if (_providers.TryGetValue(authority, out var known) && DateTimeOffset.UtcNow - known.At < TimeSpan.FromHours(6))
            return known;
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new SsoException("The single sign-on provider's address must start with https://.");
        try
        {
            using var http = outbound.Create();
            using var response = await http.GetAsync(authority.TrimEnd('/') + "/.well-known/openid-configuration", ct);
            var json = await JsonAsync(response, ct);
            if (!response.IsSuccessStatusCode || Text(json, "issuer") is not { } issuer || Text(json, "authorization_endpoint") is not { } authorize
                || Text(json, "token_endpoint") is not { } token || Text(json, "jwks_uri") is not { } jwks)
                throw new SsoException($"{authority} doesn't publish an OpenID Connect configuration (/.well-known/openid-configuration). Check the address.");
            if (new[] { authorize, token, jwks }.Any(u => !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                throw new SsoException("The single sign-on provider's configuration has addresses that aren't https://.");
            if (issuer.Contains('{'))
                throw new SsoException("That address is for every Microsoft tenant at once. Use your own tenant's: https://login.microsoftonline.com/<tenant-id>/v2.0");
            return _providers[authority] = new Provider(issuer, authorize, token, jwks, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new SsoException($"Couldn't reach the single sign-on provider at {uri.Host}: {ex.Message}");
        }
    }

    private async Task<JsonElement> KeysAsync(Provider provider, bool refresh, CancellationToken ct)
    {
        if (!refresh && _keys.TryGetValue(provider.JwksUri, out var known) && DateTimeOffset.UtcNow - known.At < TimeSpan.FromHours(6))
            return known.Keys;
        try
        {
            using var http = outbound.Create();
            using var response = await http.GetAsync(provider.JwksUri, ct);
            var keys = await JsonAsync(response, ct);
            _keys[provider.JwksUri] = (keys, DateTimeOffset.UtcNow);
            return keys;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new SsoException($"Couldn't get the sign-in provider's signing keys: {ex.Message}");
        }
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return json.RootElement.Clone();
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
