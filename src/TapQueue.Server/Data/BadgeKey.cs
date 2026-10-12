using System.Security.Cryptography;
using System.Text;
using TapQueue.Server.Config;

namespace TapQueue.Server.Data;

/// <summary>
/// The secret key card numbers are hashed with (HMAC-SHA-256). A card number is a handful of digits,
/// so a plain hash of one is found by trying them all; with the key, the hashes in the database (or
/// in a copy of it) say nothing without this file as well. It's 32 random bytes in
/// data_dir/secrets/badge.key, made on first start, readable by the server's user only, and it goes
/// into backups, because without it no stored card can be matched again.
/// </summary>
public sealed class BadgeKey
{
    /// <summary>The folder in data_dir the key lives in.</summary>
    public const string Folder = "secrets";

    /// <summary>What a keyed hash starts with; anything else is a plain SHA-256 from before 0.16.</summary>
    public const string Prefix = "h1:";

    private readonly byte[] _key;

    public BadgeKey(ServerConfig config) : this(Path.Combine(config.Server.DataDir, Folder, "badge.key"))
    {
    }

    public BadgeKey(string path)
    {
        if (!File.Exists(path))
        {
            Backups.BackupStore.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
            using var file = new FileStream(path, Backups.BackupStore.PrivateFile(FileMode.CreateNew));
            file.Write(RandomNumberGenerator.GetBytes(32));
        }
        _key = File.ReadAllBytes(path);
        if (_key.Length < 32)
            throw new InvalidDataException($"{path} isn't a badge key (it's {_key.Length} bytes, not 32). Restore it from a backup; without it, stored cards can't be matched.");
    }

    /// <summary>The hash a card number (already normalized) is stored under.</summary>
    public string Hash(string normalizedCard) => Upgrade(Tokens.Hash(normalizedCard));

    /// <summary>
    /// Turns a hash from before 0.16 into a keyed one. Keyed hashes are made from the old hash rather
    /// than the card number, so cards stored before can be converted without anyone tapping again.
    /// </summary>
    public string Upgrade(string plainHash) =>
        Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(plainHash)));
}
