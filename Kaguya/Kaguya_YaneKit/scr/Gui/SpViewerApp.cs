// ============================================================================
// SpViewerApp.cs
// Avalonia Application 入口: 从控制台启动 SP 立绘查看器窗口
//
// 使用方式:
//   SpViewerApp.Launch(picDir, paramsDocument, canvasWidth, canvasHeight)
//   在 STA 线程上启动 Avalonia, 控制台阻塞等待窗口关闭
// ============================================================================
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Kaguya_YaneKit.Formats.Params;

namespace Kaguya_YaneKit.Gui;

internal sealed class SpViewerApp : Application
{
    private readonly string _picDir;
    private readonly SpViewerSource _source;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    public SpViewerApp(string picDir, SpViewerSource source, int canvasWidth, int canvasHeight)
    {
        _picDir = picDir;
        _source = source;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new SpViewerWindow(_picDir, _source, _canvasWidth, _canvasHeight);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void Launch(string picDir, ParamsDatDocument? paramsDocument, int canvasWidth, int canvasHeight)
        => Launch(picDir, SpViewerSource.FromParams(paramsDocument), canvasWidth, canvasHeight);

    public static void Launch(string picDir, SpViewerSource source, int canvasWidth, int canvasHeight)
    {
        var thread = new Thread(() =>
        {
            var builder = AppBuilder.Configure(() => new SpViewerApp(picDir, source, canvasWidth, canvasHeight))
                .UsePlatformDetect()
                .LogToTrace();
            builder.StartWithClassicDesktopLifetime(Array.Empty<string>());
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}

internal sealed record SpViewerSource(ParamsDatDocument? ParamsDocument, string? TblstrScrDirectory)
{
    public static SpViewerSource FromParams(ParamsDatDocument? paramsDocument) =>
        new(paramsDocument, null);

    public static SpViewerSource FromTblstrScr(string scrDirectory) =>
        new(null, scrDirectory);
}
