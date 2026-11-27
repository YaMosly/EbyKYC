using System.Windows;

namespace Relay;

public partial class ConnectionWindow : Window
{
    internal SshProfile? Profile { get; private set; }

    internal ConnectionWindow(SshProfile? profile)
    {
        InitializeComponent();
        NativeTheme.Apply(this);
        if (profile is not null)
        {
            HostInput.Text = profile.Host;
            SessionInput.Text = profile.Session;
        }
        Loaded += (_, _) => HostInput.Focus();
    }

    private void Accept(object sender, RoutedEventArgs args)
    {
        var host = HostInput.Text.Trim();
        var session = SessionInput.Text.Trim();
        if (!SshProfile.IsHost(host))
        {
            Error.Text = "Введи SSH-алиас или имя хоста латиницей. Пример: my-vps.";
            HostInput.Focus();
            return;
        }
        if (!SshProfile.IsSession(session))
        {
            Error.Text = "В имени сессии допустимы латинские буквы, цифры, дефис и подчёркивание.";
            SessionInput.Focus();
            return;
        }
        Profile = new SshProfile(host, session);
        DialogResult = true;
    }
}
