using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ProjectG.Launcher.Core;
using Xunit;

namespace Launcher.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "project-g-launcher-tests", Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Dictionary<string, byte[]> _responses = new();
    private readonly SecureHttp _http;
    private LauncherSettings Settings => new()
    {
        ManifestUrl = "https://updates.example.test/manifest.json", PublicKeyPem = _key.ExportSubjectPublicKeyInfoPem(),
        InstallDirectory = Path.Combine(_root, "install")
    };
    public UpdateTests() { Directory.CreateDirectory(_root); _http = new SecureHttp(new Handler(_responses)); }

    [Fact]
    public async Task InstallVerifyUpdateRollback()
    {
        Publish("1.0.0", "first");
        var updater = new UpdateService(_http);
        await updater.InstallAsync(Settings, null, CancellationToken.None);
        await UpdateService.VerifyAsync(Settings.InstallDirectory, CancellationToken.None);
        var first = UpdateService.ReadPointer(Settings.InstallDirectory)!.Current;
        Publish("1.1.0", "second");
        await updater.InstallAsync(Settings, null, CancellationToken.None);
        Assert.Equal("1.1.0", UpdateService.ReadInstalled(Settings.InstallDirectory)!.Manifest.Version);
        Assert.Equal(first, UpdateService.ReadPointer(Settings.InstallDirectory)!.Previous);
        await UpdateService.RollbackAsync(Settings.InstallDirectory, CancellationToken.None);
        Assert.Equal(first, UpdateService.ReadPointer(Settings.InstallDirectory)!.Current);
        await UpdateService.VerifyAsync(Settings.InstallDirectory, CancellationToken.None);
    }

    [Fact]
    public async Task CorruptDownloadKeepsPreviousRelease()
    {
        Publish("1.0.0", "first");
        var updater = new UpdateService(_http);
        await updater.InstallAsync(Settings, null, CancellationToken.None);
        var first = UpdateService.ReadPointer(Settings.InstallDirectory);
        Publish("1.1.0", "second");
        _responses["/client.zip"][0] ^= 0xFF;
        await Assert.ThrowsAsync<CryptographicException>(() => updater.InstallAsync(Settings, null, CancellationToken.None));
        Assert.Equal(first, UpdateService.ReadPointer(Settings.InstallDirectory));
        await UpdateService.VerifyAsync(Settings.InstallDirectory, CancellationToken.None);
    }

    [Fact]
    public async Task TamperedSignatureCannotInstall()
    {
        Publish("1.0.0", "first");
        _responses["/manifest.json.sig"][0] ^= 0xFF;
        await Assert.ThrowsAsync<CryptographicException>(() => new UpdateService(_http).InstallAsync(Settings, null, CancellationToken.None));
        Assert.Null(UpdateService.ReadPointer(Settings.InstallDirectory));
    }

    [Fact]
    public async Task WrongPublisherKeyIsRejected()
    {
        Publish("1.0.0", "first");
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAsync<CryptographicException>(() => new UpdateService(_http).CheckAsync(
            Settings with { PublicKeyPem = other.ExportSubjectPublicKeyInfoPem() }, CancellationToken.None));
    }

    [Fact]
    public async Task InterruptedBodyCannotSelectPartialRelease()
    {
        Publish("1.0.0", "first");
        _responses["/client.zip"] = _responses["/client.zip"][..10];
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateService(_http).InstallAsync(Settings, null, CancellationToken.None));
        Assert.Null(UpdateService.ReadPointer(Settings.InstallDirectory));
    }

    [Fact]
    public async Task CancellationPreservesCurrentPointer()
    {
        Publish("1.0.0", "first");
        var updater = new UpdateService(_http);
        await updater.InstallAsync(Settings, null, CancellationToken.None);
        var pointer = UpdateService.ReadPointer(Settings.InstallDirectory);
        Publish("1.1.0", "second");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value.Message.StartsWith("Распаковка")) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.InstallAsync(Settings, progress, cancellation.Token));
        Assert.Equal(pointer, UpdateService.ReadPointer(Settings.InstallDirectory));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("/escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("foo\\escape.exe")]
    [InlineData("game.exe:stream")]
    [InlineData("CON.exe")]
    [InlineData("dir/../escape.exe")]
    [InlineData("dir /file.exe")]
    [InlineData("dir./file.exe")]
    public async Task UnsafeZipPathsRejectedBeforeWriting(string path)
    {
        var archive = Path.Combine(_root, "bad.zip");
        await File.WriteAllBytesAsync(archive, Zip((path, "evil")));
        var destination = Path.Combine(_root, "extract");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.ExtractAsync(archive, destination, null, CancellationToken.None));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task CaseAliasesAreRejected()
    {
        var archive = Path.Combine(_root, "duplicate.zip");
        await File.WriteAllBytesAsync(archive, Zip(("Game.exe", "one"), ("game.exe", "two")));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.ExtractAsync(archive, Path.Combine(_root, "extract"), null, CancellationToken.None));
    }

    [Fact]
    public async Task SymlinkEntriesAreRejected()
    {
        var archive = Path.Combine(_root, "link.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("Game.exe");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(entry.Open()); writer.Write("../../outside");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.ExtractAsync(archive, Path.Combine(_root, "extract"), null, CancellationToken.None));
    }

    [Fact]
    public async Task LocalCorruptionAndExtraFilesAreDetected()
    {
        Publish("1.0.0", "first");
        await new UpdateService(_http).InstallAsync(Settings, null, CancellationToken.None);
        var id = UpdateService.ReadPointer(Settings.InstallDirectory)!.Current;
        var game = Path.Combine(Settings.InstallDirectory, "versions", id, "game");
        await File.WriteAllTextAsync(Path.Combine(game, "extra.dll"), "extra");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.VerifyAsync(Settings.InstallDirectory, CancellationToken.None));
        File.Delete(Path.Combine(game, "extra.dll"));
        await File.WriteAllTextAsync(Path.Combine(game, "ProjectG.exe"), "wrong");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.VerifyAsync(Settings.InstallDirectory, CancellationToken.None));
    }

    [Fact]
    public async Task MissingExecutableCannotReplaceCurrent()
    {
        Publish("1.0.0", "first");
        var updater = new UpdateService(_http);
        await updater.InstallAsync(Settings, null, CancellationToken.None);
        var pointer = UpdateService.ReadPointer(Settings.InstallDirectory);
        Publish("1.1.0", "bad", "Other.exe");
        await Assert.ThrowsAsync<InvalidDataException>(() => updater.InstallAsync(Settings, null, CancellationToken.None));
        Assert.Equal(pointer, UpdateService.ReadPointer(Settings.InstallDirectory));
    }

    [Fact]
    public async Task ConcurrentLauncherIsBlocked()
    {
        Publish("1.0.0", "first");
        Directory.CreateDirectory(Settings.InstallDirectory);
        using var busy = new FileStream(Path.Combine(Settings.InstallDirectory, ".launcher.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<IOException>(() => new UpdateService(_http).InstallAsync(Settings, null, CancellationToken.None));
    }

    [Fact]
    public async Task HttpDowngradeRedirectIsRejected()
    {
        using var http = new SecureHttp(new RedirectHandler());
        await Assert.ThrowsAsync<InvalidDataException>(() => http.ReadAsync(new Uri("https://example.test/manifest.json"), 100, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedManifestIsRejected()
    {
        _responses["/large"] = new byte[1024];
        await Assert.ThrowsAsync<InvalidDataException>(() => _http.ReadAsync(new Uri("https://example.test/large"), 32, CancellationToken.None));
    }

    private void Publish(string version, string contents, string executable = "ProjectG.exe")
    {
        var archive = Zip((executable, contents), ("data/world.pck", "world data"));
        var manifest = new ReleaseManifest(1, version, 27, "https://updates.example.test/client.zip", Convert.ToHexString(SHA256.HashData(archive)), archive.Length, "ProjectG.exe");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonDefaults.Options);
        _responses["/manifest.json"] = bytes;
        _responses["/manifest.json.sig"] = _key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        _responses["/client.zip"] = archive;
    }
    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var item in entries) { using var writer = new StreamWriter(zip.CreateEntry(item.Path).Open()); writer.Write(item.Content); }
        return output.ToArray();
    }
    public void Dispose()
    {
        _http.Dispose(); _key.Dispose();
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "project-g-launcher-tests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(_root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(_root, true);
    }
    private sealed class Handler(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(responses[request.RequestUri!.AbsolutePath]) });
        }
    }
    private sealed class RedirectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("http://example.test/insecure");
            return Task.FromResult(response);
        }
    }
    private sealed class InlineProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    { public void Report(UpdateProgress value) => report(value); }
}
