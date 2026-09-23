using System.Security.Cryptography;
using System.Text;

namespace TapQueue.Server.Data;

/// <summary>Random bearer tokens. Only their SHA-256 hashes are stored.</summary>
public static class Tokens
{
    public static string New() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
