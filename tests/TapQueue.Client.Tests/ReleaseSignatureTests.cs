using System.Security.Cryptography;
using TapQueue.Shared;

namespace TapQueue.Client.Tests;

public sealed class ReleaseSignatureTests
{
    private static readonly string Sha256 = Convert.ToHexStringLower(SHA256.HashData("a program"u8));

    private static (string Key, string Signature) Sign(string sha256)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(key.SignHash(Convert.FromHexString(sha256), DSASignatureFormat.Rfc3279DerSequence)));
    }

    [Fact]
    public void ASignatureIsGoodOnlyForItsProgramAndItsKey()
    {
        var (key, signature) = Sign(Sha256);
        var (otherKey, _) = Sign(Sha256);

        Assert.True(ReleaseSignature.IsValid(Sha256, signature, [key]));
        Assert.True(ReleaseSignature.IsValid(Sha256, signature, [otherKey, key]));
        Assert.False(ReleaseSignature.IsValid(Sha256, signature, [otherKey]));
        Assert.False(ReleaseSignature.IsValid(Convert.ToHexStringLower(SHA256.HashData("another program"u8)), signature, [key]));
        Assert.False(ReleaseSignature.IsValid(Sha256, signature, ReleaseSignature.ReleaseKeys));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void WhatIsNotASignatureVouchesForNothing(string? signature)
    {
        var (key, _) = Sign(Sha256);
        Assert.False(ReleaseSignature.IsValid(Sha256, signature, [key]));
    }

    [Fact]
    public void AKeyThatIsNotOneIsIgnored()
    {
        var (key, signature) = Sign(Sha256);
        Assert.True(ReleaseSignature.IsValid(Sha256, signature, ["a typo", "AAAA", key]));
        Assert.Equal(ReleaseSignature.ReleaseKeys.Length + 1, ReleaseSignature.TrustedKeys(" " + key + " ").Count);
        Assert.Equal(ReleaseSignature.ReleaseKeys, ReleaseSignature.TrustedKeys(""));
    }

    [Fact]
    public void TheReleaseKeysAreKeys()
    {
        foreach (var key in ReleaseSignature.ReleaseKeys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            Assert.Equal(256, ecdsa.KeySize);
        }
    }
}
