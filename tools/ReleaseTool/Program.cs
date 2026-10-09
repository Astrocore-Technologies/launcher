using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ProjectG.Launcher.Core;

// Keys are generated only on explicit request. The private key must never ship with a launcher or release.
try
{
    if (args.Length == 2 && args[0] == "keygen")
    {
        var directory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteNew(Path.Combine(directory, "release.private.pem"), System.Text.Encoding.UTF8.GetBytes(key.ExportECPrivateKeyPem()));
        WriteNew(Path.Combine(directory, "release.public.pem"), System.Text.Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem()));
        Console.WriteLine("Ключи созданы. Закрытый ключ храните отдельно от публикуемых файлов.");
        return 0;
    }
    if (args.Length == 8 && args[0] == "pack")
    {
        var source = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);
        var version = args[3];
        var protocol = int.Parse(args[4]);
        var executable = SafePaths.Relative(args[5]);
        var downloadUrl = args[6];
        var privateKeyPath = Path.GetFullPath(args[7]);
        var sourcePrefix = source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (output.Equals(source, StringComparison.OrdinalIgnoreCase) || output.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) ||
            privateKeyPath.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Результат упаковки и закрытый ключ должны находиться вне исходной папки игры.");
        if (!File.Exists(SafePaths.Under(source, executable))) throw new FileNotFoundException("В экспорте отсутствует клиент.");
        var manifest = new ReleaseManifest(1, version, protocol, downloadUrl, new string('0', 64), 1, executable);
        manifest.Validate();
        using var key = ECDsa.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
        if (key.KeySize != 256) throw new InvalidDataException("Ожидается ECDSA P-256.");
        // Reject links and accidentally included signing keys before creating the archive.
        var files = Collect(source).ToArray();
        Directory.CreateDirectory(output);
        var archive = Path.Combine(output, "client.zip");
        using (var stream = new FileStream(archive, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            foreach (var file in files)
                zip.CreateEntryFromFile(file, SafePaths.Relative(Path.GetRelativePath(source, file).Replace('\\', '/')), CompressionLevel.Optimal);
        manifest = manifest with { Sha256 = await UpdateService.HashAsync(archive, CancellationToken.None), Size = new FileInfo(archive).Length };
        manifest.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonDefaults.Options);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        WriteNew(Path.Combine(output, "manifest.json"), bytes);
        WriteNew(Path.Combine(output, "manifest.json.sig"), signature);
        Console.WriteLine("Готово: client.zip, manifest.json, manifest.json.sig. Публикация не выполнялась.");
        return 0;
    }
    Console.Error.WriteLine("keygen <private-directory>\npack <game-export> <output> <version> <protocol> <relative.exe> <https-archive-url> <private-key.pem>");
    return 2;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }

static void WriteNew(string path, byte[] bytes)
{
    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
    file.Write(bytes); file.Flush(true);
}
static IEnumerable<string> Collect(string directory)
{
    SafePaths.NoLinks(directory);
    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
    {
        SafePaths.NoLinks(entry);
        if (Directory.Exists(entry)) { foreach (var file in Collect(entry)) yield return file; }
        else
        {
            if (entry.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
                entry.EndsWith(".key", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("В экспорт попал файл ключа.");
            yield return entry;
        }
    }
}
