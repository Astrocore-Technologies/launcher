using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProjectG.Launcher.Core;

public sealed class UpdateService(SecureHttp http)
{
    private const long MaximumExpandedSize = 20L * 1024 * 1024 * 1024;
    public async Task<ReleaseManifest> CheckAsync(LauncherSettings settings, CancellationToken token)
    {
        settings.Validate();
        var uri = new Uri(settings.ManifestUrl);
        var signatureUri = new UriBuilder(uri) { Path = uri.AbsolutePath + ".sig" }.Uri;
        var json = await http.ReadAsync(uri, 65536, token);
        var signature = await http.ReadAsync(signatureUri, 128, token);
        return ManifestSignature.Verify(json, signature, settings.PublicKeyPem);
    }

    public static InstallPointer? ReadPointer(string root)
    {
        SafePaths.NoLinks(root);
        var path = Path.Combine(root, "current.json");
        SafePaths.NoLinks(path);
        if (!File.Exists(path)) return null;
        var pointer = ReadJson<InstallPointer>(path);
        ValidateId(pointer.Current);
        if (pointer.Previous is not null) ValidateId(pointer.Previous);
        return pointer;
    }

    public static InstalledRelease? ReadInstalled(string root)
    {
        var pointer = ReadPointer(root);
        return pointer is null ? null : ReadRelease(root, pointer.Current);
    }

