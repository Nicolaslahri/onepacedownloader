using System.Windows;
using System.Windows.Threading;
using OnePace.Core;

namespace OnePace.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        Http.AppVersion = AppInfo.Version;

        var preview = PreviewSpec.Parse(e.Args);
        if (preview != null)
        {
            AppPaths.AppDir = preview.WorkDir;
            _preview = preview;
        }

        try
        {
            var window = new MainWindow(preview);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex) when (preview != null)
        {
            preview.Fail(ex);
            Shutdown(1);
        }
    }

    static PreviewSpec? _preview;

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_preview != null)
        {
            // CI screenshots: record the failure and stop, so the job fails loudly.
            _preview.Fail(e.Exception);
            e.Handled = true;
            Current.Shutdown(1);
            return;
        }
        // Keep the app alive and tell the user; a crash loses their place.
        e.Handled = true;
        if (Current?.MainWindow is MainWindow w) w.ReportCrash(e.Exception);
    }
}

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "3.0.0";
}
