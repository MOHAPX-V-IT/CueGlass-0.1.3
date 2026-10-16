using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Shapes;

namespace CueGlass;

public sealed partial class MainWindow
{
    private readonly CheckBox glassToggle = Check("Стеклянный фон", true);
    private readonly Slider density = new() { Minimum = 0.45, Maximum = 0.94, Value = 0.68, TickFrequency = 0.01 };
    private Border? windowFrame;
    private bool glassAvailable;
    internal bool GlassAvailable => glassAvailable;

    private void ConfigureChrome()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 74, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(18),
            UseAeroCaptionButtons = false
        });
        SizeChanged += (_, _) => RoundCurrentWindow();
        // Keep the framework's native resize, move, snap and system-menu handling.
        MaxHeight = SystemParameters.WorkArea.Height;
    }

    private void RoundCurrentWindow()
    {
        if (handle == IntPtr.Zero || closing) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Native.RoundWindow(handle, (int)(18 * dpi), WindowState == WindowState.Maximized);
    }

    private void ApplyWindowAppearance()
    {
        if (windowFrame == null) return;
        bool enabled = On(glassToggle) && !SystemParameters.HighContrast;
        if (handle != IntPtr.Zero)
        {
            if (hwndSource?.CompositionTarget != null) hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;
            glassAvailable = Native.SetGlass(handle, enabled);
            RoundCurrentWindow();
        }
        double amount = enabled && glassAvailable ? density.Value : 0.98;
        var top = Color.FromArgb((byte)(amount * 255), 18, 30, 42);
        var bottom = Color.FromArgb((byte)Math.Min(255, (amount + .10) * 255), 12, 19, 30);
        windowFrame.Background = new LinearGradientBrush(top, bottom, 115);
    }

    private void AddAppearanceSettings(StackPanel panel)
    {
        panel.Children.Add(glassToggle);
        var label = Text("Плотность стекла · текст всегда остаётся чётким", 11); label.Foreground = MutedBrush;
        label.Margin = new Thickness(0, 8, 0, 2); panel.Children.Add(label); panel.Children.Add(density);
        glassToggle.Click += (_, _) => { ApplyWindowAppearance(); ApplyProtection(); };
        density.ValueChanged += (_, _) => ApplyWindowAppearance();
    }

    private void InstallStyles()
    {
        var dictionary = (ResourceDictionary)Application.LoadComponent(new Uri("/CueGlass;component/Theme.xaml", UriKind.Relative));
        Resources.MergedDictionaries.Add(dictionary);
    }

    private void BuildLayout()
    {
        windowFrame = new Border
        {
            CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("Rim"), Background = BackgroundBrush,
            SnapsToDevicePixels = true
        };
        Content = windowFrame;
        var root = new Grid { Margin = new Thickness(20, 15, 20, 16) };
        windowFrame.Child = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Height = 52, Margin = new Thickness(0, 0, 0, 17), LastChildFill = true };
        var chrome = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, -8, 0) };
        chrome.Children.Add(WindowButton("\uE921", "Свернуть", () => SystemCommands.MinimizeWindow(this)));
        chrome.Children.Add(WindowButton("\uE922", "Развернуть / восстановить", () =>
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this);
        }));
        chrome.Children.Add(WindowButton("\uE8BB", "Закрыть", Close));
        WindowChrome.SetIsHitTestVisibleInChrome(chrome, true);
        DockPanel.SetDock(chrome, Dock.Right); header.Children.Add(chrome);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var mark = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(12), BorderBrush = ColorBrush("#65CAF6E6"), BorderThickness = new Thickness(1), Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#364CAF9C"), (Color)ColorConverter.ConvertFromString("#102C626D"), 120), Margin = new Thickness(0, 0, 12, 0) };
        mark.Child = new Path { Data = Geometry.Parse("M 22,9 C 12,4 7,11 7,18 C 7,26 14,30 22,25 M 17,17 L 26,17 M 21,13 L 26,17 L 21,21"), Stroke = AccentBrush, StrokeThickness = 1.65, Stretch = Stretch.Uniform, Margin = new Thickness(8) };
        brand.Children.Add(mark);
        var wordmark = new StackPanel();
        wordmark.Children.Add(Text("CueGlass", 21, FontWeights.SemiBold));
        var tagline = Text("Подсказки в твоём ритме", 11); tagline.Foreground = MutedBrush; wordmark.Children.Add(tagline);
        brand.Children.Add(wordmark); header.Children.Add(brand); root.Children.Add(header);

        var bar = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(start); controls.Children.Add(pause); DockPanel.SetDock(controls, Dock.Right); bar.Children.Add(controls);
        status.VerticalAlignment = VerticalAlignment.Center; status.Foreground = AccentBrush; status.FontSize = 10;
        status.Text = "ГОТОВ К РАЗГОВОРУ";
        bar.Children.Add(status);
        var sessionPanel = new Border { Background = (Brush)FindResource("Pane"), BorderBrush = ColorBrush("#22FFFFFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 10, 10, 10), Child = bar };
        Grid.SetRow(sessionPanel, 1); root.Children.Add(sessionPanel);
        meter.Margin = new Thickness(8, 6, 8, 0); meter.Height = 2;
        Grid.SetRow(meter, 2); root.Children.Add(meter);

        tabs.Margin = new Thickness(0, 15, 0, 12); tabs.Background = Brushes.Transparent;
        context.VerticalContentAlignment = VerticalAlignment.Top; transcript.VerticalContentAlignment = VerticalAlignment.Top;
        cardScroll.Content = cards;
        tabs.Items.Add(new TabItem { Header = TabLabel("\uE8F2", "Подсказки"), Content = cardScroll });
        var transcriptPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var caption = Text("Общий канал · реплики участников", 11); caption.Foreground = MutedBrush; caption.Margin = new Thickness(2, 0, 0, 8);
        DockPanel.SetDock(caption, Dock.Top); transcriptPanel.Children.Add(caption); transcriptPanel.Children.Add(transcript);
        tabs.Items.Add(new TabItem { Header = TabLabel("\uE720", "Речь"), Content = transcriptPanel });
        tabs.Items.Add(new TabItem { Header = TabLabel("\uE713", "Настройки"), Content = SettingsPanel() });
        Grid.SetRow(tabs, 3); root.Children.Add(tabs);

        ask.Height = 46; ask.Background = Brushes.Transparent; ask.BorderThickness = new Thickness(0); ask.Padding = new Thickness(12, 10, 12, 10);
        AutomationProperties.SetName(ask, "Вопрос помощнику");
        hint.Content = "Подсказать  ↗"; hint.Margin = new Thickness(4); hint.Padding = new Thickness(13, 9, 13, 9);
        hint.ToolTip = "Подсказка по экрану и разговору · Ctrl+Shift+Space";
        var composer = new DockPanel(); DockPanel.SetDock(hint, Dock.Right); composer.Children.Add(hint);
        var inputHost = new Grid(); inputHost.Children.Add(ask);
        var placeholder = Text("Спроси или уточни ответ…", 12); placeholder.Foreground = MutedBrush; placeholder.Margin = new Thickness(12, 0, 0, 0); placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false;
        ask.TextChanged += (_, _) => placeholder.Visibility = ask.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        inputHost.Children.Add(placeholder); composer.Children.Add(inputHost);
        var composerFrame = new Border { CornerRadius = new CornerRadius(14), BorderBrush = ColorBrush("#3BADC9C4"), BorderThickness = new Thickness(1), Background = ColorBrush("#9A111D29"), Child = composer, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(composerFrame, 4); root.Children.Add(composerFrame);

        var footer = new StackPanel();
        message.TextWrapping = TextWrapping.Wrap; message.Foreground = ColorBrush("#F2C491"); message.Margin = new Thickness(0, 0, 0, 4); footer.Children.Add(message);
        latencyStatus.Foreground = MutedBrush; latencyStatus.Margin = new Thickness(0, 0, 0, 4); footer.Children.Add(latencyStatus);
        protectionStatus.FontSize = 10; protectionStatus.TextWrapping = TextWrapping.Wrap; footer.Children.Add(protectionStatus);
        screenStatus.FontSize = 10; screenStatus.Foreground = MutedBrush; screenStatus.Margin = new Thickness(0, 4, 0, 8); footer.Children.Add(screenStatus);
        var shortcuts = new DockPanel();
        DockPanel.SetDock(clock, Dock.Right); clock.FontFamily = new FontFamily("Consolas"); clock.Foreground = MutedBrush; shortcuts.Children.Add(clock);
        var keys = Text("⌃ ⇧ Space  подсказка     ⌃ ⇧ P  пауза     ⌃ ⇧ H  окно", 10); keys.Foreground = ColorBrush("#8195A8"); shortcuts.Children.Add(keys); footer.Children.Add(shortcuts);
        Grid.SetRow(footer, 5); root.Children.Add(footer);

        AddCard("ТВОЙ ПОМОЩНИК НА ЗВОНКЕ", "Слушает разговор. Видит задачу.\nПомогает сформулировать ответ.\n\nДобавь ключ и контекст в настройках, затем нажми «Начать».");
        pause.IsEnabled = false; hint.IsEnabled = false;
        foreach (var combo in new[] { model, micDevice, outputDevice, monitor })
            combo.DropDownOpened += (_, _) => _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (combo.Template.FindName("PART_Popup", combo) is Popup popup && popup.Child is FrameworkElement child) ProtectPopup(child);
            }));
    }

    private void ProtectPopup(FrameworkElement content)
    {
        if (!On(protection)) return;
        if (PresentationSource.FromVisual(content) is HwndSource source)
        {
            Native.ApplyCaptureProtection(source.Handle, true);
            Native.DdaExclusion(source.Handle, On(captureCompatibility), out _);
        }
    }

    private Button WindowButton(string glyph, string label, Action action)
    {
        var button = new Button { Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Width = 31, Height = 30, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, ToolTip = label, Foreground = MutedBrush, Focusable = false };
        AutomationProperties.SetName(button, label);
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += (_, _) => action();
        return button;
    }

    private static StackPanel TabLabel(string glyph, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        panel.Children.Add(new TextBlock { Text = label });
        return panel;
    }
}
