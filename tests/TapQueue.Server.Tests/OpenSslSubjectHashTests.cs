using System.Security.Cryptography.X509Certificates;
using TapQueue.Server.ActiveDirectory;

namespace TapQueue.Server.Tests;

public sealed class OpenSslSubjectHashTests
{
    /// <summary>Expected values from `openssl x509 -hash`: an AD-style CA (DC= parts are IA5String) and mixed case and spacing.</summary>
    [Theory]
    [InlineData("test-dc-ca.pem", "7e3c431a")]
    [InlineData("test-mixed-ca.pem", "d8dd51a3")]
    public void MatchesOpenSsl(string file, string expected)
    {
        var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)));
        Assert.Equal(expected, OpenSslSubjectHash.Of(certificate));
    }
}
