using System.Security.Cryptography;
using System.Text;

namespace TapQueue.Server.Data;

/// <summary>Random bearer tokens. Only their SHA-256 hashes are stored.</summary>
public static class Tokens
{
    public static string New() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// A PC's print key, which its printers' IPP paths carry, made from its private key. Anyone on the
    /// PC can read a printer's path, so the print key only says which PC a job came from; it can't be
    /// turned back into the key that the PC's service checks in and vouches for tray apps with.
    /// </summary>
    public static string PrintKey(string workstationKey) =>
        Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes("tapqueue-print-key:" + workstationKey)));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
