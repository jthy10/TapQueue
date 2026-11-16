using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace TapQueue.Shared;

/// <summary>
/// Signed releases. PCs and stations install new builds from their TapQueue server by themselves, as
/// SYSTEM or root, so the server must only be able to pass TapQueue's own releases on. Each release
/// lists its programs' SHA-256s (<see cref="ReleaseCheck.ProgramsFile"/>), and that list is signed
/// where the release is built, with a key no TapQueue server ever has
/// (<see cref="SignatureFile"/>). The server hands the list and its signature out with the build,
/// and the PC or station checks them itself against the public keys it was built with: no way out to
/// the internet needed, and a server that was taken over still can't run a program of its own.
///
/// A build that comes without a signature (an older release, or a TapQueue built without a release
/// key) is checked the older way, by asking where releases are published (<see cref="ReleaseCheck"/>).
/// The signature is ECDSA P-256 with SHA-256 over the list's bytes, as
/// <c>openssl dgst -sha256 -sign</c> writes it, in base64.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>The release file holding the signature of <see cref="ReleaseCheck.ProgramsFile"/>. scripts/sign-release.sh writes it.</summary>
    public const string SignatureFile = ReleaseCheck.ProgramsFile + ".sig";

    private const string KeysResource = "TapQueue.Shared.release-keys.txt";

    /// <summary>
    /// The keys TapQueue's own releases are signed with (base64 SubjectPublicKeyInfo), from
    /// release-keys.txt, which is built into every program. More than one while a key is being
    /// replaced; none in a TapQueue nobody has made a release key for.
    /// </summary>
    public static IReadOnlyList<string> ReleaseKeys { get; } = LoadKeys();

    private static List<string> LoadKeys()
    {
        using var stream = typeof(ReleaseSignature).Assembly.GetManifestResourceStream(KeysResource);
        if (stream is null)
            return [];
        using var reader = new StreamReader(stream);
        return ParseKeys(reader.ReadToEnd());
    }

    /// <summary>One key a line; blank lines and lines starting with # are skipped.</summary>
    public static List<string> ParseKeys(string text) =>
        text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).ToList();

    /// <summary>The release keys plus <paramref name="updateKey"/> from a config file, for a site that signs its own builds.</summary>
    public static IReadOnlyList<string> TrustedKeys(string? updateKey) =>
        string.IsNullOrWhiteSpace(updateKey) ? ReleaseKeys : [.. ReleaseKeys, updateKey.Trim()];

    /// <summary>Whether <paramref name="signature"/> (base64) is the signature of <paramref name="list"/> by one of <paramref name="keys"/>.</summary>
    public static bool IsValid(string? list, string? signature, IEnumerable<string> keys)
    {
        if (string.IsNullOrEmpty(list) || string.IsNullOrWhiteSpace(signature))
            return false;
        byte[] signed;
        try
        {
            signed = Convert.FromBase64String(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        var data = Encoding.UTF8.GetBytes(list);
        foreach (var key in keys)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.VerifyData(data, signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                    return true;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                // Not a key (a typo in update_key): it vouches for nothing.
            }
        }
        return false;
    }

    public enum Verdict
    {
        /// <summary>The list is signed with a trusted key and has this program in it: install it.</summary>
        Good,
        /// <summary>There's no signature to go by (or no key to check one with): ask where releases are published instead.</summary>
        Unsigned,
        /// <summary>There is a signature and it's wrong, or the signed list doesn't have this program: never install it.</summary>
        Refused,
    }

    /// <summary>What a build's signed list says about installing it, and for <see cref="Verdict.Refused"/> why not.</summary>
    /// <param name="what">"client" or "station build", for the message.</param>
    public static (Verdict Verdict, string? Problem) Check(string what, string version, string program, string sha256, string? list, string? signature,
        IReadOnlyList<string> keys)
    {
        if (string.IsNullOrEmpty(list) || string.IsNullOrWhiteSpace(signature) || keys.Count == 0)
            return (Verdict.Unsigned, null);
        if (!IsValid(list, signature, keys))
            return (Verdict.Refused, $"The server's {what} {version} comes with a signature this computer doesn't trust, so it wasn't installed.");
        return ReleaseCheck.Lists(list, program, sha256)
            ? (Verdict.Good, null)
            : (Verdict.Refused, $"The server's {what} {version} isn't the {program} in the signed release it came with, so it wasn't installed.");
    }
}
