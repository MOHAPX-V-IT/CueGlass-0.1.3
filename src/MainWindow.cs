using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace CueGlass;

public sealed partial class MainWindow : Window
{
    private static readonly Brush BackgroundBrush = ColorBrush("#111923"), CardBrush = ColorBrush("#8C23313F"),
        MutedBrush = ColorBrush("#9BAAC0"), AccentBrush = ColorBrush("#9BF0D4");
    private readonly Preferences prefs;
    private readonly bool diagnostic;
    private readonly PasswordBox key = new();
    private readonly CheckBox remember = Check("Сохранить ключ в Windows (DPAPI)", true);
    private readonly ComboBox model = new() { IsEditable = true };
    private readonly ComboBox micDevice = new(), outputDevice = new(), monitor = new();
    private readonly CheckBox microphone = Check("Мой микрофон"), systemAudio = Check("Звук собеседников"), screen = Check("Экран для подсказок"),
        autoHints = Check("Подсказки после паузы"), periodic = Check("Снимок каждые 15 с"), protection = Check("Исключать окно из захвата"),
        captureCompatibility = Check("Совместимость с Discord / захватом рабочего стола"),
        codeMode = Check("Код: развёрнутые ответы и исправления");
    private readonly TextBox context = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button start = Button("Начать", true), pause = Button("Пауза"), hint = Button("Подсказка  ↗", true);
    private readonly TextBlock status = Text("ГОТОВ К ПОДКЛЮЧЕНИЮ", 11), protectionStatus = Text("", 11), clock = Text("00:00", 12),
        screenStatus = Text("Экран ещё не передавался", 11), message = Text("", 12), latencyStatus = Text("", 11);
    private readonly Slider speechPause = new() { Minimum = 450, Maximum = 1200, TickFrequency = 50, IsSnapToTickEnabled = true };
    private readonly ScreenFrameCache frameCache = new(index => Task.Run(() => Native.CaptureScreen(index)));
    private readonly Stopwatch firstTextTimer = new();
    private Task<bool>? sendingFrame;
    private bool firstTextPending;
    private readonly ProgressBar meter = new() { Minimum = 0, Maximum = 1, Height = 3, Foreground = AccentBrush, Background = CardBrush };
    private readonly StackPanel cards = new();
    private readonly ScrollViewer cardScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox transcript = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox ask = new() { Height = 38, ToolTip = "Вопрос или инструкция. Enter — отправить." };
    private readonly TabControl tabs = new();
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<Control> lockedWhileRunning = new();
    private RealtimeClient? client;
    private AudioCapture? audio;
    private TextBlock? currentAnswer;
    private HwndSource? hwndSource;
    private IntPtr handle;
    private bool connecting, paused, generating, pendingHint, speechActive, stopping, closing;
    private DateTime connectedAt, lastFrame, requestAt;
    private int connectionVersion, committedVersion, hintedCommitVersion, captureVersion, hintOperation;
    private long totalTokens;
    private string? lastImageId;
    private string? lastImageUri;
    private DateTime lastImageUpload;

