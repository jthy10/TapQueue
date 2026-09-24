using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TapQueue.Server.ActiveDirectory;

/// <summary>
/// The file name OpenSSL looks a CA up by in a certificate directory (what `openssl x509 -hash` prints):
/// the first 4 bytes, little-endian, of the SHA-1 of the subject's canonical form. libldap built on
/// OpenSSL (Ubuntu 26.04) only finds CAs named &lt;hash&gt;.0; GnuTLS builds read every file.
/// </summary>
public static class OpenSslSubjectHash
{
    public static string Of(X509Certificate2 certificate) => Of(certificate.SubjectName);

    public static string Of(X500DistinguishedName name)
    {
        var digest = SHA1.HashData(Canonical(name.RawData));
        return BitConverter.ToUInt32(digest, 0).ToString(BitConverter.IsLittleEndian ? "x8" : throw new PlatformNotSupportedException());
    }

    /// <summary>
    /// OpenSSL's x509_name_canon: each RDN SET re-encoded with its string values as lower-cased
    /// UTF8String with whitespace trimmed and runs collapsed, concatenated without the outer SEQUENCE.
    /// </summary>
    private static byte[] Canonical(byte[] subject)
    {
        var output = new AsnWriter(AsnEncodingRules.DER);
        var rdns = new AsnReader(subject, AsnEncodingRules.DER).ReadSequence();
        while (rdns.HasData)
        {
            var set = rdns.ReadSetOf();
            using (output.PushSetOf())
            {
                while (set.HasData)
                {
                    var pair = set.ReadSequence();
                    using (output.PushSequence())
                    {
                        output.WriteObjectIdentifier(pair.ReadObjectIdentifier());
                        var tag = pair.PeekTag();
                        if (tag.TagClass == TagClass.Universal && CanonTypes.Contains((UniversalTagNumber)tag.TagValue))
                            output.WriteCharacterString(UniversalTagNumber.UTF8String, Normalize(pair.ReadCharacterString((UniversalTagNumber)tag.TagValue)));
                        else
                            output.WriteEncodedValue(pair.ReadEncodedValue().Span);
                    }
                }
            }
        }
        return output.Encode();
    }

    private static readonly UniversalTagNumber[] CanonTypes =
    [
        UniversalTagNumber.UTF8String, UniversalTagNumber.BMPString, UniversalTagNumber.UniversalString, UniversalTagNumber.PrintableString,
        UniversalTagNumber.T61String, UniversalTagNumber.IA5String, UniversalTagNumber.VisibleString,
    ];

    /// <summary>ASCII-only lower-casing and whitespace handling, as OpenSSL does.</summary>
    private static string Normalize(string value)
    {
        var s = new StringBuilder();
        var pendingSpace = false;
        foreach (var c in value.Trim(' ', '\t', '\n', '\r', '\f', '\v'))
        {
            if (c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v')
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace) s.Append(' ');
            pendingSpace = false;
            s.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
        }
        return s.ToString();
    }
}
