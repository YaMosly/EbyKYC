using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Relay;

public partial class MainWindow : Window
{
    private const string TerminalOrigin = "https://relay.local/";
    private const string TerminalPage = TerminalOrigin + "index.html";
    private CancellationTokenSource? activity;
    private CancellationTokenSource? chatLoading;
    private TaskCompletionSource? flush;
    private SshProfile? profile;
    private PseudoTerminal? attached;
    private int sequence;
    private int terminalInputId;
    private int columns = 92;
    private int rows = 26;
    private bool demoMode = true;
    private bool archiveMode;
    private bool installingChatList;
    private bool paused;
    private bool demoDone;
    private bool ready;
    private bool closed;
    private bool terminalInputEnabled;

    public MainWindow()
    {
        InitializeComponent();
        NativeTheme.Apply(this);
        SetControlsReady(false);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            var dataDirectory = Path.Combine(Path.GetTempPath(), "EbyKYC", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDirectory);
            if (closed)
                return;
            await Web.EnsureCoreWebView2Async(environment);
            if (closed)
                return;
            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.NavigationStarting += (_, navigation) => navigation.Cancel = navigation.Uri != TerminalPage;
            core.FrameNavigationStarting += (_, navigation) => navigation.Cancel = true;
            core.NewWindowRequested += (_, window) => window.Handled = true;
            core.PermissionRequested += (_, permission) => permission.State = CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, request) =>
            {
                if (!request.Request.Uri.StartsWith(TerminalOrigin, StringComparison.Ordinal))
                    request.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
            };
            core.NavigationCompleted += (_, navigation) =>
            {
                if (!navigation.IsSuccess && !closed)
                    ShowStartupFailure("Не удалось открыть локальную страницу терминала.");
            };
            core.ProcessFailed += (_, _) =>
            {
                if (!closed)
                    ShowStartupFailure("Терминал WebView2 остановился. Перезапусти EbyKYC.");
            };
            core.WebMessageReceived += OnWebMessage;
            core.SetVirtualHostNameToFolderMapping("relay.local",
                Path.Combine(AppContext.BaseDirectory, "Terminal"), CoreWebView2HostResourceAccessKind.Deny);
            core.Navigate(TerminalPage);
        }
        catch (Exception) when (closed)
        {
        }
        catch (Exception)
        {
            ShowStartupFailure("Терминал не загрузился. Проверь, что папка Terminal рядом с EbyKYC.exe и установлен Microsoft Edge WebView2 Runtime.");
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (closed || args.Source != TerminalPage)
            return;
        using var message = JsonDocument.Parse(args.WebMessageAsJson);
        var data = message.RootElement;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("kind", out var kind))
            return;

        switch (kind.GetString())
        {
            case "ready" when !ready:
                ready = true;
                Startup.Visibility = Visibility.Collapsed;
                SetControlsReady(true);
                var savedProfile = SshProfileStore.Load();
                if (savedProfile is null)
                    StartDemo();
                else
                    StartSsh(savedProfile);
                break;
            case "size":
                columns = data.GetProperty("columns").GetInt32();
                rows = data.GetProperty("rows").GetInt32();
                attached?.Resize(columns, rows);
                break;
            case "flushed" when ready:
                if (data.GetProperty("id").GetInt32() == sequence)
                    flush?.TrySetResult();
                break;
            case "input" when terminalInputEnabled && attached is { } connection:
                if (data.GetProperty("id").GetInt32() == terminalInputId &&
                    data.GetProperty("text").GetString() is { Length: > 0 } text &&
                    (text.Length > 262144 || !connection.QueueInput(text)))
                {
                    SetStatus("Ввод не отправлен. Повтори после ответа терминала.", "#FAD58F");
                }
                break;
            case "copy" when ready:
                if (data.GetProperty("text").GetString() is { Length: > 0 } selected)
                    _ = UseClipboardAsync(() => Clipboard.SetText(selected));
                break;
            case "paste" when terminalInputEnabled:
                if (data.GetProperty("id").GetInt32() == terminalInputId)
                    _ = PasteFromClipboardAsync();
                break;
        }
    }

    private Task PasteFromClipboardAsync()
    {
        var id = terminalInputId;
        return UseClipboardAsync(() =>
        {
            if (!terminalInputEnabled || id != terminalInputId)
                return;
            var text = Clipboard.GetText();
            if (!string.IsNullOrEmpty(text))
                Send(new { kind = "paste-text", id, text });
        });
    }

    private async Task UseClipboardAsync(Action operation)
    {
        for (var attempt = 0; attempt < 4 && !closed; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                if (attempt == 3)
                    SetStatus("Буфер обмена занят. Повтори действие.", "#FAD58F");
                else
                    await Task.Delay(75);
            }
        }
    }

    private void OpenLive(object sender, RoutedEventArgs args)
    {
        if (!archiveMode)
            return;
        if (demoMode)
            StartDemo();
        else if (profile is not null)
            StartSsh(profile);
    }

    private void RestartDemo(object sender, RoutedEventArgs args) => StartDemo();

    private void TogglePause(object sender, RoutedEventArgs args)
    {
        if (demoDone)
            return;
        paused = !paused;
        PauseButton.Content = paused ? "Продолжить" : "Пауза";
        SetStatus(paused ? "Демо на паузе" : "Воспроизведение демосеанса",
            paused ? "#FAD58F" : "#8AF2CC");
    }

    private void StartDemo()
    {
        var token = BeginActivity();
        chatLoading?.Cancel();
        demoMode = true;
        archiveMode = false;
        LiveNavLabel.Text = "Демосеанс";
        paused = false;
        demoDone = false;
        TerminalTitle.Text = "claude · demo";
        TerminalTitle.ToolTip = null;
        TerminalBadge.Text = "ДЕМО";
        ConnectLabel.Text = "Подключить сервер";
        ResetChatListSelection();
        ChatList.ItemsSource = Demo.Chats;
        ChatListStatus.Text = "";
        RefreshChats.Visibility = Visibility.Collapsed;
        BackToLive.Visibility = Visibility.Collapsed;
        PauseButton.Content = "Пауза";
        PauseButton.IsEnabled = true;
        PauseButton.Visibility = Visibility.Visible;
        RestartButton.Visibility = Visibility.Visible;
        DisconnectButton.Visibility = Visibility.Collapsed;
        ReconnectButton.Visibility = Visibility.Collapsed;
        SelectNavigation();
        SetStatus("Воспроизведение демосеанса", "#8AF2CC");
        Send(new { kind = "reset" });
        _ = PlayDemoAsync(token);
    }

    private async Task PlayDemoAsync(CancellationToken token)
    {
        try
        {
            foreach (var (delay, output) in Demo.Frames)
            {
                await Task.Delay(delay, token);
                while (paused)
                    await Task.Delay(80, token);
                token.ThrowIfCancellationRequested();
                Send(new { kind = "text", text = output });
            }
            demoDone = true;
            PauseButton.IsEnabled = false;
            SetStatus("Демо", "#8AF2CC");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void ConfigureSsh(object sender, RoutedEventArgs args)
    {
        var dialog = new ConnectionWindow(profile) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is not { } selected)
            return;

        var saved = SshProfileStore.TrySave(selected);
        StartSsh(selected);
        if (!saved)
        {
            MessageBox.Show(this, "Не удалось сохранить параметры подключения для следующего запуска.",
                "EbyKYC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StartSsh(SshProfile selected)
    {
        var token = BeginActivity();
        var loadList = demoMode || profile != selected;
        profile = selected;
        demoMode = false;
        archiveMode = false;
        LiveNavLabel.Text = "Живой сеанс";
        ResetChatListSelection();
        TerminalTitle.Text = $"{selected.Host} · {selected.Session}";
        TerminalTitle.ToolTip = null;
        TerminalBadge.Text = "SSH";
        ConnectLabel.Text = "Изменить подключение";
        RefreshChats.Visibility = Visibility.Visible;
        BackToLive.Visibility = Visibility.Collapsed;
        PauseButton.Visibility = Visibility.Collapsed;
        RestartButton.Visibility = Visibility.Collapsed;
        SetSshControls(active: true);
        SelectNavigation();
        if (loadList)
        {
            ChatList.ItemsSource = null;
            _ = LoadChatListAsync(selected);
        }
        _ = ConnectSshAsync(selected, token);
    }

    private void RefreshChatList(object sender, RoutedEventArgs args)
    {
        if (profile is not null && !demoMode)
            _ = LoadChatListAsync(profile);
    }

    private async Task LoadChatListAsync(SshProfile selected)
    {
        chatLoading?.Cancel();
        chatLoading?.Dispose();
        chatLoading = new CancellationTokenSource();
        var token = chatLoading.Token;
        RefreshChats.IsEnabled = false;
        if (ChatList.Items.Count == 0)
            ChatListStatus.Text = "Загрузка чатов…";
        try
        {
            var chats = await ChatHistory.ListAsync(selected, token);
            token.ThrowIfCancellationRequested();
            var hidden = SshProfileStore.LoadHiddenChatKeys(selected.Host);
            if (hidden.Length > 0)
                chats = chats.Where(chat => !hidden.Contains(chat.Key, StringComparer.Ordinal)).ToArray();
            var previous = (ChatList.SelectedItem as ChatEntry)?.Key;
            installingChatList = true;
            ChatList.ItemsSource = chats;
            ChatList.SelectedItem = chats.FirstOrDefault(chat => chat.Key == previous);
            ChatListStatus.Text = chats.Length == 0 ? "Сохранённых чатов пока нет" : "";
            ChatListStatus.ToolTip = null;
            RefreshChats.ToolTip = "Обновить чаты";
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ChatListStatus.Text = ChatList.Items.Count == 0 ? "Не удалось загрузить чаты" : "";
            ChatListStatus.ToolTip = error.Message;
            RefreshChats.ToolTip = error.Message;
        }
        finally
        {
            installingChatList = false;
            if (!token.IsCancellationRequested && !closed)
                RefreshChats.IsEnabled = true;
        }
    }

    private void ResetChatListSelection()
    {
        installingChatList = true;
        ChatList.SelectedItem = null;
        installingChatList = false;
    }

    private async void SelectChat(object sender, System.Windows.Controls.SelectionChangedEventArgs args)
    {
        if (installingChatList || !ready || ChatList.SelectedItem is not ChatEntry chat)
            return;
        var token = BeginActivity();
        archiveMode = true;
        TerminalBadge.Text = "ЧАТ";
        TerminalTitle.Text = chat.Title;
        TerminalTitle.ToolTip = chat.Title;
        PauseButton.Visibility = RestartButton.Visibility = Visibility.Collapsed;
        DisconnectButton.Visibility = ReconnectButton.Visibility = Visibility.Collapsed;
        BackToLive.Visibility = Visibility.Visible;
        SelectNavigation();
        Send(new { kind = "reset" });

        try
        {
            SetStatus("Загрузка истории…", "#FAD58F");
            var transcript = demoMode
                ? Demo.OpenChat(chat.Key)
                : await ChatHistory.ReadAsync(profile!, chat.Key, token);
            token.ThrowIfCancellationRequested();
            var body = new StringBuilder("\x1b[?25l");
            if (transcript.Limited)
                body.Append("\x1b[90mПоказана недавняя часть чата.\x1b[0m\r\n\r\n");
            if (transcript.Messages.Length == 0)
                body.Append("В чате пока нет текстовых сообщений.\r\n");
            foreach (var message in transcript.Messages)
            {
                body.Append(message.Role == "user" ? "\x1b[1;36mТы\x1b[0m\r\n" : "\x1b[1;32mClaude\x1b[0m\r\n");
                body.Append(TerminalText(message.Text)).Append("\r\n\r\n");
            }
            Send(new { kind = "text", text = body.ToString(), start = true });
            SetStatus("История чата", "#8AF2CC");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            WriteError(error.Message);
            SetStatus("Не удалось открыть чат", "#FFADB1");
        }
    }

    private async Task ConnectSshAsync(SshProfile selected, CancellationToken token)
    {
        try
        {
            for (var attempt = 0; attempt <= 3; attempt++)
            {
                if (attempt > 0)
                {
                    SetStatus($"Связь прервана · повтор {attempt} из 3", "#FAD58F");
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token);
                }
                else
                {
                    SetStatus("Проверяем SSH и сессию tmux…", "#FAD58F");
                    Send(new { kind = "reset" });
                }

                var failure = await Ssh.ProbeAsync(selected, token);
                token.ThrowIfCancellationRequested();
                if (failure is not null)
                {
                    WriteError(failure);
                    if (attempt == 0 || attempt == 3)
                        break;
                    continue;
                }

                Send(new { kind = "reset" });
                var connection = Ssh.Attach(selected, columns, rows);
                attached = connection;
                SetStatus("Подключено · терминал принимает ввод", "#8AF2CC");
                if (attempt == 0)
                    Web.Focus();
                SetTerminalInput(true);
                try
                {
                    var code = await connection.ReadAsync((bytes, count) => DisplayBytesAsync(bytes, count, token), token);
                    token.ThrowIfCancellationRequested();
                    if (code == 0)
                    {
                        SetSshControls(active: false);
                        SetStatus("Подключение завершено", "#A1ADC0");
                        return;
                    }
                }
                finally
                {
                    if (attached == connection)
                    {
                        attached = null;
                        SetTerminalInput(false);
                    }
                }
            }
            SetSshControls(active: false);
            SetStatus("Нет подключения · проверь сообщение в терминале", "#FFADB1");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!closed && !token.IsCancellationRequested)
        {
            SetSshControls(active: false);
            WriteError(exception.Message);
            SetStatus("Не удалось открыть SSH-сеанс", "#FFADB1");
        }
    }

    private void DisconnectSsh(object sender, RoutedEventArgs args)
    {
        SetTerminalInput(false);
        activity?.Cancel();
        SetSshControls(active: false);
        SetStatus("SSH отключён", "#A1ADC0");
    }

    private void ReconnectSsh(object sender, RoutedEventArgs args)
    {
        if (profile is not null)
            StartSsh(profile);
    }

    private void SetSshControls(bool active)
    {
        DisconnectButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ReconnectButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task DisplayBytesAsync(byte[] buffer, int count, CancellationToken token)
    {
        // Await the renderer before reading another chunk to bound queued terminal data.
        var payload = Convert.ToBase64String(buffer, 0, count);
        await Dispatcher.InvokeAsync(async () =>
        {
            if (token.IsCancellationRequested || closed)
                return;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            flush = completion;
            Send(new { kind = "bytes", payload, id = ++sequence });
            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                if (flush == completion)
                    flush = null;
            }
        }).Task.Unwrap();
    }

    private void WriteError(string error)
    {
        Send(new { kind = "text", text = "\r\n" + TerminalText(error) + "\r\n" });
    }

    private static string TerminalText(string text) =>
        new string(text.Where(character => !char.IsControl(character) || character is '\n' or '\t').ToArray())
            .Replace("\n", "\r\n", StringComparison.Ordinal);

    private void Send<T>(T message)
    {
        if (ready && !closed)
            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void SetTerminalInput(bool enabled)
    {
        terminalInputEnabled = enabled;
        Send(new { kind = "input-mode", enabled, id = ++terminalInputId });
    }

    private CancellationToken BeginActivity()
    {
        SetTerminalInput(false);
        activity?.Cancel();
        activity?.Dispose();
        activity = new CancellationTokenSource();
        return activity.Token;
    }

    private void SetStatus(string text, string color)
    {
        Lamp.ToolTip = text;
        System.Windows.Automation.AutomationProperties.SetName(Lamp, text);
        Lamp.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void SelectNavigation()
    {
        var selected = new SolidColorBrush(Color.FromRgb(36, 50, 66));
        var normal = new SolidColorBrush(Color.FromRgb(20, 27, 39));
        DemoNav.Background = archiveMode ? normal : selected;
    }

    private void SetControlsReady(bool enabled)
    {
        DemoNav.IsEnabled = Connect.IsEnabled = PauseButton.IsEnabled = RestartButton.IsEnabled = enabled;
        ChatList.IsEnabled = enabled;
        DisconnectButton.IsEnabled = ReconnectButton.IsEnabled = enabled;
    }

    private void ShowStartupFailure(string text)
    {
        ready = false;
        activity?.Cancel();
        SetControlsReady(false);
        Web.Visibility = Visibility.Collapsed;
        Startup.Visibility = Visibility.Visible;
        Startup.Text = text;
        SetStatus("Ошибка терминала", "#FFADB1");
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        closed = true;
        activity?.Cancel();
        activity?.Dispose();
        chatLoading?.Cancel();
        chatLoading?.Dispose();
        Web.Dispose();
    }
}
