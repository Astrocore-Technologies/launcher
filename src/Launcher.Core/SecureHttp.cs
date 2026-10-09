using System.Net;

namespace ProjectG.Launcher.Core;

// Redirects are followed explicitly so HTTPS cannot silently downgrade to HTTP.
public sealed class SecureHttp : IDisposable
{
    private readonly HttpClient _client;
    public SecureHttp() : this(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) { }
    public SecureHttp(HttpMessageHandler handler) => _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };

    public static void ValidateUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Для обновлений требуется HTTPS без пароля и фрагмента в адресе.");
    }

    private async Task<HttpResponseMessage> OpenAsync(Uri uri, CancellationToken token)
    {
        for (var hop = 0; hop < 6; hop++)
        {
            ValidateUrl(uri);
            var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("Перенаправление без адреса.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("Слишком много перенаправлений.");
    }

    public async Task<byte[]> ReadAsync(Uri uri, int limit, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await OpenAsync(uri, deadline.Token);
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Ответ сервера обновлений слишком большой.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public async Task DownloadAsync(Uri uri, string path, long expectedSize, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        using var response = await OpenAsync(uri, deadline.Token);
        if (response.Content.Headers.ContentLength is { } length && length != expectedSize)
            throw new InvalidDataException("Размер загрузки не совпадает с манифестом.");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        var buffer = new byte[131072];
        long total = 0;
        int read;
        var lastReport = Environment.TickCount64;
        while ((read = await input.ReadAsync(buffer, deadline.Token)) > 0)
        {
            total += read;
            if (total > expectedSize) throw new InvalidDataException("Загрузка превышает ожидаемый размер.");
            await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            if (Environment.TickCount64 - lastReport > 100)
            {
                progress?.Report(new($"Загрузка: {total / 1048576.0:F1} / {expectedSize / 1048576.0:F1} МБ", (double)total / expectedSize));
                lastReport = Environment.TickCount64;
            }
        }
        if (total != expectedSize) throw new InvalidDataException("Загрузка оборвалась до конца файла.");
        await output.FlushAsync(deadline.Token);
        output.Flush(true);
    }
    public void Dispose() => _client.Dispose();
}
