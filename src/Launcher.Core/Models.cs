using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProjectG.Launcher.Core;

// JSON is signed as exact UTF-8 bytes; reformatting a published manifest invalidates its signature.
public sealed record ReleaseManifest(int Schema, string Version, int Protocol, string DownloadUrl,
    string Sha256, long Size, string Executable)
{
    public const long MaximumArchiveSize = 8L * 1024 * 1024 * 1024;
    public void Validate()
    {
        if (Schema != 1 || string.IsNullOrWhiteSpace(Version) ||
            !Regex.IsMatch(Version, @"^\d+\.\d+\.\d+(?:\.\d+)?$") ||
            !System.Version.TryParse(Version, out _) || Protocol < 1 ||
            Size is <= 0 or > MaximumArchiveSize || Sha256 is null ||
            !Regex.IsMatch(Sha256, "^[A-Fa-f0-9]{64}$"))
            throw new InvalidDataException("Некорректный манифест релиза.");
        SecureHttp.ValidateUrl(new Uri(DownloadUrl, UriKind.Absolute));
        SafePaths.Relative(Executable);
        if (!Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ожидается Windows-клиент .exe.");
    }
}

public static class ManifestSignature
{
    public static ReleaseManifest Verify(byte[] json, byte[] signature, string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        if (key.KeySize != 256 || !key.VerifyData(json, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Подпись обновления недействительна.");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, JsonDefaults.Options)
            ?? throw new InvalidDataException("Пустой манифест.");
        manifest.Validate();
        return manifest;
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

public sealed record LauncherSettings
{
    public string ManifestUrl { get; init; } = "";
    public string PublicKeyPem { get; init; } = "";
    public string InstallDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectG", "Game");
    public string ServerHost { get; init; } = "127.0.0.1";
    public int ServerPort { get; init; } = 9050;

    public void Validate()
    {
        SecureHttp.ValidateUrl(new Uri(ManifestUrl, UriKind.Absolute));
        using var key = ECDsa.Create();
        key.ImportFromPem(PublicKeyPem);
        if (key.KeySize != 256) throw new InvalidDataException("Нужен открытый ключ ECDSA P-256.");
        if (!Path.IsPathFullyQualified(InstallDirectory) || ServerPort is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(ServerHost) || Uri.CheckHostName(ServerHost) == UriHostNameType.Unknown)
            throw new InvalidDataException("Проверьте путь установки и адрес сервера.");
    }
}

public sealed record InstalledFile(string Path, long Size, string Sha256);
public sealed record InstalledRelease(ReleaseManifest Manifest, List<InstalledFile> Files);
public sealed record InstallPointer(string Current, string? Previous);
public sealed record UpdateProgress(string Message, double? Fraction = null);
