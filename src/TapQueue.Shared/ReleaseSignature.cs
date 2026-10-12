using System.Security.Cryptography;

namespace TapQueue.Shared;

/// <summary>
/// Signatures on the client and station programs that PCs and stations install by themselves. A
/// build is signed where it's made, with a key the TapQueue server never has, and each PC and
/// station checks the signature before installing: a server (or an admin of it) can then only hand
/// out builds whoever holds the key has signed, not programs of its own.
/// The signature is ECDSA P-256 over the program's SHA-256, as `openssl dgst -sha256 -sign` writes it.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>
    /// The keys TapQueue's own releases are signed with (base64 SubjectPublicKeyInfo). More than one
    /// while a key is being replaced. Builds made elsewhere are signed with their maker's key, which
    /// PCs and stations are given as update_key in client.toml and station.toml.
    /// </summary>
    public static readonly string[] ReleaseKeys =
    [
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEL/V8/FWqKyEd4Cvlh+BX98KjI4n9Jip6MAYiME7LRfxu9v/TS/CHYHLyfsxlZfNZP8vx3vUyLuYzzWx/56lqRw==",
    ];

    /// <summary>The release keys plus <paramref name="updateKey"/> from a config file, if it has one.</summary>
    public static IReadOnlyList<string> TrustedKeys(string? updateKey) =>
        string.IsNullOrWhiteSpace(updateKey) ? ReleaseKeys : [.. ReleaseKeys, updateKey.Trim()];

    /// <summary>Whether <paramref name="signature"/> (base64) is one of <paramref name="keys"/>' signature of the program with this SHA-256.</summary>
    public static bool IsValid(string sha256, string? signature, IEnumerable<string> keys)
    {
        if (string.IsNullOrWhiteSpace(signature) || sha256.Length != 64)
            return false;
        byte[] hash, signed;
        try
        {
            hash = Convert.FromHexString(sha256);
            signed = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return false;
        }
        foreach (var key in keys)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.VerifyHash(hash, signed, DSASignatureFormat.Rfc3279DerSequence))
                    return true;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                // Not a key (a typo in update_key): it vouches for nothing.
            }
        }
        return false;
    }

    /// <summary>Why a build offered for installing is refused, or null if its signature is good.</summary>
    /// <param name="what">"client" or "station build".</param>
    public static string? Refusal(string what, string version, string sha256, string? signature, IEnumerable<string> keys) =>
        IsValid(sha256, signature, keys) ? null
        : string.IsNullOrWhiteSpace(signature)
            ? $"The server's {what} {version} isn't signed, so it wasn't installed. Publish a signed release, or see \"Signed updates\" in the docs to sign your own builds."
            : $"The server's {what} {version} isn't signed with a key this computer trusts, so it wasn't installed.";
}
