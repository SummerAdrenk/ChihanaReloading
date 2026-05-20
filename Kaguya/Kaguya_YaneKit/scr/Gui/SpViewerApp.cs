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
using Avalonia.Themes.Fluent;
using Kaguya_YaneKit.Formats.Params;

namespace Kaguya_YaneKit.Gui;

internal sealed class SpViewerApp : Application
{
    private readonly string _picDir;
    private readonly ParamsDatDocument? _params;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;

    public SpViewerApp(string picDir, ParamsDatDocument? paramsDocument, int canvasWidth, int canvasHeight)
    {
        _picDir = picDir;
        _params = paramsDocument;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new SpViewerWindow(_picDir, _params, _canvasWidth, _canvasHeight);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void Launch(string picDir, ParamsDatDocument? paramsDocument, int canvasWidth, int canvasHeight)
    {
        var thread = new Thread(() =>
        {
            var builder = AppBuilder.Configure(() => new SpViewerApp(picDir, paramsDocument, canvasWidth, canvasHeight))
                .UsePlatformDetect()
                .LogToTrace();
            builder.StartWithClassicDesktopLifetime(Array.Empty<string>());
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
