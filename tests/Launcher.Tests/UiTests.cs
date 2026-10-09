using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ProjectG.Launcher.App;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Launcher.Tests.UiTests))]
namespace Launcher.Tests;

public sealed class UiTests
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<LauncherApplication>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    [AvaloniaFact]
    public void WindowRendersAtDefaultSize()
    {
        var window = new MainWindow(previewMode: true);
        window.Show();
        try
        {
            Assert.Equal("Project G — Launcher", window.Title);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var destination = Path.GetFullPath("../../../../../.artifacts/launcher-preview.png", AppContext.BaseDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            frame.Save(destination);
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            using var compactFrame = window.CaptureRenderedFrame();
            Assert.NotNull(compactFrame);
            compactFrame.Save(Path.Combine(Path.GetDirectoryName(destination)!, "launcher-preview-minimum.png"));
        }
        finally { window.Close(); }
    }
}