    private static void ValidateId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Повреждён указатель установленной сборки.");
    }

    private static InstalledRelease ReadRelease(string root, string id)
    {
        ValidateId(id);
        var release = ReadJson<InstalledRelease>(SafePaths.Under(root, $"versions/{id}/install.json"));
        release.Manifest.Validate();
        if (release.Files is null || release.Files.Count is < 1 or > 50000)
            throw new InvalidDataException("Повреждён список файлов установки.");
        return release;
    }

    private static T ReadJson<T>(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Файл состояния слишком большой.");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonDefaults.Options)
            ?? throw new InvalidDataException("Повреждён файл состояния установки.");
    }

    // A per-install lock prevents two launcher processes from racing the pointer or verifying while updating.
    private static FileStream Acquire(string root)
    {
        SafePaths.NoLinks(root);
        Directory.CreateDirectory(root);
        var path = SafePaths.Under(root, ".launcher.lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("Эта установка уже используется другим лаунчером или запущенной игрой.", ex); }
    }

    public async Task InstallAsync(LauncherSettings settings, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        // Fetch and authenticate again at installation time rather than trusting a stale UI result.
        var manifest = await CheckAsync(settings, token);
        var root = Path.GetFullPath(settings.InstallDirectory);
        using var installLock = Acquire(root);
        var previous = ReadPointer(root);
        var id = Guid.NewGuid().ToString("N");
        var staging = SafePaths.Under(root, $"staging/{id}");
        Directory.CreateDirectory(staging);
        var archive = Path.Combine(staging, "download.zip");
        var game = Path.Combine(staging, "game");
        progress?.Report(new("Скачивание обновления", 0));
        try
        {
            await http.DownloadAsync(new Uri(manifest.DownloadUrl), archive, manifest.Size, progress, token);
            progress?.Report(new("Проверка SHA-256"));
            if (!string.Equals(await HashAsync(archive, token), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("Контрольная сумма архива не совпадает. Установка отменена.");
            var files = await ExtractAsync(archive, game, progress, token);
            if (!File.Exists(SafePaths.Under(game, manifest.Executable)))
                throw new InvalidDataException("В архиве отсутствует исполняемый файл клиента.");
            await WriteJsonAsync(Path.Combine(staging, "install.json"), new InstalledRelease(manifest, files), token);
            File.Delete(archive);
            token.ThrowIfCancellationRequested();
            var destination = SafePaths.Under(root, $"versions/{id}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
            // All files are immutable and complete before this single atomic pointer replacement.
            // Interruption before replacement leaves the previous release selected and usable.
            await CommitPointerAsync(root, new(id, previous?.Current), token);
            progress?.Report(new("Обновление установлено", 1));
        }
        finally
        {
            // Only the random staging directory owned by this operation may be removed.
            if (Directory.Exists(staging))
            {
                try { DeleteStaging(root, id); }
                catch (IOException) { /* A locked staging file is harmless; never delete an installed release. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void DeleteStaging(string root, string id)
    {
        ValidateId(id);
        var directory = SafePaths.Under(root, $"staging/{id}");
        // Inspect all descendants before a recursive delete; links may not escape the owned directory.
        foreach (var child in Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            SafePaths.NoLinks(child);
        Directory.Delete(directory, true);
    }

    public static async Task<List<InstalledFile>> ExtractAsync(string archivePath, string destination,
        IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        SafePaths.NoLinks(destination);
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count is < 1 or > 50000) throw new InvalidDataException("Недопустимое количество файлов в ZIP.");
        long expanded = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate the entire archive before writing any file.
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.EndsWith('/') ? entry.FullName[..^1] : entry.FullName;
            SafePaths.Relative(name);
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & 0x400) != 0 || !seen.Add(name))
                throw new InvalidDataException("ZIP содержит ссылку, специальный файл или повторяющийся путь.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedSize) throw new InvalidDataException("Распакованная сборка превышает 20 ГБ.");
        }
        Directory.CreateDirectory(destination);
        var files = new List<InstalledFile>();
        var buffer = new byte[131072];
        long extracted = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var directory = entry.FullName.EndsWith('/');
            var path = SafePaths.Under(destination, directory ? entry.FullName[..^1] : entry.FullName);
            if (directory) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                length += read;
                if (length > entry.Length) throw new InvalidDataException("ZIP содержит неверный размер файла.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (length != entry.Length) throw new InvalidDataException("Файл ZIP обрезан.");
            await output.FlushAsync(token);
            output.Flush(true);
            files.Add(new(entry.FullName, length, Convert.ToHexString(hash.GetHashAndReset())));
            extracted += length;
            progress?.Report(new($"Распаковка: {files.Count} файлов", expanded == 0 ? 1 : (double)extracted / expanded));
        }
        return files;
    }

    public static async Task VerifyAsync(string root, CancellationToken token)
    {
        using var installLock = Acquire(root);
        var pointer = ReadPointer(root) ?? throw new InvalidOperationException("Игра ещё не установлена.");
        await VerifyReleaseAsync(root, pointer.Current, token);
    }

    private static async Task VerifyReleaseAsync(string root, string id, CancellationToken token)
    {
        var release = ReadRelease(root, id);
        var game = SafePaths.Under(root, $"versions/{id}/game");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in release.Files)
        {
            var path = SafePaths.Under(game, file.Path);
            if (!expected.Add(file.Path) || !File.Exists(path) || new FileInfo(path).Length != file.Size ||
                !string.Equals(await HashAsync(path, token), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Повреждён файл: {file.Path}. Установите сборку заново.");
        }
        if (!expected.Contains(release.Manifest.Executable)) throw new InvalidDataException("Нет клиента в списке файлов.");
        foreach (var path in Directory.EnumerateFileSystemEntries(game)) CheckTree(path);
        void CheckTree(string path)
        {
            SafePaths.NoLinks(path);
            if (Directory.Exists(path))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) CheckTree(child);
            }
            else if (!expected.Contains(Path.GetRelativePath(game, path).Replace('\\', '/')))
                throw new InvalidDataException("В каталоге игры обнаружен посторонний файл. Переустановите сборку.");
        }
    }

    public static async Task RollbackAsync(string root, CancellationToken token)
    {
        using var installLock = Acquire(root);
        var pointer = ReadPointer(root) ?? throw new InvalidOperationException("Игра ещё не установлена.");
        if (pointer.Previous is null) throw new InvalidOperationException("Предыдущей сборки пока нет.");
        await VerifyReleaseAsync(root, pointer.Previous, token);
        await CommitPointerAsync(root, new(pointer.Previous, pointer.Current), token);
    }

    public static async Task LaunchAsync(LauncherSettings settings, CancellationToken token)
    {
        settings.Validate();
        using var installLock = Acquire(settings.InstallDirectory);
        var pointer = ReadPointer(settings.InstallDirectory) ?? throw new InvalidOperationException("Игра ещё не установлена.");
        await VerifyReleaseAsync(settings.InstallDirectory, pointer.Current, token);
        var release = ReadRelease(settings.InstallDirectory, pointer.Current);
        var game = SafePaths.Under(settings.InstallDirectory, $"versions/{pointer.Current}/game");
        var info = new ProcessStartInfo(SafePaths.Under(game, release.Manifest.Executable))
            { WorkingDirectory = game, UseShellExecute = false };
        // ArgumentList passes values without shell interpretation. Godot user args follow '--'.
        info.ArgumentList.Add("--");
        info.ArgumentList.Add($"--server-host={settings.ServerHost}");
        info.ArgumentList.Add($"--server-port={settings.ServerPort}");
        using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить клиент.");
        // Keep the lock while the game runs; UI stays responsive and can be minimized.
        await process.WaitForExitAsync(CancellationToken.None);
    }

    public static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    }

    private static async Task CommitPointerAsync(string root, InstallPointer pointer, CancellationToken token)
    {
        var temporary = SafePaths.Under(root, $"pointer-{Guid.NewGuid():N}.tmp");
        await WriteJsonAsync(temporary, pointer, token);
        var target = SafePaths.Under(root, "current.json");
        // Do not observe cancellation inside the commit: exactly one complete pointer must win.
        if (File.Exists(target)) File.Replace(temporary, target, null);
        else File.Move(temporary, target);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, JsonDefaults.Options, token);
        await stream.FlushAsync(token);
        stream.Flush(true);
    }
}
