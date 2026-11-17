using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevChat.App.Agent;
using DevChat.App.Services;
using DevChat.App.Tools;

namespace DevChat.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly Settings _settings;
    private readonly HistoryService _history;
    private readonly BridgeService _bridge;
    private readonly SkillService _skills = new();
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private ChatMessageViewModel? _pendingAssistant; // сообщение, в которое стримятся дельты
    private CancellationTokenSource? _runCts;
    private ChatMessageViewModel? _lastActionMsg;
    private System.Windows.Threading.DispatcherTimer _busyTimer;
    private System.Diagnostics.Stopwatch _runStopwatch = new();
    private int _busySeconds;
    private bool _phaseSent;
    private bool _gotDelta;

    public ObservableCollection<ChatListItemViewModel> Chats { get; } = new();
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public ObservableCollection<string> Files { get; } = new();
    public ObservableCollection<PlanItemViewModel> PlanItems { get; } = new();

    [ObservableProperty]
    private string _planHeader = "План";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private ChatListItemViewModel? _selectedChat;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _inputText = "";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>0 — чат; 1 — агент; 2 — команда (оркестратор).</summary>
    [ObservableProperty]
    private int _workModeIndex;

    public string[] WorkModes { get; } = { "💬 Чат", "🔧 Агент", "🤝 Команда" };

    public string WorkModeLabel => WorkModes[Math.Clamp(WorkModeIndex, 0, 2)];

    partial void OnWorkModeIndexChanged(int value) => OnPropertyChanged(nameof(WorkModeLabel));

    [RelayCommand]
    private void CycleWorkMode() => WorkModeIndex = (WorkModeIndex + 1) % 3;

    /// <summary>
    /// Установить расширение Chrome ПОСТОЯННО: открывает chrome://extensions
    /// и показывает пошаговую инструкцию «Load unpacked» (одноразовое действие).
    /// </summary>
    [RelayCommand]
    private void InstallExtension()
    {
        // ищем папку расширения
        var exeDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(exeDir, "extension"),
            Path.Combine(exeDir, "..", "..", "..", "..", "src", "extension", "dist"),
        };
        var extPath = candidates.FirstOrDefault(p => Directory.Exists(p) && File.Exists(Path.Combine(p, "manifest.json")));
        if (extPath is null)
        {
            Messages.Add(new ChatMessageViewModel { Role = "system", Text = "🔌 Папка расширения не найдена рядом с приложением." });
            return;
        }
        extPath = Path.GetFullPath(extPath);

        // копируем путь в буфер обмена (пользователь вставит в диалог выбора папки)
        try { System.Windows.Clipboard.SetText(extPath); } catch { /* буфер занят */ }

        // открываем chrome://extensions
        var chrome = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
        }.FirstOrDefault(File.Exists);

        if (chrome is not null)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(chrome)
            {
                Arguments = "chrome://extensions",
                UseShellExecute = true,
            });
        }

        Messages.Add(new ChatMessageViewModel { Role = "system", Text =
            $"🔌 Установка расширения (один раз):\n\n" +
            $"1. В Chrome открылась страница расширений\n" +
            $"2. Включите «Режим разработчика» (переключатель справа сверху)\n" +
            $"3. Нажмите «Загрузить распакованное расширение»\n" +
            $"4. Вставьте путь (уже в буфере): {extPath}\n" +
            $"5. Нажмите «Выбор папки»\n\n" +
            $"После этого расширение установлено ПОСТОЯННО и переживает перезапуски Chrome." });
    }

    /// <summary>0 — DeepSeek, 1 — ChatGPT. Сохраняется в настройках.</summary>
    [ObservableProperty]
    private int _providerIndex;

    public string[] Providers { get; } = { "🤖 DeepSeek", "🧠 ChatGPT" };

    public string ProviderName => ProviderIndex == 1 ? "chatgpt" : "deepseek";

    public string ProviderLabel => Providers[Math.Clamp(ProviderIndex, 0, 1)];

    public string ProviderName2 => ProviderIndex == 1 ? "ChatGPT" : "DeepSeek";

    public bool HasGptIcon => ProviderIndex == 1;

    public bool HasDsIcon => ProviderIndex == 0;

    partial void OnProviderIndexChanged(int value)
    {
        _settings.ProviderIndex = value;
        _settings.Save();
        OnPropertyChanged(nameof(ProviderLabel));
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(ProviderName2));
        OnPropertyChanged(nameof(HasGptIcon));
        OnPropertyChanged(nameof(HasDsIcon));
    }

    [RelayCommand]
    private void CycleProvider() => ProviderIndex = 1 - ProviderIndex;

    [RelayCommand]
    private void CopyMessage(ChatMessageViewModel? msg)
    {
        if (msg is null || string.IsNullOrEmpty(msg.Text)) return;
        try { System.Windows.Clipboard.SetText(msg.Text); }
        catch { /* буфер занят другим процессом — повтори клик */ }
    }

    /// <summary>Вернуть текст сообщения в поле ввода (не писать промт заново).</summary>
    [RelayCommand]
    private void RepeatMessage(ChatMessageViewModel? msg)
    {
        if (msg is null) return;
        InputText = msg.Text;
    }

    /// <summary>0 — подтверждать всё; 1 — только команды; 2 — без подтверждений.</summary>
    [ObservableProperty]
    private int _confirmModeIndex;

    public string[] ConfirmModes { get; } = { "🔐 Подтверждения: все", "⚙ Подтверждения: только команды", "🔓 Подтверждения: выкл" };

    public string ConfirmModeLabel => ConfirmModes[Math.Clamp(ConfirmModeIndex, 0, 2)];

    partial void OnConfirmModeIndexChanged(int value)
    {
        _settings.ConfirmMode = value;
        _settings.Save();
        OnPropertyChanged(nameof(ConfirmModeLabel));
    }

    [RelayCommand]
    private void CycleConfirmMode() => ConfirmModeIndex = (ConfirmModeIndex + 1) % 3;

    private bool _allowAllThisTask;

    [ObservableProperty]
    private string _statusText = "Ожидание расширения…";

    /// <summary>Живой индикатор хода: «⏳ DeepSeek думает 0:47» / «✍ Отвечает 1:12».</summary>
    [ObservableProperty]
    private string _busyInfoText = "";

    [ObservableProperty]
    private Brush _statusBrush = Brushes.Gray;

    /// <summary>Запрос код-бихайнда прокрутить ленту вниз (новое сообщение/дельта).</summary>
    public event Action? ScrollToBottomRequested;

    public MainViewModel(Settings settings, HistoryService history, BridgeService bridge)
    {
        _settings = settings;
        _history = history;
        _bridge = bridge;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        ConfirmModeIndex = Math.Clamp(settings.ConfirmMode, 0, 2);
        ProviderIndex = Math.Clamp(settings.ProviderIndex, 0, 1);

        foreach (var c in history.ListChats())
            Chats.Add(new ChatListItemViewModel { Id = c.Id, Title = c.Title, ProviderChatUrl = c.ProviderChatUrl, AgentRole = c.AgentRole });
        SelectedChat = Chats.FirstOrDefault();

        bridge.Connected += () => _dispatcher.BeginInvoke(() => { FileLog.Write("расширение подключено"); UpdateStatus(); });
        bridge.Disconnected += () => _dispatcher.BeginInvoke(() => { FileLog.Write("расширение отключено"); UpdateStatus(); });
        bridge.Delta += chunk => _dispatcher.BeginInvoke(() =>
        {
            _gotDelta = true;
            if (_pendingAssistant is { } m)
            {
                m.Text += chunk;
                ScrollToBottomRequested?.Invoke();
            }
        });
        bridge.Phase += p => _dispatcher.BeginInvoke(() =>
        {
            if (p == "sent") _phaseSent = true;
        });
        bridge.Log += m => FileLog.Write(m);

        // секундный таймер живого индикатора хода
        _busyTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _busyTimer.Tick += (_, _) =>
        {
            if (!IsBusy) return;
            _busySeconds++;
            var phase = _gotDelta ? "✍ Отвечает" : _phaseSent ? $"⏳ {ProviderName} думает" : "⏳ Отправка";
            BusyInfoText = $"{phase} {_busySeconds / 60}:{_busySeconds % 60:D2}";
        };

        _ = bridge.RunAsync(settings.Port, settings.Token, AppShutdownToken).ContinueWith(
            t => _dispatcher.BeginInvoke(() => StatusText = $"Ошибка моста: {t.Exception!.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);

        // автозапуск установки расширения при первом запуске (мост не подключился за 8с)
        var setupTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        setupTimer.Tick += (_, _) =>
        {
            setupTimer.Stop();
            if (!_bridge.IsConnected && !_extensionAutoInstalled)
            {
                _extensionAutoInstalled = true;
                Messages.Add(new ChatMessageViewModel { Role = "system", Text = "🔌 Добро пожаловать в DevChat!\n\nРасширение Chrome ещё не установлено. Нажмите кнопку подключения в шапке для пошаговой установки (займёт 30 секунд, один раз)." });
            }
        };
        setupTimer.Start();
        // если мост подключился до таймера — отменяем
        bridge.Connected += () => _dispatcher.BeginInvoke(() => setupTimer.Stop());

        // проверка обновлений с GitHub (не блокирует UI)
        _ = CheckForUpdatesAsync();

        RefreshFiles();
    }

    private bool _extensionAutoInstalled;

    private async Task CheckForUpdatesAsync()
    {
        var info = await UpdateService.CheckAsync();
        if (info.HasUpdate)
        {
            await _dispatcher.BeginInvoke(() =>
            {
                var answer = System.Windows.MessageBox.Show(
                    $"Доступна новая версия: {info.LatestVersion}\n" +
                    $"Текущая: {UpdateService.CurrentVersion}\n\n" +
                    $"Открыть страницу загрузки?",
                    "DevChat — обновление",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Information);
                if (answer == System.Windows.MessageBoxResult.Yes && info.DownloadUrl is { } url)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            });
        }
    }

    private static CancellationToken AppShutdownToken => (System.Windows.Application.Current as App)?.ShutdownCts.Token ?? CancellationToken.None;

    private void UpdateStatus()
    {
        var connected = _bridge.IsConnected;
        StatusText = connected ? "Мост: подключено" : "Мост: ожидание расширения…";
        StatusBrush = connected ? Brushes.MediumSeaGreen : new SolidColorBrush(Color.FromRgb(0xe5, 0x48, 0x4d));
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings) { Owner = System.Windows.Application.Current.MainWindow };
        win.ShowDialog();
        RefreshFiles();
    }

    /// <summary>Диагностика моста: соединение + живость селекторов DeepSeek.</summary>
    [RelayCommand]
    private async Task DiagnoseAsync()
    {
        void Say(string s)
        {
            Messages.Add(new ChatMessageViewModel { Role = "system", Text = s });
            FileLog.Write("диагностика: " + s);
            ScrollToBottomRequested?.Invoke();
        }

        Say("🩺 Диагностика моста…");
        if (!_bridge.IsConnected)
        {
            Say("✗ Расширение не подключено. Проверь: Chrome запущен; вкладка chrome://extensions → DevChat Bridge включён; service worker активен.");
            return;
        }
        try
        {
            var status = await _bridge.RequestAsync("getStatus", new Dictionary<string, object?> { ["provider"] = ProviderName }, 15_000);
            var statusLine = status.TryGetProperty("data", out var d)
                ? $"вкладка: {(TryBool(d, "tabOpen") ? "открыта" : "закрыта (будет создана)")}, вход: {(TryBool(d, "loggedIn") ? "выполнен" : TryBool(d, "loginPage") ? "НЕТ (страница входа!)" : "неизвестен")}"
                : status.GetRawText();
            Say($"✓ Соединение: активно. {ProviderLabel}: {statusLine}");

            var probe = await _bridge.RequestAsync("probe", new Dictionary<string, object?> { ["provider"] = ProviderName }, 30_000);
            if (probe.TryGetProperty("data", out var p))
            {
                var composer = p.TryGetProperty("composer", out var comp) && comp.GetArrayLength() > 0;
                var answer = p.TryGetProperty("assistantMsg", out var ans) && ans.GetArrayLength() > 0;
                Say($"✓ Селекторы: поле ввода {(composer ? "найдено" : "НЕ НАЙДЕНО (!)")}, блок ответа {(answer ? "найден" : "не найден (пустой чат — это нормально)")}");
                if (!composer)
                {
                    // дамп живого DOM — чтобы подобрать селекторы под фактическую вёрстку
                    var dump = await _bridge.RequestAsync("dump", new Dictionary<string, object?> { ["provider"] = ProviderName }, 30_000);
                    if (dump.TryGetProperty("data", out var dd))
                    {
                        var raw = dd.GetRawText();
                        Say($"🔍 Дамп: {raw[..Math.Min(raw.Length, 700)]}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Say($"✗ Диагностика провалилась: {ex.Message}");
        }
    }

    private static bool TryBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Горячая перезагрузка кода расширения (SW вечно живёт при активном WS и не подхватывает dist).</summary>
    [RelayCommand]
    private async Task ReloadExtensionAsync()
    {
        try
        {
            await _bridge.RequestAsync("reloadExtension", timeoutMs: 10_000);
            Messages.Add(new ChatMessageViewModel { Role = "system", Text = "⟳ Расширение перезагружается — переподключение займёт до ~30с." });
            FileLog.Write("перезагрузка расширения запрошена");
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessageViewModel { Role = "system", Text = $"⟳ Не удалось: {ex.Message}\nЕсли «неизвестная операция» — расширение на старой сборке, нажми «Обновить» на chrome://extensions вручную." });
        }
    }

    [RelayCommand]
    private void DeleteChat(ChatListItemViewModel? chat)
    {
        if (chat is null || IsBusy) return;
        var answer = System.Windows.MessageBox.Show(
            $"Удалить чат «{chat.Title}»?\nСообщения, план команды и журнал отката этого чата будут удалены безвозвратно.",
            "DevChat", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        _history.DeleteChat(chat.Id);
        var index = Chats.IndexOf(chat);
        Chats.Remove(chat);
        if (ReferenceEquals(SelectedChat, null) && Chats.Count > 0)
            SelectedChat = Chats[Math.Clamp(index, 0, Chats.Count - 1)];
        else if (Chats.Count == 0)
            SelectedChat = null;
    }

    /// <summary>Управление агентами (скиллами): создание/редактирование/состав команды.</summary>
    [RelayCommand]
    private void OpenAgents()
    {
        var win = new AgentsWindow(_skills) { Owner = System.Windows.Application.Current.MainWindow };
        win.ShowDialog();
    }

    [RelayCommand]
    private void NewChat()
    {
        var id = _history.CreateChat();
        var chat = new ChatListItemViewModel { Id = id };
        Chats.Insert(0, chat);
        SelectedChat = chat;
    }

    /// <summary>Чистый контекст: новая сессия выбранного провайдера для этого чата (история в приложении сохраняется).</summary>
    [RelayCommand]
    private async Task NewSessionAsync()
    {
        var chat = SelectedChat;
        if (chat is null || IsBusy || !_bridge.IsConnected) return;
        try
        {
            await _bridge.RequestAsync("openChat", new Dictionary<string, object?> { ["newChat"] = true, ["provider"] = ProviderName }, 30_000);
            chat.ProviderChatUrl = null;
            _history.ClearProviderChatUrl(chat.Id);
            _history.ClearTeamState(chat.Id); // новая сессия = новая задача команды
            Messages.Add(new ChatMessageViewModel { Role = "system", Text = $"🔄 Сессия {ProviderName} обновлена — модель начнёт с чистого контекста." });
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessageViewModel { Role = "system", Text = $"🔄 Не удалось обновить сессию: {ex.Message}" });
        }
    }

    partial void OnSelectedChatChanged(ChatListItemViewModel? value) => LoadMessages(value);

    /// <summary>Чеклист плана из состояния команды; ✓ — пункт, чьи файлы агент уже менял.</summary>
    private void UpdatePlanItems(HistoryService.TeamState? state)
    {
        PlanItems.Clear();
        if (state is null) { PlanHeader = "План"; return; }
        var items = ParsePlanItems(state.Plan);
        foreach (var item in items)
        {
            var touchedFiles = state.ChangedFiles
                .Where(f => item.Contains(System.IO.Path.GetFileName(f), StringComparison.OrdinalIgnoreCase))
                .Select(System.IO.Path.GetFileName)
                .Distinct()
                .ToList();
            PlanItems.Add(new PlanItemViewModel
            {
                Text = item,
                Touched = touchedFiles.Count > 0,
                FilesNote = touchedFiles.Count > 0 ? "↳ " + string.Join(", ", touchedFiles) : "",
            });
        }
        var done = PlanItems.Count(p => p.Touched);
        var stage = state.Stage switch { "dev" => "🔨 разработка", "review" => "🔍 ревью", "done" => "✅ готово", _ => state.Stage };
        PlanHeader = items.Count > 0 ? $"План {done}/{items.Count} · {stage}" : $"План · {stage}";
        FileLog.Write($"план: пунктов={items.Count}, отмечено={done}, этап={state.Stage}, длина плана={state.Plan.Length}");
    }

    private static List<string> ParsePlanItems(string plan)
    {
        // план может прийти и строками, и одной простынёй («ПЛАН: 1. … 2. …»),
        // а числа вроде «41–60» внутри текста не должны ломать нарезку
        var text = System.Text.RegularExpressions.Regex.Replace(plan, @"^\s*ПЛАН\s*:?\s*", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // цепочка монотонной нумерации 1. 2. 3. … (или 1) 2) …)
        var candidates = System.Text.RegularExpressions.Regex.Matches(text, @"(?:^|\n|\s)(?<n>\d{1,2})[\.\)]\s+")
            .Select(m => (num: int.Parse(m.Groups["n"].Value), start: m.Index + m.Length, next: m.Index))
            .ToList();

        var chain = new List<(int num, int start, int next)>();
        // начало плана могли обрезать (экстракция) — стартуем цепочку с минимального доступного номера
        int expected = candidates.Count > 0 ? candidates.Min(c => c.num) : 1;
        foreach (var c in candidates)
        {
            if (c.num == expected) { chain.Add(c); expected++; }
        }

        if (chain.Count > 0)
        {
            var items = new List<string>();
            for (var i = 0; i < chain.Count; i++)
            {
                var end = i + 1 < chain.Count ? chain[i + 1].next : text.Length;
                var chunk = text[chain[i].start..end].Trim();
                if (chunk.Length > 2) items.Add(chunk);
            }
            return items;
        }

        // фолбэк: маркерные строки
        return plan.Split('\n').Select(l => l.Trim())
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^(?:\d+[\.\)]|[-*•])\s+"))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^(?:\d+[\.\)]|[-*•])\s+", ""))
            .ToList();
    }

    private void LoadMessages(ChatListItemViewModel? chat)
    {
        Messages.Clear();
        _pendingAssistant = null;
        UpdatePlanItems(chat is null ? null : _history.GetTeamState(chat.Id));
        if (chat is null) return;
        foreach (var m in _history.ListMessages(chat.Id))
            Messages.Add(new ChatMessageViewModel { Role = m.Role, Text = m.Text, Timestamp = m.CreatedAt });
        ScrollToBottomRequested?.Invoke();
    }

    // ── отправка ────────────────────────────────────────────────────────────

    private bool CanSend() => !IsBusy && _bridge.IsConnected && SelectedChat is not null && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        var chat = SelectedChat!;
        InputText = "";

        _history.AppendMessage(chat.Id, "user", text);
        Messages.Add(new ChatMessageViewModel { Role = "user", Text = text });
        ScrollToBottomRequested?.Invoke();

        IsBusy = true;
        _runCts = new CancellationTokenSource();
        _runStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _busySeconds = 0;
        _phaseSent = false;
        _gotDelta = false;
        BusyInfoText = "⏳ Отправка 0:00";
        _busyTimer.Start();
        SendCommand.NotifyCanExecuteChanged();
        FileLog.Write($"send: режим={WorkModeLabel}, провайдер={ProviderName}, чат={chat.Id}, длина={text.Length}");
        try
        {
            if (WorkModeIndex != 2 && chat.ProviderChatUrl is null)
                await _bridge.RequestAsync("openChat", new Dictionary<string, object?> { ["newChat"] = true, ["provider"] = ProviderName }, 30_000);

            switch (WorkModeIndex)
            {
                case 1:
                    await RunAgentAsync(chat, text);
                    break;
                case 2:
                    await RunTeamAsync(chat, text);
                    break;
                default:
                    await SendPlainAsync(chat, text);
                    break;
            }

            if (chat.Title == "Новый чат")
            {
                var title = text.Length > 40 ? text[..40] + "…" : text;
                chat.Title = title;
                _history.RenameChat(chat.Id, title);
            }
        }
        catch (Exception ex)
        {
            var friendly = FriendlyError(ex);
            FileLog.Write($"ошибка: {ex.GetType().Name}: {ex.Message}");
            Messages.Add(new ChatMessageViewModel { Role = "system", Text = friendly });
            _history.AppendMessage(chat.Id, "system", friendly);
        }
        finally
        {
            _busyTimer.Stop();
            BusyInfoText = "";
            _pendingAssistant = null;
            _lastActionMsg = null;
            _allowAllThisTask = false;
            IsBusy = false;
            _runCts?.Dispose();
            _runCts = null;
            SendCommand.NotifyCanExecuteChanged();
            ScrollToBottomRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void Stop()
    {
        try { _runCts?.Cancel(); }
        catch (ObjectDisposedException) { /* уже завершено */ }
    }

    private async Task SendPlainAsync(ChatListItemViewModel chat, string text)
    {
        var assistant = new ChatMessageViewModel { Role = "assistant" };
        _pendingAssistant = assistant;
        Messages.Add(assistant);

        var result = await _bridge.RequestAsync("sendPrompt", new Dictionary<string, object?> { ["text"] = text, ["provider"] = ProviderName }, 360_000, _runCts!.Token);
        if (result.TryGetProperty("type", out var t) && t.GetString() == "error")
        {
            if (result.TryGetProperty("data", out var ed))
                FileLog.Write("дамп ошибки: " + ed.GetRawText()[..Math.Min(ed.GetRawText().Length, 2500)]);
            throw new BridgeException(
                result.TryGetProperty("code", out var c) ? c.GetString() ?? "error" : "error",
                result.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
        }

        if (result.TryGetProperty("markdown", out var md) && md.GetString() is { } full && full.Length >= assistant.Text.Length)
            assistant.Text = full;
        assistant.Duration = $"{_runStopwatch.Elapsed.Minutes}:{_runStopwatch.Elapsed.Seconds:D2}";
        _history.AppendMessage(chat.Id, "assistant", assistant.Text);
        RememberProviderUrl(chat, result);
    }

    /// <summary>Подключает события агент-раннера к ленте (author — подпись роли для команды).</summary>
    private void WireRunner(AgentRunner runner, string? author)
    {
        var turnSw = new System.Diagnostics.Stopwatch();
        runner.TurnStarted += () => _dispatcher.BeginInvoke(() =>
        {
            turnSw.Restart();
            _pendingAssistant = new ChatMessageViewModel { Role = "assistant", Author = author };
            Messages.Add(_pendingAssistant);
        });
        runner.TurnCompleted += () => _dispatcher.BeginInvoke(() =>
        {
            if (_pendingAssistant is { } m)
            {
                m.Duration = $"{turnSw.Elapsed.Minutes}:{turnSw.Elapsed.Seconds:D2}";
                // сырой ответ модели в журнал — диагностика «вне протокола» по фактам
                var raw = m.Text.ReplaceLineEndings(" ");
                FileLog.Write($"ответ[{author ?? "агент"}]: {raw[..Math.Min(raw.Length, 500)]}");
            }
        });
        runner.ActionStarted += title => _dispatcher.BeginInvoke(() =>
        {
            FileLog.Write("действие: " + title);
            _lastActionMsg = new ChatMessageViewModel { Role = "agent", Author = author, Text = title };
            Messages.Add(_lastActionMsg);
            ScrollToBottomRequested?.Invoke();
        });
        runner.ActionDone += status => _dispatcher.BeginInvoke(() =>
        {
            FileLog.Write("  " + status);
            if (_lastActionMsg is { } m) m.Text += "\n" + status;
        });
        runner.SystemMessage += s => _dispatcher.BeginInvoke(() =>
        {
            FileLog.Write("агент: " + s);
            Messages.Add(new ChatMessageViewModel { Role = "system", Text = s });
        });
    }

    private void EnsureWorkingFolder()
    {
        if (string.IsNullOrWhiteSpace(_settings.WorkingFolder) || !Directory.Exists(_settings.WorkingFolder))
            throw new InvalidOperationException("Не задана рабочая папка — укажи её в Настройках (нужна для инструментов агента).");
    }

    /// <summary>Открыть папку скиллов в проводнике.</summary>
    [RelayCommand]
    private void OpenSkills()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", _skills.SkillsDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessageViewModel { Role = "system", Text = $"🧩 Не удалось открыть папку: {ex.Message}" });
        }
    }

    private async Task RunAgentAsync(ChatListItemViewModel chat, string task)
    {
        EnsureWorkingFolder();
        var (skillsSection, skillsCount) = _skills.BuildSection("agent", "dev");
        if (skillsCount > 0)
            Messages.Add(new ChatMessageViewModel { Role = "system", Text = $"🧩 Скиллы агента: {skillsCount}" });
        var runner = new AgentRunner { SkillsSection = skillsSection, Provider = ProviderName };
        WireRunner(runner, null);

        var final = await runner.RunAsync(
            _bridge, chat.Id, task, _settings.WorkingFolder, _history,
            ConfirmActionAsync, _runCts!.Token);

        FileLog.Write($"агент: финал ({final.Length} симв.)");
        _history.AppendMessage(chat.Id, "assistant", final);
        Messages.Add(new ChatMessageViewModel { Role = "system", Text = $"🏁 ({_runStopwatch.Elapsed:mm\\:ss}) {final}" });
        RefreshFiles();
    }

    private async Task RunTeamAsync(ChatListItemViewModel chat, string task)
    {
        EnsureWorkingFolder();
        var orch = new OrchestratorRunner
        {
            RunnerFactory = author =>
            {
                var runner = new AgentRunner { Provider = ProviderName };
                WireRunner(runner, author);
                return runner;
            },
            SkillsForRole = role =>
            {
                var (section, count) = _skills.BuildSection(role);
                if (count > 0)
                    _dispatcher.BeginInvoke(() =>
                        Messages.Add(new ChatMessageViewModel { Role = "system", Text = $"🧩 Скиллы роли {role}: {count}" }));
                return (section, count);
            },
            Specialists = _skills.ListSpecialists(),
        };
        orch.StageStarted += s => _dispatcher.BeginInvoke(() =>
        {
            FileLog.Write("команда: " + s);
            Messages.Add(new ChatMessageViewModel { Role = "system", Text = s });
            ScrollToBottomRequested?.Invoke();
        });
        orch.StateSaved += st => _dispatcher.BeginInvoke(() => UpdatePlanItems(st));

        var final = await orch.RunTeamAsync(
            _bridge, chat.Id, task, _settings.WorkingFolder, _history,
            ConfirmActionAsync, _runCts!.Token,
            resume: _history.GetTeamState(chat.Id));

        FileLog.Write($"команда: финал ({final.Length} симв.)");
        _history.AppendMessage(chat.Id, "assistant", final);
        Messages.Add(new ChatMessageViewModel { Role = "system", Text = "🏁🏁 ИТОГ КОМАНДЫ:\n" + final });
        RefreshFiles();
    }

    private Task<bool> ConfirmActionAsync(ActionBlock a, string? detail)
    {
        // режимы: 2 — не спрашивать; 1 — только команды; 0 — всё; плюс «разрешить остаток задачи»
        if (_allowAllThisTask || _settings.ConfirmMode == 2 || (_settings.ConfirmMode == 1 && a.Tool != "run"))
            return Task.FromResult(true);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.BeginInvoke(() =>
        {
            var (header, sub) = a.Tool switch
            {
                "write_file" => ($"Записать файл: {a.Attr("path")}",
                    detail is null ? "Новый файл в рабочей папке." : "Изменение существующего файла. Дифф: − старые, + новые строки."),
                "run" => ($"Выполнить команду", $"Рабочая папка: {a.Attr("cwd") ?? "."}. Действие нельзя откатить."),
                _ => (a.Tool, ""),
            };
            var win = new ConfirmWindow(header, sub, detail ?? "") { Owner = System.Windows.Application.Current.MainWindow };
            var shown = win.ShowDialog() == true;
            if (shown && win.Allowed && win.AllowRest)
            {
                _allowAllThisTask = true;
                Messages.Add(new ChatMessageViewModel { Role = "system", Text = "🔓 Остальные действия этой задачи идут без подтверждений." });
            }
            tcs.SetResult(shown && win.Allowed);
        });
        return tcs.Task;
    }

    private void RememberProviderUrl(ChatListItemViewModel chat, JsonElement result)
    {
        if (result.TryGetProperty("data", out var d) && d.TryGetProperty("url", out var u) && u.GetString() is { } url
            && (url.Contains("/a/chat/s/") || url.Contains("chatgpt.com/c/")))
        {
            chat.ProviderChatUrl = url;
            _history.SetProviderChatUrl(chat.Id, url);
        }
    }

    // ── файловая панель ─────────────────────────────────────────────────────

    [RelayCommand]
    private void RefreshFiles()
    {
        Files.Clear();
        var root = _settings.WorkingFolder;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ToolExecutor.ShouldSkipName))
                         .Take(500))
                Files.Add(Path.GetRelativePath(root, f));
        }
        catch (Exception ex)
        {
            Files.Add($"(ошибка чтения: {ex.Message})");
        }
    }

    [RelayCommand]
    private void UndoChat()
    {
        var chat = SelectedChat;
        if (chat is null) return;
        var count = _history.RestoreChatFiles(chat.Id, _settings.WorkingFolder);
        Messages.Add(new ChatMessageViewModel { Role = "system", Text = count > 0 ? $"↩ Откачено файлов: {count}" : "↩ Нечего откатывать (нет версий от этого чата)" });
        RefreshFiles();
    }

    // ── ошибки ──────────────────────────────────────────────────────────────

    private static string FriendlyError(Exception ex) => ex switch
    {
        BridgeException be => be.Code switch
        {
            "notLoggedIn" => "DeepSeek: поле ввода не найдено. Проверь, что вход выполнен (chat.deepseek.com открыт и залогинен).",
            "selectorNotFound" => "DeepSeek: блок ответа не найден — похоже, изменилась вёрстка. Запусти диагностику: dotnet run --project src/Cli -- auto dump",
            "timeout" => "Ответ не получен за отведённое время.",
            "rateLimited" => "DeepSeek сообщает о лимите — подожди и повтори.",
            _ => $"Мост: {be.Message}",
        },
        AgentOperationException ae => ae.Code switch
        {
            "notLoggedIn" => "DeepSeek: поле ввода не найдено — проверь вход в браузере.",
            "selectorNotFound" => "DeepSeek не начал отвечать (вёрстка или сервисное сообщение). Автоповтор уже пробовал — если повторяется, открой чат DeepSeek и посмотри, что там.",
            "timeout" => "DeepSeek не завершил ответ за отведённое время.",
            "sendFailed" => "Промт не появился в чате DeepSeek — отправка не сработала (уже пробовали повторить).",
            "composerFailed" => "Не удалось вставить текст в поле ввода DeepSeek.",
            "rateLimited" => "DeepSeek сообщает о лимите — подожди и повтори.",
            _ => $"Мост [{ae.Code}]: {ae.Message}",
        },
        OperationCanceledException => "⏹ Остановлено пользователем.",
        TimeoutException => "Расширение не ответило (таймаут). Проверь, что Chrome открыт; если открыт — перезапусти расширение (кнопка ⟳ или chrome://extensions).",
        InvalidOperationException => "Расширение не подключено. Проверь, что Chrome запущен и расширение DevChat Bridge включено.",
        _ => $"Ошибка: {ex.Message}",
    };

    private sealed class BridgeException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
