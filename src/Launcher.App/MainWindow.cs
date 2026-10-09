using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProjectG.Launcher.Core;

namespace ProjectG.Launcher.App;

public sealed class MainWindow : Window
{
    private readonly SecureHttp _http = new();
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectG", "Launcher", "settings.json");
    private LauncherSettings _settings = new();
    private readonly TextBlock _status = Label("Настройте источник обновлений", 22);
    private readonly TextBlock _detail = Label("Укажите адрес манифеста и открытый ключ издателя в настройках.", 13, "#9CAAB7");
    private readonly TextBlock _installed = Label("—", 24);
    private readonly TextBlock _available = Label("—", 24);
    private readonly TextBlock _server = Label("—", 17);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 5, Foreground = Brush.Parse("#64DDB6") };
    private readonly Button _play = new() { Content = "ИГРАТЬ", MinWidth = 200, Height = 52, FontSize = 18, FontWeight = FontWeight.Bold, Background = Brush.Parse("#64DDB6"), Foreground = Brush.Parse("#0D211D") };
    private readonly Button _check = new() { Content = "Проверить обновления" };
    private readonly Button _install = new() { Content = "Установить / обновить" };
    private readonly Button _rollback = new() { Content = "Предыдущая версия" };
    private readonly Button _verify = new() { Content = "Проверить файлы" };
    private readonly Button _cancel = new() { Content = "Отмена", IsEnabled = false };
    private readonly TextBox _url = new() { Watermark = "https://…/manifest.json" };
    private readonly TextBox _key = new() { AcceptsReturn = true, Height = 100, FontSize = 11, TextWrapping = TextWrapping.Wrap, Watermark = "-----BEGIN PUBLIC KEY-----", FontFamily = new FontFamily("Consolas") };
    private readonly TextBox _folder = new();
    private readonly TextBox _host = new();
    private readonly TextBox _port = new();
    private readonly Button _save = new() { Content = "Сохранить настройки" };
    
    private CancellationTokenSource? _operation;
    private bool _playing;
    private ReleaseManifest? _latest;

    public MainWindow(bool previewMode = false)
    {
        if (previewMode) _settings = new LauncherSettings { InstallDirectory = Path.Combine(Path.GetTempPath(), "launcher-preview-" + Guid.NewGuid().ToString("N")) };
        Title = "Project G — Launcher";
        Width = 1000; Height = 680; MinWidth = 960; MinHeight = 660;
        Background = Brush.Parse("#0D141C");
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(Label("PROJECT G", 30, "#E5F3ED", FontWeight.Bold));
        header.Children.Add(Label("ОДИН МИР. ТВОЯ ИСТОРИЯ.", 10, "#64DDB6"));

        // Two persistent columns keep settings and game actions visible without page scrolling.
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,20,*") };
        var card = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto") };
        _status.FontSize = 21;
        _status.Margin = new Thickness(0, 0, 0, 12);
        _detail.Margin = new Thickness(0, 0, 0, 18);
        _detail.MaxHeight = 90;
        _detail.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(_detail, _detail.Text);
        _progress.Margin = new Thickness(0, 0, 0, 20);
        var metrics = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        metrics.Children.Add(Metric("УСТАНОВЛЕНО", _installed, 0));
        metrics.Children.Add(Metric("ДОСТУПНО", _available, 1));
        var server = new StackPanel { Spacing = 6, Margin = new Thickness(0, 16, 0, 20) };
        server.Children.Add(Label("ИГРОВОЙ СЕРВЕР", 10, "#8295A5"));
        _server.TextWrapping = TextWrapping.NoWrap;
        _server.TextTrimming = TextTrimming.CharacterEllipsis;
        server.Children.Add(_server);
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*"), RowDefinitions = new RowDefinitions("Auto,10,Auto,10,Auto") };
        var buttons = new[] { _check, _install, _verify, _rollback, _cancel };
        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            button.FontSize = 12;
            button.Padding = new Thickness(8, 9);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(button, index % 2 * 2);
            Grid.SetRow(button, index / 2 * 2);
            actions.Children.Add(button);
        }
        _play.HorizontalAlignment = HorizontalAlignment.Stretch;
        _play.HorizontalContentAlignment = HorizontalAlignment.Center;
        _play.Margin = new Thickness(0, 20, 0, 0);
        var leftSections = new Control[] { _status, _detail, _progress, metrics, server, actions, _play };
        for (var index = 0; index < leftSections.Length; index++)
        { Grid.SetRow(leftSections[index], index); card.Children.Add(leftSections[index]); }
        actions.VerticalAlignment = VerticalAlignment.Top;
        columns.Children.Add(new Border { Background = Brush.Parse("#151F2A"), CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Child = card });

        var settings = new StackPanel { Spacing = 8 };
        settings.Children.Add(Label("Настройки", 21));
        AddSetting(settings, "Манифест обновлений (HTTPS)", _url);
        AddSetting(settings, "Открытый ключ издателя (PEM)", _key);
        AddSetting(settings, "Папка игры (полный путь)", _folder);
        var connection = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,90") };
        var host = new StackPanel { Spacing = 8 };
        var port = new StackPanel { Spacing = 8 };
        AddSetting(host, "Адрес сервера", _host);
        AddSetting(port, "UDP-порт", _port);
        connection.Children.Add(host); Grid.SetColumn(port, 2); connection.Children.Add(port);
        settings.Children.Add(connection);
        _save.HorizontalAlignment = HorizontalAlignment.Stretch;
        _save.HorizontalContentAlignment = HorizontalAlignment.Center;
        _save.Margin = new Thickness(0, 10, 0, 0);
        settings.Children.Add(_save);
        var settingsCard = new Border { Background = Brush.Parse("#151F2A"), CornerRadius = new CornerRadius(12), Padding = new Thickness(20), Child = settings };
        Grid.SetColumn(settingsCard, 2); columns.Children.Add(settingsCard);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(24) };
        body.Children.Add(header);
        Grid.SetRow(columns, 1); body.Children.Add(columns);
        var footer = Label("Project G Launcher  •  Windows  •  Закрытое тестирование", 11, "#7F909F");
        footer.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(footer, 2); body.Children.Add(footer);
        Content = body;
        _check.Click += async (_, _) => await RunAsync(CheckAsync);
        _install.Click += async (_, _) => await RunAsync(async token =>
        {
            await new UpdateService(_http).InstallAsync(_settings, new Progress<UpdateProgress>(ShowProgress), token);
            _latest = UpdateService.ReadInstalled(_settings.InstallDirectory)?.Manifest;
            SetStatus("Готово к игре", "Сборка проверена и установлена.");
        });
        _verify.Click += async (_, _) => await RunAsync(async token =>
        {
            SetStatus("Проверка файлов", "Проверяем размер и SHA-256 каждого файла игры.");
            _progress.IsIndeterminate = true;
            await UpdateService.VerifyAsync(_settings.InstallDirectory, token);
            SetStatus("Файлы в порядке", "Можно запускать игру.");
        });
        _rollback.Click += async (_, _) => await RunAsync(async token =>
        {
            await UpdateService.RollbackAsync(_settings.InstallDirectory, token);
            _latest = null;
            SetStatus("Предыдущая версия восстановлена", "Её совместимость проверит игровой сервер при подключении.");
        });
        _play.Click += async (_, _) => await RunAsync(async token =>
        {
            await CheckAsync(token);
            var installed = UpdateService.ReadInstalled(_settings.InstallDirectory);
            if (installed?.Manifest.Sha256 != _latest!.Sha256)
                await new UpdateService(_http).InstallAsync(_settings, new Progress<UpdateProgress>(ShowProgress), token);
            _playing = true; _cancel.IsEnabled = false;
            SetStatus("Запуск / игра работает", "Лаунчер можно свернуть. Обновление доступно после выхода из игры.");
            await UpdateService.LaunchAsync(_settings, token);
            SetStatus("Игра закрыта", "Можно снова играть или проверить обновления.");
        });
        _cancel.Click += (_, _) => _operation?.Cancel();
        _save.Click += async (_, _) => await RunAsync(async token =>
        {
            if (!int.TryParse(_port.Text, out var port)) throw new InvalidDataException("Порт должен быть числом.");
            var settingsValue = new LauncherSettings
            {
                ManifestUrl = _url.Text?.Trim() ?? "", PublicKeyPem = _key.Text?.Trim() ?? "",
                InstallDirectory = _folder.Text?.Trim() ?? "", ServerHost = _host.Text?.Trim() ?? "", ServerPort = port
            };
            settingsValue.Validate();
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporary = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settingsValue, JsonDefaults.Options), token);
            File.Move(temporary, _settingsPath, true);
            _settings = settingsValue; _latest = null;

            await CheckAsync(token);
        });
        Closing += (_, args) =>
        {
            if (_operation is null) return;
            args.Cancel = true;
            if (_playing) SetStatus("Игра ещё работает", "Сверните лаунчер или сначала закройте игру.");
            else { _operation.Cancel(); SetStatus("Отмена операции", "Дождитесь остановки загрузки, затем закройте окно."); }
        };
        Closed += (_, _) => _http.Dispose();
        Opened += async (_, _) =>
        {
            if (previewMode) return;
            try
            {
                var path = File.Exists(_settingsPath) ? _settingsPath : Path.Combine(AppContext.BaseDirectory, "launcher.settings.json");
                if (File.Exists(path)) _settings = JsonSerializer.Deserialize<LauncherSettings>(await File.ReadAllTextAsync(path), JsonDefaults.Options)
                    ?? throw new InvalidDataException("Пустой файл настроек.");
                LoadFields(); Refresh();
                if (_settings.ManifestUrl.Length > 0) await RunAsync(CheckAsync);

            }
            catch (Exception error) { LoadFields(); SetStatus("Проверьте настройки", error.Message); }
        };
        LoadFields(); Refresh();
    }

    private async Task CheckAsync(CancellationToken token)
    {
        SetStatus("Проверяем обновления", "Проверка подписанного манифеста.");
        _progress.IsIndeterminate = true;
        _latest = await new UpdateService(_http).CheckAsync(_settings, token);
        var installed = UpdateService.ReadInstalled(_settings.InstallDirectory);
        SetStatus(installed?.Manifest.Sha256 == _latest.Sha256 ? "Готово к игре" : "Доступна сборка " + _latest.Version,
            installed?.Manifest.Sha256 == _latest.Sha256 ? "У вас актуальная версия клиента." : $"Размер загрузки: {_latest.Size / 1048576.0:F1} МБ. Нажмите «Установить / обновить».");
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation; SetBusy(true);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { SetStatus("Операция остановлена", "Текущая установленная сборка сохранена. При тайм-ауте проверьте соединение и повторите."); }
        catch (Exception error) { SetStatus("Не удалось завершить операцию", error.Message); }
        finally
        {
            _operation = null; _playing = false; _progress.IsIndeterminate = false; SetBusy(false);
            try { Refresh(); } catch (Exception error) { _play.IsEnabled = false; SetStatus("Ошибка установки", error.Message); }
        }
    }

    private void Refresh()
    {
        var installed = UpdateService.ReadInstalled(_settings.InstallDirectory);
        _installed.Text = installed?.Manifest.Version ?? "Нет игры";
        _available.Text = _latest?.Version ?? "—";
        _server.Text = $"{_settings.ServerHost}:{_settings.ServerPort}";
        _play.IsEnabled = !string.IsNullOrWhiteSpace(_settings.ManifestUrl);
        _verify.IsEnabled = installed is not null;
        _rollback.IsEnabled = UpdateService.ReadPointer(_settings.InstallDirectory)?.Previous is not null;
        _install.IsEnabled = !string.IsNullOrWhiteSpace(_settings.ManifestUrl);
    }
    private void LoadFields()
    {
        _url.Text = _settings.ManifestUrl; _key.Text = _settings.PublicKeyPem; _folder.Text = _settings.InstallDirectory;
        _host.Text = _settings.ServerHost; _port.Text = _settings.ServerPort.ToString();
    }
    private void SetBusy(bool busy)
    {
        foreach (var control in new Control[] { _play, _check, _install, _rollback, _verify, _save, _url, _key, _folder, _host, _port }) control.IsEnabled = !busy;
        _cancel.IsEnabled = busy;
    }
    private void ShowProgress(UpdateProgress value)
    {
        _status.Text = value.Message;
        _progress.IsIndeterminate = value.Fraction is null;
        if (value.Fraction is { } fraction) _progress.Value = fraction * 100;
    }
    private void SetStatus(string title, string detail) { _status.Text = title; _detail.Text = detail; ToolTip.SetTip(_detail, detail); }
    private static TextBlock Label(string text, double size, string color = "#E0E9EF", FontWeight? weight = null) =>
        new() { Text = text, FontSize = size, Foreground = Brush.Parse(color), FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Metric(string title, TextBlock value, int column)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 12, 8) };
        panel.Children.Add(Label(title, 10, "#8295A5")); panel.Children.Add(value); Grid.SetColumn(panel, column); return panel;
    }
    private static void AddSetting(StackPanel panel, string title, Control input)
    { panel.Children.Add(Label(title, 12, "#9CAAB7")); panel.Children.Add(input); }
}
