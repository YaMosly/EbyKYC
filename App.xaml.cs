using System.Windows;

namespace Relay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs args)
    {
        Environment.SetEnvironmentVariable("TERM", "xterm-256color");
        base.OnStartup(args);
    }
}
