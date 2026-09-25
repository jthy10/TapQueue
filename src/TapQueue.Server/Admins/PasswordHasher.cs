using System.Security.Cryptography;
using System.Text;

namespace TapQueue.Server.Admins;

/// <summary>
/// Admin console passwords for local users: PBKDF2-SHA256 with a random salt, stored as
/// "pbkdf2-sha256$iterations$salt$hash" so the work factor can go up later without breaking old hashes.
/// </summary>
public static class PasswordHasher
{
    public const int MinLength = 10;

    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Why a new password won't do; null if it's fine.</summary>
    public static string? Problem(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinLength ? $"Use a password of at least {MinLength} characters." : null;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, Iterations);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not [Scheme, var iterationsText, var saltText, var hashText] || !int.TryParse(iterationsText, out var iterations))
            return false;
        var expected = Convert.FromBase64String(hashText);
        return CryptographicOperations.FixedTimeEquals(Derive(password, Convert.FromBase64String(saltText), iterations), expected);
    }

    /// <summary>Does the same work as <see cref="Verify"/> for an unknown user, so a wrong name takes as long as a wrong password.</summary>
    public static void VerifyNothing(string password) => Derive(password, new byte[SaltBytes], Iterations);

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}
