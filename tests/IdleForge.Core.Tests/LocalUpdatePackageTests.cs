using IdleForge.Core.Updates;

namespace IdleForge.Core.Tests;

public sealed class LocalUpdatePackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IdleForge-update-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ManifestRoundTripValidatesEveryFile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "runtimes"));
        await File.WriteAllTextAsync(Path.Combine(_root, "IdleForge.App.exe"), "app-v2");
        await File.WriteAllTextAsync(Path.Combine(_root, "runtimes", "native.dll"), "native-v2");

        var created = await LocalUpdatePackage.CreateManifestAsync(_root, "2.3.4");
        var validated = await LocalUpdatePackage.ValidateAsync(_root);

        Assert.Equal("2.3.4", validated.Version);
        Assert.Equal(2, created.Files.Count);
        Assert.Contains(validated.Files, x => x.Path == "runtimes/native.dll");
    }

    [Fact]
    public async Task ModifiedPackageIsRejected()
    {
        Directory.CreateDirectory(_root);
        var app = Path.Combine(_root, "IdleForge.App.exe");
        await File.WriteAllTextAsync(app, "trusted");
        await LocalUpdatePackage.CreateManifestAsync(_root, "1.0.0");
        await File.WriteAllTextAsync(app, "modified");

        await Assert.ThrowsAsync<InvalidDataException>(() => LocalUpdatePackage.ValidateAsync(_root));
    }

    [Fact]
    public async Task PackageWithoutMainExecutableIsRejected()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "readme.txt"), "incomplete");
        await LocalUpdatePackage.CreateManifestAsync(_root, "1.0.0");

        await Assert.ThrowsAsync<InvalidDataException>(() => LocalUpdatePackage.ValidateAsync(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
