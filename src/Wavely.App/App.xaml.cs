using Microsoft.UI.Xaml;
using Wavely.Core;

namespace Wavely.App;

public partial class App : Application
{
    private Window? Main;
    private Mutex? InstanceMutex;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, Args) => AppPaths.Log(Args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, Args) => { if (Args.ExceptionObject is Exception Error) AppPaths.Log(Error); };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs Args)
    {
        InstanceMutex = new Mutex(true, "Local\\Wavely.Recorder", out var First);
        if (!First) { NativeShell.ShowExisting(); Exit(); return; }
        Main = new MainWindow();
        Main.Activate();
    }
}
