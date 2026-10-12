using TapQueue.Server.Backups;

namespace TapQueue.Server.Tests;

/// <summary>Encrypting backups with a passphrase.</summary>
public sealed class BackupCipherTests
{
    private const string Passphrase = "correct horse battery staple";

    private static byte[] Encrypt(byte[] plain)
    {
        var output = new MemoryStream();
        BackupCipher.Encrypt(new MemoryStream(plain), output, Passphrase);
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, string passphrase = Passphrase)
    {
        var output = new MemoryStream();
        BackupCipher.Decrypt(new MemoryStream(encrypted), output, passphrase);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]       // exactly one piece
    [InlineData(65537)]
    [InlineData(3 * 65536)]   // ends on a piece boundary
    [InlineData(200_000)]
    public void WhatGoesInComesOut(int length)
    {
        var plain = new byte[length];
        new Random(length).NextBytes(plain);

        var encrypted = Encrypt(plain);

        Assert.Equal(plain, Decrypt(encrypted));
        if (length > 16)
            Assert.Equal(-1, encrypted.AsSpan().IndexOf(plain.AsSpan(0, 16)));
    }

    [Fact]
    public void TheWrongPassphraseIsRefused() =>
        Assert.Contains("passphrase is wrong", Assert.Throws<InvalidDataException>(() => Decrypt(Encrypt(new byte[100]), "not the passphrase")).Message);

    [Fact]
    public void AChangedFileIsRefused()
    {
        var encrypted = Encrypt(new byte[200_000]);
        encrypted[^40] ^= 1;

        Assert.Throws<InvalidDataException>(() => Decrypt(encrypted));
    }

    [Theory]
    [InlineData(34 + 4 + 65536 + 16)]            // cut after the first whole piece
    [InlineData(34 + 2 * (4 + 65536 + 16) + 50)] // cut inside a piece
    public void AFileWithItsEndMissingIsRefused(int keep) =>
        Assert.Throws<InvalidDataException>(() => Decrypt(Encrypt(new byte[200_000])[..keep]));

    [Fact]
    public void SomethingAddedAfterTheEndIsRefused() =>
        Assert.Throws<InvalidDataException>(() => Decrypt([.. Encrypt(new byte[100]), 1, 2, 3]));
}
