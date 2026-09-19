using Microsoft.UI.Xaml;
using RonPatcher.Diagnostics;

namespace RonPatcher;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        StartupLog.Write("App constructor entered.");
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            StartupLog.Write($"Unhandled WinUI exception (handled={args.Handled}).", args.Exception);
        };
        StartupLog.Write("App InitializeComponent completed.");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupLog.Write("OnLaunched entered.");
        try
        {
            MainWindow = new MainWindow();
            StartupLog.Write("MainWindow constructed.");
            MainWindow.Activate();
            StartupLog.Write("MainWindow activated.");
        }
        catch (Exception exception)
        {
            StartupLog.Write("OnLaunched failed.", exception);
            throw;
        }
    }
}