    public MainWindow(bool diagnostic = false)
    {
        this.diagnostic = diagnostic;
        prefs = diagnostic ? new Preferences() : Preferences.Load();
        Title = "CueGlass 0.1.3 — личный помощник";
        Width = 590; Height = 850; MinWidth = 500; MinHeight = 680;
        Background = Brushes.Transparent; Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ConfigureChrome();
        InstallStyles();
        BuildLayout();
        LoadSettings();
        WireEvents();
        ContentRendered += (_, _) => { ApplyWindowAppearance(); ApplyProtection(); };
        Activated += (_, _) => { if (!closing) ApplyProtection(); };
        StateChanged += (_, _) => { if (!closing) { ApplyWindowAppearance(); ApplyProtection(); } };
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            hwndSource = HwndSource.FromHwnd(handle);
            hwndSource?.AddHook(HotKeyHook);
            ApplyWindowAppearance();
            ApplyProtection();
            if (diagnostic) return;
            bool h = Native.RegisterHotKey(handle, 1, 0x4006, 0x20); // Ctrl+Shift+Space
            bool p = Native.RegisterHotKey(handle, 2, 0x4006, 0x50); // Ctrl+Shift+P
            bool v = Native.RegisterHotKey(handle, 3, 0x4006, 0x48); // Ctrl+Shift+H
            if (!h || !p || !v) Notify("Часть горячих клавиш занята другим приложением. Кнопки доступны.");
        };
        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true;
            ticker.Stop(); if (!diagnostic) SaveSettings(); await StopSession();
            Native.UnregisterHotKey(handle, 1); Native.UnregisterHotKey(handle, 2); Native.UnregisterHotKey(handle, 3);
            hwndSource?.RemoveHook(HotKeyHook);
            // Closing can run synchronously when no session exists. Schedule the
            // second Close after WPF finishes the original Closing callback.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        };
        ticker.Tick += async (_, _) => await Tick();
        ticker.Start();
    }

    private ScrollViewer SettingsPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 8, 0) };
        void Label(string text) { var t = Text(text, 11, FontWeights.SemiBold); t.Foreground = MutedBrush; t.Margin = new Thickness(0, 12, 0, 6); panel.Children.Add(t); }
        Label("OPENAI API-КЛЮЧ"); panel.Children.Add(key); panel.Children.Add(remember);
        var remove = Button("Удалить сохранённый ключ");
        remove.Click += (_, _) => { KeyVault.Delete(); key.Clear(); remember.IsChecked = false; Notify("Сохранённый API-ключ удалён с этого компьютера."); };
        panel.Children.Add(remove);
        Label("REALTIME-МОДЕЛЬ");
        model.Items.Add("gpt-realtime"); model.Items.Add("gpt-realtime-2.1"); panel.Children.Add(model);
        var apiNote = Text("API оплачивается с твоего баланса. Вход через ChatGPT не включает аудио этого режима.", 11); apiNote.TextWrapping = TextWrapping.Wrap; apiNote.Foreground = MutedBrush; panel.Children.Add(apiNote);
        Label("ЧТО ПЕРЕДАВАТЬ В OPENAI");
        panel.Children.Add(microphone); panel.Children.Add(micDevice);
        panel.Children.Add(systemAudio); panel.Children.Add(outputDevice);
        panel.Children.Add(screen); panel.Children.Add(monitor); panel.Children.Add(periodic); panel.Children.Add(autoHints);
        Label("ПАУЗА ПОСЛЕ РЕЧИ");
        var pauseLabel = Text("", 11); pauseLabel.Foreground = MutedBrush;
        speechPause.ValueChanged += (_, _) => pauseLabel.Text = ((int)speechPause.Value) + " мс · меньше — быстрее, больше — терпимее к паузам";
        panel.Children.Add(speechPause); panel.Children.Add(pauseLabel);
        panel.Children.Add(codeMode);
        Label("КОНТЕКСТ ЗВОНКА / ТВОИ ЗАМЕТКИ"); panel.Children.Add(context);
        var import = Button("Добавить заметки или исходный код");
        import.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Заметки и код|*.txt;*.md;*.py;*.js;*.jsx;*.ts;*.tsx;*.cs;*.go;*.java;*.sql;*.html;*.css;*.json;*.rs|Все файлы|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                if (new FileInfo(dialog.FileName).Length > 200000) { Notify("Файл слишком большой. Выбери текст до 200 КБ."); return; }
                context.Text = (context.Text + "\nФайл: " + Path.GetFileName(dialog.FileName) + "\n" + File.ReadAllText(dialog.FileName)).Trim();
            }
            catch (Exception e) { Notify(e.Message); }
        };
        panel.Children.Add(import);
        Label("ОКНО"); panel.Children.Add(protection); panel.Children.Add(captureCompatibility);
        AddAppearanceSettings(panel);
        var note = Text("Проверка флага Windows не гарантирует скрытие во всех программах захвата. Проверь конкретный звонок. Для чистого звука используй наушники. После смены аудиоустройства переподключись.", 11);
        note.TextWrapping = TextWrapping.Wrap; note.Foreground = MutedBrush; panel.Children.Add(note);
        var discordNote = Text("Если в Discord остаётся чёрный прямоугольник: включи совместимость выше и перезапусти демонстрацию. Если не помогло — выбери показ конкретного рабочего приложения вместо всего экрана. Discord может использовать разные способы захвата; результат проверяется на стороне зрителя.", 11);
        discordNote.TextWrapping = TextWrapping.Wrap; discordNote.Foreground = MutedBrush; discordNote.Margin = new Thickness(0, 10, 0, 0); panel.Children.Add(discordNote);
        lockedWhileRunning.AddRange(new Control[] { key, remember, model, micDevice, outputDevice, microphone, systemAudio, screen, monitor, periodic, autoHints, speechPause, codeMode, context, import, remove });
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void LoadSettings()
    {
        key.Password = diagnostic ? "" : KeyVault.Read(); remember.IsChecked = key.Password.Length > 0;
        model.Text = prefs.Model; context.Text = prefs.Context;
        microphone.IsChecked = prefs.Microphone; systemAudio.IsChecked = prefs.SystemAudio; screen.IsChecked = prefs.Screen;
        autoHints.IsChecked = prefs.AutoHints; periodic.IsChecked = prefs.PeriodicScreen; protection.IsChecked = prefs.ProtectWindow;
        captureCompatibility.IsChecked = prefs.CaptureCompatibility;
        speechPause.Value = Math.Clamp(prefs.SpeechPauseMs, 450, 1200);
        codeMode.IsChecked = prefs.CodeMode;
        Opacity = 1; glassToggle.IsChecked = prefs.GlassEffect; density.Value = Math.Clamp(prefs.GlassDensity, 0.45, 0.94);
        try
        {
            micDevice.ItemsSource = AudioCapture.ListDevices(DataFlow.Capture);
            outputDevice.ItemsSource = AudioCapture.ListDevices(DataFlow.Render);
            micDevice.SelectedItem = ((IEnumerable<AudioDevice>)micDevice.ItemsSource).FirstOrDefault(d => d.Id == prefs.MicId); micDevice.SelectedIndex = Math.Max(0, micDevice.SelectedIndex);
            outputDevice.SelectedItem = ((IEnumerable<AudioDevice>)outputDevice.ItemsSource).FirstOrDefault(d => d.Id == prefs.OutputId); outputDevice.SelectedIndex = Math.Max(0, outputDevice.SelectedIndex);
        }
        catch (Exception e) { Notify("Аудиоустройства: " + e.Message); }
        int n = 0;
        foreach (var s in System.Windows.Forms.Screen.AllScreens) monitor.Items.Add($"Экран {++n} · {s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " · основной" : ""));
        monitor.SelectedIndex = Array.FindIndex(System.Windows.Forms.Screen.AllScreens, s => s.Primary);
        if (monitor.SelectedIndex < 0) monitor.SelectedIndex = 0;
        if (key.Password.Length == 0) tabs.SelectedIndex = 2;
    }

    private void SaveSettings()
    {
        prefs.Model = model.Text.Trim(); prefs.Context = context.Text;
        prefs.MicId = (micDevice.SelectedItem as AudioDevice)?.Id ?? "";
        prefs.OutputId = (outputDevice.SelectedItem as AudioDevice)?.Id ?? "";
        prefs.Microphone = On(microphone); prefs.SystemAudio = On(systemAudio); prefs.Screen = On(screen);
        prefs.AutoHints = On(autoHints); prefs.PeriodicScreen = On(periodic); prefs.ProtectWindow = On(protection);
        prefs.CaptureCompatibility = On(captureCompatibility);
        prefs.SpeechPauseMs = (int)speechPause.Value;
        prefs.CodeMode = On(codeMode);
        prefs.GlassEffect = On(glassToggle); prefs.GlassDensity = density.Value;
        try { prefs.Save(); } catch (Exception e) { Notify("Настройки не сохранены: " + e.Message); }
    }

    private void WireEvents()
    {
        start.Click += async (_, _) => { if (client != null) await StopSession(); else await StartSession(); };
        pause.Click += async (_, _) => await TogglePause();
        hint.Click += async (_, _) => await RequestHint(ask.Text.Trim());
        ask.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await RequestHint(ask.Text.Trim()); } };
        protection.Click += (_, _) => ApplyProtection();
        captureCompatibility.Click += (_, _) => ApplyProtection();
    }

    private async Task StartSession()
    {
        if (connecting || stopping) return;
        string secret = key.Password.Trim();
        if (secret.Length < 10) { tabs.SelectedIndex = 2; Notify("Введи API-ключ OpenAI в настройках."); return; }
        if (string.IsNullOrWhiteSpace(model.Text)) { Notify("Укажи Realtime-модель."); return; }
        connecting = true; start.IsEnabled = false; status.Text = "ПОДКЛЮЧЕНИЕ…"; message.Text = "";
        SaveSettings();
        foreach (var c in lockedWhileRunning) c.IsEnabled = false;
        int version = ++connectionVersion;
        frameCache.Invalidate(); sendingFrame = null; ++captureVersion;
        try
        {
            if (On(remember)) KeyVault.Save(secret); else KeyVault.Delete();
            client = new RealtimeClient();
            client.Event += (type, data) => Dispatcher.BeginInvoke(new Action(() => { if (version == connectionVersion) HandleEvent(type, data); }));
            client.Failed += error => Dispatcher.BeginInvoke(new Action(async () => { if (version == connectionVersion) { await StopSession(); Notify("Соединение: " + error); } }));
            await client.Connect(secret, prefs.Model, prefs.Context, speechPauseMs: prefs.SpeechPauseMs, codeMode: prefs.CodeMode);
            if (closing || version != connectionVersion) return;
            audio = new AudioCapture();
            bool audioPaused = false;
            // Capture callbacks run on a worker thread; a single volatile flag gates all audio.
            audio.Data += pcm => { if (!System.Threading.Volatile.Read(ref audioPaused)) client?.AppendAudio(pcm); };
            pauseAudio = value => System.Threading.Volatile.Write(ref audioPaused, value);
            audio.Level += level => Dispatcher.BeginInvoke(new Action(() => { if (version == connectionVersion) meter.Value = paused ? 0 : level; }));
            audio.Failed += error => Dispatcher.BeginInvoke(new Action(async () => { if (version == connectionVersion) { await StopSession(); Notify("Захват звука остановлен: " + error); } }));
            audio.Start(prefs.Microphone, prefs.SystemAudio, prefs.MicId, prefs.OutputId);
            connectedAt = DateTime.UtcNow; lastFrame = DateTime.MinValue; totalTokens = 0;
            paused = false; generating = false; pendingHint = false; speechActive = false;
            committedVersion = hintedCommitVersion = 0;
            firstTextPending = false; latencyStatus.Text = "";
            status.Text = "В ЭФИРЕ · ДАННЫЕ → OPENAI"; status.Foreground = AccentBrush;
            start.Content = "Завершить"; pause.IsEnabled = true; hint.IsEnabled = true; tabs.SelectedIndex = 0;
            AddCard("СЕССИЯ НАЧАТА · " + DateTime.Now.ToString("HH:mm"), "Слушаю выбранные источники. Для подсказки нажми Ctrl+Shift+Space или задай вопрос внизу.");
            if (prefs.Screen) await SendFrame();
        }
        catch (Exception e) { await StopSession(); Notify("Не удалось начать: " + e.Message + " Проверь ключ, баланс и доступ к Realtime."); }
        finally { connecting = false; start.IsEnabled = true; }
    }

    private Action<bool>? pauseAudio;
    private async Task StopSession()
    {
        if (stopping) return;
        stopping = true; ++connectionVersion;
        frameCache.Invalidate(); sendingFrame = null; ++captureVersion; ++hintOperation; firstTextPending = false; firstTextTimer.Reset();
        pauseAudio?.Invoke(true); paused = true;
        var previousAudio = audio; audio = null;
        var previousClient = client; client = null;
        try
        {
            if (previousAudio != null) await previousAudio.DisposeAsync();
            if (previousClient != null) await previousClient.DisposeAsync();
        }
        finally
        {
            generating = false; pendingHint = false; speechActive = false; lastImageId = null; lastImageUri = null; pauseAudio = null;
            meter.Value = 0; status.Text = "ПЕРЕДАЧА ОСТАНОВЛЕНА"; status.Foreground = MutedBrush;
            start.Content = "Начать"; pause.Content = "Пауза"; pause.IsEnabled = false; hint.IsEnabled = false;
            foreach (var c in lockedWhileRunning) c.IsEnabled = true;
            stopping = false;
        }
    }

    private async Task TogglePause()
    {
        if (client?.IsReady != true) return;
        paused = !paused; client.SetPaused(paused); audio?.SetPaused(paused); pauseAudio?.Invoke(paused); pendingHint = false; speechActive = false;
        frameCache.Invalidate(); sendingFrame = null; ++captureVersion; ++hintOperation; firstTextPending = false;
        client.Queue(new { type = "input_audio_buffer.clear" });
        if (paused && generating) client.Queue(new { type = "response.cancel" });
        if (paused) { pause.Content = "Продолжить"; status.Text = "ПАУЗА · ЗВУК И ЭКРАН НЕ ПЕРЕДАЮТСЯ"; meter.Value = 0; hint.IsEnabled = false; }
        else { pause.Content = "Пауза"; status.Text = "В ЭФИРЕ · ДАННЫЕ → OPENAI"; hint.IsEnabled = true; if (prefs.Screen) await SendFrame(); }
    }

    private async Task RequestHint(string question = "", bool automatic = false)
    {
        if (client?.IsReady != true || paused || connecting) return;
        if (automatic && committedVersion <= hintedCommitVersion) return;
        if (generating) { if (automatic) pendingHint = true; else Notify("Дождись текущего ответа."); return; }
        int requestCommit = committedVersion;
        int operation = ++hintOperation;
        generating = true; pendingHint = false; requestAt = DateTime.UtcNow;
        firstTextTimer.Restart(); firstTextPending = true;
        if (prefs.Screen && !await SendFrame(forceFresh: !automatic))
        {
            if (operation == hintOperation) { generating = false; firstTextPending = false; }
            return;
        }
        if (operation != hintOperation) return;
        if (client == null || paused) { generating = false; return; }
        if (automatic && speechActive) { generating = false; pendingHint = true; firstTextPending = false; return; }
        if (automatic) hintedCommitVersion = Math.Max(hintedCommitVersion, requestCommit);
        if (question.Length > 0) { client.Queue(Protocol.Text(question)); ask.Clear(); }
        else if (!automatic) client.Queue(Protocol.Text("Дай следующую полезную подсказку по последнему вопросу звонка и текущему экрану. Если контекста недостаточно, скажи, что уточнить."));
        client.Queue(new { type = "response.create", response = new { output_modalities = new[] { "text" } } });
        status.Text = "ГОТОВЛЮ ПОДСКАЗКУ…";
    }

    private Task<bool> SendFrame(bool forceFresh = false)
    {
        // A hint can share a frame operation already started by the timer.
        // Do not drop that hint just because screenshot capture is in progress.
        if (sendingFrame is { IsCompleted: false }) return sendingFrame;
        sendingFrame = SendFrameCore(forceFresh);
        return sendingFrame;
    }

    private async Task<bool> SendFrameCore(bool forceFresh)
    {
        if (paused || client?.IsReady != true) return false;
        int index = monitor.SelectedIndex, version = connectionVersion, captureGeneration = captureVersion;
        try
        {
            var frame = await frameCache.Get(index, forceFresh ? TimeSpan.Zero : TimeSpan.FromMilliseconds(1500));
            if (version != connectionVersion || captureGeneration != captureVersion || paused || client?.IsReady != true) return false;
            if (lastImageUri == frame.Uri && (DateTime.UtcNow - lastImageUpload).TotalSeconds < 30)
            {
                lastFrame = DateTime.UtcNow;
                screenStatus.Text = "Экран не изменился · использую уже отправленный снимок";
                return true;
            }
            if (lastImageId != null) client.Queue(new { type = "conversation.item.delete", item_id = lastImageId });
            lastImageId = "screen_" + Guid.NewGuid().ToString("N");
            client.Queue(new { type = "conversation.item.create", item = new { id = lastImageId, type = "message", role = "user", content = new[] { new { type = "input_image", image_url = frame.Uri } } } });
            lastImageUri = frame.Uri; lastImageUpload = DateTime.UtcNow;
            lastFrame = DateTime.UtcNow; screenStatus.Text = "Снимок экрана отправлен · " + DateTime.Now.ToString("HH:mm:ss");
            return true;
        }
        catch (Exception e) { Notify("Снимок не отправлен: " + e.Message); return false; }
    }

    private async Task PrepareFrame()
    {
        if (paused || !prefs.Screen || client?.IsReady != true) return;
        int index = monitor.SelectedIndex;
        // Prefetch only into memory. Sending still requires SendFrame's session
        // and pause checks, so a late capture cannot leak across a paused session.
        try { await frameCache.Get(index, TimeSpan.FromMilliseconds(1500)); }
        catch { /* A requested hint will report any persistent screenshot error. */ }
    }

    private async Task Tick()
    {
        if (connecting || stopping || client?.IsReady != true) return;
        var elapsed = DateTime.UtcNow - connectedAt; clock.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        if (elapsed.TotalMinutes >= 55) { await StopSession(); Notify("Сессия остановлена через 55 минут. Начни новую для продолжения."); return; }
        if (generating && (DateTime.UtcNow - requestAt).TotalSeconds > 60) { await StopSession(); Notify("Ответ не завершился за минуту. Переподключись."); return; }
        if (!paused && speechActive && prefs.Screen) await PrepareFrame();
        if (!paused && !generating && prefs.Screen && prefs.PeriodicScreen && (DateTime.UtcNow - lastFrame).TotalSeconds >= 15) await SendFrame();
    }

    private void HandleEvent(string type, JsonElement data)
    {
        switch (type)
        {
            case "input_audio_buffer.speech_started": speechActive = true; _ = PrepareFrame(); break;
            case "input_audio_buffer.speech_stopped": speechActive = false; break;
            case "input_audio_buffer.committed":
                committedVersion++;
                if (prefs.AutoHints && !paused) ScheduleAutoHint();
                break;
            case "conversation.item.input_audio_transcription.completed":
                string text = data.GetProperty("transcript").GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                {
                    transcript.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + "\n\n");
                    if (transcript.Text.Length > 60000) transcript.Text = transcript.Text[^40000..];
                    transcript.ScrollToEnd();
                }
                break;
            case "conversation.item.input_audio_transcription.failed": Notify("Не удалось расшифровать фрагмент речи. Проверь качество звука."); break;
            case "response.created":
                generating = true; requestAt = DateTime.UtcNow;
                currentAnswer = AddCard("ПОДСКАЗКА · " + DateTime.Now.ToString("HH:mm:ss"), "");
                break;
            case "response.output_text.delta":
            case "response.text.delta":
                string delta = data.GetProperty("delta").GetString() ?? "";
                if (firstTextPending && !string.IsNullOrWhiteSpace(delta))
                {
                    firstTextPending = false; firstTextTimer.Stop();
                    latencyStatus.Text = $"Первый текст: {firstTextTimer.Elapsed.TotalSeconds:0.00} с от запуска подсказки";
                }
                if (currentAnswer != null)
                {
                    bool bottom = cardScroll.ScrollableHeight - cardScroll.VerticalOffset < 80;
                    currentAnswer.Text += delta;
                    if (bottom) cardScroll.ScrollToEnd();
                }
                break;
            case "response.done":
                generating = false; firstTextPending = false;
                var response = data.GetProperty("response");
                string responseStatus = response.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                if (responseStatus != "completed" && responseStatus != "cancelled")
                    Notify("Ответ " + responseStatus + (response.TryGetProperty("status_details", out var details) ? ": " + details.ToString() : ""));
                if (response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("total_tokens", out var tokens)) totalTokens += tokens.GetInt64();
                if (!paused) status.Text = "В ЭФИРЕ · ТОКЕНОВ: " + totalTokens.ToString("N0");
                if (pendingHint && !paused) ScheduleAutoHint();
                break;
            case "error":
                generating = false; pendingHint = false; firstTextPending = false;
                Notify("OpenAI: " + data.GetProperty("error").GetProperty("message").GetString());
                break;
        }
    }

    private void ScheduleAutoHint()
    {
        if (!paused && !speechActive) _ = RequestHint(automatic: true);
    }

    private void ApplyProtection()
    {
        if (handle == IntPtr.Zero) return;
        string? error = Native.ApplyCaptureProtection(handle, On(protection));
        bool compat = On(protection) && On(captureCompatibility);
        string? compatibilityError = Native.DdaExclusion(handle, compat, out bool verified);
        protectionStatus.Text = error != null ? "Исключение окна НЕ подтверждено: " + error
            : !On(protection) ? "Окно может быть видно в демонстрации"
            : compatibilityError != null && compat ? "Основной флаг установлен; " + compatibilityError
            : "Защита окна включена" + (verified ? " · совместимость Discord" : "");
        protectionStatus.Foreground = error != null || (compat && compatibilityError != null) || !On(protection) ? ColorBrush("#F2BE79") : AccentBrush;
    }
    private IntPtr HotKeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x007E || msg == 0x031E) // display change / DWM composition change
        {
            _ = Dispatcher.BeginInvoke(new Action(() => { if (!closing) ApplyProtection(); }));
            return IntPtr.Zero;
        }
        if (msg != 0x0312) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case 1: _ = RequestHint(); break;
            case 2: _ = TogglePause(); break;
            case 3: if (IsVisible) Hide(); else { Show(); Activate(); ApplyProtection(); } break;
        }
        return IntPtr.Zero;
    }
    internal TextBlock AddCard(string title, string body)
    {
        var content = new StackPanel();
        var label = Text(title, 10, FontWeights.SemiBold); label.Foreground = AccentBrush; label.Margin = new Thickness(0, 0, 0, 9); content.Children.Add(label);
        var answer = Text(body, 15); answer.TextWrapping = TextWrapping.Wrap; answer.LineHeight = 23; content.Children.Add(answer);
        var border = new Border
        {
            Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#302D4554"), (Color)ColorConverter.ConvertFromString("#A0121D2A"), 120),
            BorderBrush = (Brush)FindResource("Rim"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16), Padding = new Thickness(18), Margin = new Thickness(0, 10, 2, 0), Child = content
        };
        border.MouseEnter += (_, _) => border.BorderBrush = ColorBrush("#7089C7C0");
        border.MouseLeave += (_, _) => border.BorderBrush = (Brush)FindResource("Rim");
        border.ContextMenu = new ContextMenu();
        var copy = new MenuItem { Header = "Копировать ответ" }; copy.Click += (_, _) => { if (answer.Text.Length > 0) Clipboard.SetText(answer.Text); }; border.ContextMenu.Items.Add(copy);
        cards.Children.Add(border);
        while (cards.Children.Count > 40) cards.Children.RemoveAt(0);
        cardScroll.ScrollToEnd(); return answer;
    }
    internal void PreviewContent()
    {
        cards.Children.Clear();
        tabs.SelectedIndex = 0;
        AddCard("ВОПРОС · ДЕМОНСТРАЦИОННЫЕ ДАННЫЕ", "Как устроен RAG и когда его стоит использовать?");
        AddCard("ПРИМЕР ПОДСКАЗКИ · ДЕМОНСТРАЦИОННЫЕ ДАННЫЕ", "RAG помогает модели отвечать по актуальным документам.\n\n• Сначала находим подходящие фрагменты.\n• Передаём их модели вместе с вопросом.\n• Возвращаем ответ со ссылками на источники.\n\nСтоит уточнить: как часто меняются документы и какие права доступа нужны?");
    }
    internal void PreviewSettings() => tabs.SelectedIndex = 2;
    private void Notify(string text) => message.Text = text;
    private static bool On(CheckBox control) => control.IsChecked == true;
    private static SolidColorBrush ColorBrush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    private static TextBlock Text(string text, double size, FontWeight? weight = null) => new() { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Foreground = Brushes.White };
    private static CheckBox Check(string text, bool value = false) => new() { Content = text, IsChecked = value, Foreground = Brushes.White, Margin = new Thickness(0, 7, 0, 7) };
    private static Button Button(string text, bool accent = false) => new()
    {
        Content = text, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(5, 0, 0, 0),
        Background = accent ? AccentBrush : CardBrush, Foreground = accent ? BackgroundBrush : Brushes.White,
        BorderThickness = new Thickness(1), BorderBrush = ColorBrush("#25FFFFFF"), Cursor = Cursors.Hand, FontWeight = FontWeights.SemiBold
    };
}
