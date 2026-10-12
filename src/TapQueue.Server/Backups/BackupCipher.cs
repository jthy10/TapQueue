using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TapQueue.Server.Backups;

/// <summary>
/// Encrypts a backup with a passphrase (backup.passphrase in server.toml), so a copy on a share or a
/// laptop doesn't hand over the password hashes and the server's private key. The key comes from the
/// passphrase by PBKDF2-SHA-256; the file is AES-256-GCM in 64 KB pieces, each with its own tag, so
/// a changed, cut short or reordered file is refused rather than restored.
///
/// Layout: the magic line, a 16-byte salt, the PBKDF2 round count and a 4-byte nonce prefix, then
/// pieces of [length][ciphertext][tag]. A piece's nonce is the prefix and its number; whether it's
/// the last one is authenticated with it, which is what catches a file with its end missing.
/// </summary>
public static class BackupCipher
{
    /// <summary>Added to an encrypted backup's file name.</summary>
    public const string Extension = ".enc";

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("TQBACKUP1\n");
    private const int Iterations = 600_000;
    private const int MaxIterations = 20_000_000;
    private const int PieceBytes = 64 * 1024;
    private const int TagBytes = 16;

    public static bool IsEncrypted(string path)
    {
        using var file = File.OpenRead(path);
        var magic = new byte[Magic.Length];
        return file.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) == magic.Length && magic.AsSpan().SequenceEqual(Magic);
    }

    public static void Encrypt(Stream plain, Stream output, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var prefix = RandomNumberGenerator.GetBytes(4);
        output.Write(Magic);
        output.Write(salt);
        WriteInt(output, Iterations);
        output.Write(prefix);

        using var aes = new AesGcm(KeyFrom(passphrase, salt, Iterations), TagBytes);
        var buffer = new byte[PieceBytes];
        var next = new byte[PieceBytes];
        var cipher = new byte[PieceBytes];
        var tag = new byte[TagBytes];
        var read = plain.ReadAtLeast(buffer, PieceBytes, throwOnEndOfStream: false);
        for (long piece = 0; ; piece++)
        {
            // Read ahead, to know whether this piece is the last.
            var readNext = read == PieceBytes ? plain.ReadAtLeast(next, PieceBytes, throwOnEndOfStream: false) : 0;
            var last = readNext == 0;
            aes.Encrypt(Nonce(prefix, piece), buffer.AsSpan(0, read), cipher.AsSpan(0, read), tag, [(byte)(last ? 1 : 0)]);
            WriteInt(output, read);
            output.Write(cipher, 0, read);
            output.Write(tag);
            if (last)
                return;
            (buffer, next, read) = (next, buffer, readNext);
        }
    }

    /// <exception cref="InvalidDataException">Not an encrypted backup, the wrong passphrase, or a damaged file.</exception>
    public static void Decrypt(Stream input, Stream plain, string passphrase)
    {
        var header = new byte[Magic.Length + 16 + 4 + 4];
        if (input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("it isn't an encrypted TapQueue backup.");
        var salt = header.AsSpan(Magic.Length, 16).ToArray();
        var iterations = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(Magic.Length + 16));
        var prefix = header.AsSpan(Magic.Length + 20, 4).ToArray();
        if (iterations is < 1 or > MaxIterations)
            throw new InvalidDataException("its header is damaged.");

        using var aes = new AesGcm(KeyFrom(passphrase, salt, iterations), TagBytes);
        var cipher = new byte[PieceBytes];
        var buffer = new byte[PieceBytes];
        var tag = new byte[TagBytes];
        var lengthBytes = new byte[4];
        for (long piece = 0; ; piece++)
        {
            if (input.ReadAtLeast(lengthBytes, 4, throwOnEndOfStream: false) < 4)
                throw new InvalidDataException("it's cut short.");
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length is < 0 or > PieceBytes
                || input.ReadAtLeast(cipher.AsSpan(0, length), length, throwOnEndOfStream: false) < length
                || input.ReadAtLeast(tag, TagBytes, throwOnEndOfStream: false) < TagBytes)
                throw new InvalidDataException("it's cut short or damaged.");
            // Only a full piece can have more after it.
            var last = length < PieceBytes || input.ReadByte() is var peek && Unread(input, peek);
            try
            {
                aes.Decrypt(Nonce(prefix, piece), cipher.AsSpan(0, length), tag, buffer.AsSpan(0, length), [(byte)(last ? 1 : 0)]);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new InvalidDataException(piece == 0 ? "the passphrase is wrong (or the file is damaged)." : "it's damaged.");
            }
            plain.Write(buffer, 0, length);
            if (!last)
                continue;
            if (input.ReadByte() >= 0)
                throw new InvalidDataException("it has something after its end.");
            return;
        }

        // True if the stream ended at the byte just read; otherwise steps back so the next piece starts where it should.
        static bool Unread(Stream stream, int peeked)
        {
            if (peeked < 0)
                return true;
            stream.Position--;
            return false;
        }
    }

    private static byte[] KeyFrom(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] Nonce(byte[] prefix, long piece)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), piece);
        return nonce;
    }

    private static void WriteInt(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        output.Write(bytes);
    }
}
