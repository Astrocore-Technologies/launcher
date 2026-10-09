namespace ProjectG.Launcher.Core;

public static class SafePaths
{
    // Apply Windows rules even when release tools run on Linux; reject aliases and alternate streams.
    public static string Relative(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\\') || value.StartsWith('/') || value.Length > 220)
            throw new InvalidDataException("Небезопасный путь в сборке.");
        foreach (var part in value.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new InvalidDataException("Небезопасный путь в сборке.");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
                    "123456789¹²³".Contains(stem[3])))
                throw new InvalidDataException("Зарезервированное имя Windows в сборке.");
        }
        return value;
    }

    public static string Under(string root, string relative)
    {
        Relative(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь вне каталога сборки.");
        NoLinks(full);
        return full;
    }

    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Каталог установки не должен содержать символические ссылки.");
    }
}
