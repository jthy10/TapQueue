using System.Security.Cryptography.X509Certificates;
using TapQueue.Server.ActiveDirectory;
using TapQueue.Server.Admin;

namespace TapQueue.Server.Tests;

/// <summary>Files the server writes or serves from folders it shares.</summary>
public sealed class FileSafetyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tapqueue-files").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static X509Certificate2 Fixture(string name) =>
        X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void TheAdCaFileIsOnlyRewrittenWhenTheCertificateChanges()
    {
        var folder = Path.Combine(_dir, "directory-ca");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "stale.0"), "old");

        LdapDirectorySourceFactory.WriteTrustedCa(folder, Fixture("test-dc-ca.pem"));
        var file = Path.Combine(folder, "7e3c431a.0");
        var written = File.GetLastWriteTimeUtc(file);
        File.SetLastWriteTimeUtc(file, written.AddMinutes(-5));
        LdapDirectorySourceFactory.WriteTrustedCa(folder, Fixture("test-dc-ca.pem"));
        var unchanged = File.GetLastWriteTimeUtc(file);
        LdapDirectorySourceFactory.WriteTrustedCa(folder, Fixture("test-mixed-ca.pem"));

        Assert.Equal(written.AddMinutes(-5), unchanged);
        Assert.Equal(["d8dd51a3.0"], Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public void TheConsoleFromDiskStaysInsideItsFolder()
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "wwwroot")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(_dir, "wwwroot-old")).FullName;
        File.WriteAllText(Path.Combine(root, "app.js"), "ok");
        File.WriteAllText(Path.Combine(sibling, "secret.js"), "no");

        using var inside = AdminUi.FromDisk(root, "app.js");
        using var outside = AdminUi.FromDisk(root, "../wwwroot-old/secret.js");

        Assert.NotNull(inside);
        Assert.Null(outside);
    }
}
