using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OnePace.App.Services;
using OnePace.App.ViewModels;
using OnePace.Core;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace OnePace.App;

public partial class MainWindow : FluentWindow, IUiHost
{
    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    readonly MainViewModel _vm;
    readonly ContentDialogService _dialogs = new();
    readonly SnackbarService _snackbar = new();
    readonly PreviewSpec? _preview;
    bool _closeConfirmed;
    bool _dialogOpen;
    bool _syncingSource;
    EpisodeItem? _lastChecked;

    public MainWindow(PreviewSpec? preview)
    {
        _preview = preview;
        var settings = Settings.Load();
        preview?.ApplyTo(settings);
        var index = IndexStore.Load(() => typeof(MainWindow).Assembly.GetManifestResourceStream("episode_index.json"));

        InitializeComponent();
        ApplyTheme(settings.Appearance);
        _vm = new MainViewModel(this, settings, index);
        DataContext = _vm;
        _vm.SettingsPage.ThemeChanged += ApplyTheme;
        _vm.PropertyChanged += OnVmChanged;
        Title = $"One Pace Downloader {AppInfo.Version}";

        _dialogs.SetDialogHost(DialogHost);
        _snackbar.SetSnackbarPresenter(SnackbarHost);
        ApplicationThemeManager.Changed += (theme, _) => ThemeBrushes.Apply(theme == ApplicationTheme.Dark);

        RestorePlacement(settings.Window);
        SyncSourceSelection();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKey;
        SizeChanged += (_, _) => IsCompact = ActualWidth < 1180;
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm.Settings.Appearance == "System")
            SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        await _vm.RescanLibraryAsync();
        if (_preview != null)
        {
            await _preview.RunAsync(this, _vm);
            return;
        }
        await Task.Delay(2500);
        await _vm.CheckForUpdatesAsync();
    }

    // ------------------------------------------------------------ theme --

    void ApplyTheme(string appearance)
    {
        bool dark = appearance switch
        {
            "Dark" => true,
            "Light" => false,
            _ => SystemUsesDarkTheme(),
        };
        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, true);
        ThemeBrushes.Apply(dark);
        if (!IsLoaded) return;
        try
        {
            SystemThemeWatcher.UnWatch(this);
            if (appearance == "System") SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        }
        catch (Exception) { /* not watched yet */ }
    }

    static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    // ---------------------------------------------------------- layout --

    void OnMainAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The details pane takes a share of the space so it grows on big
        // windows instead of leaving the episode list mostly empty.
        double want = Math.Clamp(e.NewSize.Width * 0.38, 340, 520);
        if (Math.Abs(DetailColumn.Width.Value - want) > 0.5) DetailColumn.Width = new GridLength(want);
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedSource):
                SyncSourceSelection();
                break;
            case nameof(MainViewModel.SettingsOpen):
                SyncSourceSelection();
                break;
            case nameof(MainViewModel.Detail):
                DetailScroll.ScrollToTop();
                break;
            case nameof(MainViewModel.SelectedArc):
                _lastChecked = null;
                if (EpisodeList.Items.Count > 0) EpisodeList.ScrollIntoView(EpisodeList.Items[0]);
                break;
        }
    }

    /// <summary>The sources list shows no selection while Settings is open,
    /// like a NavigationView.</summary>
    void SyncSourceSelection()
    {
        _syncingSource = true;
        SourceList.SelectedItem = _vm.SettingsOpen ? null : _vm.SelectedSource;
        _syncingSource = false;
    }

    void OnSourceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSource || SourceList.SelectedItem is not SourceItem item) return;
        if (item == _vm.SelectedSource) _vm.SettingsOpen = false;
        else _vm.SelectedSource = item;
    }

    void OnSourceClicked(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SettingsOpen && SourceList.SelectedItem == null && _vm.SelectedSource != null)
        {
            // Clicking the source you were on before opening Settings.
            if ((e.OriginalSource as FrameworkElement)?.DataContext == _vm.SelectedSource) _vm.SettingsOpen = false;
        }
    }

    void OnEpisodeCheckClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox { DataContext: EpisodeItem item }) return;
        bool target = !item.IsChecked;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastChecked != null && _lastChecked != item)
        {
            _vm.CheckRange(_lastChecked, item, target);
            e.Handled = true;
        }
        _lastChecked = item;
    }

    void OnEpisodeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } d && FindParent<CheckBox>(d) != null) return;
        if (EpisodeList.SelectedItem is EpisodeItem item) item.IsChecked = !item.IsChecked;
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    void OnPreviewKey(object sender, KeyEventArgs e)
    {
        if (_dialogOpen) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.F)
        {
            _vm.SettingsOpen = false;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.A && Keyboard.FocusedElement is not TextBox && !_vm.SettingsOpen)
        {
            _vm.ToggleSelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            _vm.SearchText = "";
            ArcList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm.SettingsOpen)
        {
            _vm.SettingsOpen = false;
            e.Handled = true;
        }
    }

    void OnLogChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();

    // ------------------------------------------------- window placement --

    void RestorePlacement(WindowPlacement? p)
    {
        if (_preview != null)
        {
            Width = _preview.Width;
            Height = _preview.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 20;
            Top = 20;
            return;
        }
        var work = SystemParameters.WorkArea;
        if (p == null)
        {
            Width = Math.Min(1320, work.Width - 40);
            Height = Math.Min(840, work.Height - 40);
            return;
        }
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                          SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Width = Math.Max(MinWidth, p.Width);
        Height = Math.Max(MinHeight, p.Height);
        if (vs.IntersectsWith(new Rect(p.X, p.Y, Math.Min(p.Width, 200), 40)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.X;
            Top = p.Y;
        }
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_preview == null && _vm.IsDownloading && !_closeConfirmed)
        {
            e.Cancel = true;
            if (!await ConfirmAsync("Quit while downloading?",
                    "A download is still running. Quitting stops it. The part already downloaded is kept and picks up where it left off next time.",
                    "Quit", "Keep downloading"))
                return;
            _closeConfirmed = true;
            _vm.CancelForExit();
            Close();
            return;
        }
        if (_preview != null) return;
        var r = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        _vm.Settings.Window = new WindowPlacement(r.X, r.Y, r.Width, r.Height, WindowState == WindowState.Maximized);
        _vm.TrySave();
    }

    public void ReportCrash(Exception e)
    {
        _vm.Log("Unexpected error: " + e);
        _vm.LogVisible = true;
        Toast("Something went wrong", e.Message, ToastKind.Danger);
    }

    // ---------------------------------------------------------- IUiHost --

    async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        // One at a time: a second dialog would stack behind the first.
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        try { return await _dialogs.ShowAsync(dialog, CancellationToken.None); }
        finally { _dialogOpen = false; }
    }

    static ContentDialog Dialog(string title, object content, string primary, string close) => new()
    {
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        CloseButtonText = close,
        DefaultButton = ContentDialogButton.Primary,
    };

    static TextBlock Message(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 440,
        Style = (Style)Application.Current.Resources["BodyText"],
    };

    public async Task<bool> ConfirmAsync(string title, string message, string primary, string close = "Cancel") =>
        await ShowDialogAsync(Dialog(title, Message(message), primary, close)) == ContentDialogResult.Primary;

    public async Task AlertAsync(string title, string message)
    {
        var d = Dialog(title, Message(message), "", "OK");
        d.DefaultButton = ContentDialogButton.Close;
        await ShowDialogAsync(d);
    }

    public async Task<BatchChoice?> AskBatchAsync(BatchRequest request)
    {
        var content = new BatchDialog(request);
        var d = Dialog($"Download {SourceRules.Plural(request.Episodes.Count, "episode")}", content, "Start download", "Cancel");
        d.PrimaryButtonIcon = new SymbolIcon { Symbol = SymbolRegular.ArrowDownload24 };
        return await ShowDialogAsync(d) == ContentDialogResult.Primary ? content.Choice : null;
    }

    public async Task ShowNoTorrentClientAsync()
    {
        var d = Dialog("No torrent app found",
            Message("The magnet link is copied to your clipboard.\n\nTo open magnets, install a torrent app. qBittorrent is free, open source and has no ads. After installing it, click the magnet button again."),
            "Get qBittorrent", "OK");
        if (await ShowDialogAsync(d) == ContentDialogResult.Primary) Shell.Open(Shell.QBittorrentUrl);
    }

    public async Task<bool> AskUsenetSetupAsync()
    {
        var d = Dialog("Add your Usenet indexer",
            Message("Usenet downloads need your own indexer URL and API key. Add them once in Settings and the NZB buttons start working.\n\n"
                    + "Don't have Usenet yet? You need a provider (Newshosting or Eweka, for example) plus an NZBGeek account."),
            "Open Settings", "Close");
        d.SecondaryButtonText = "Setup guide";
        var r = await ShowDialogAsync(d);
        if (r == ContentDialogResult.Secondary) Shell.Open(Usenet.SetupGuideUrl);
        return r == ContentDialogResult.Primary;
    }

    public async Task ShowDnsAsync()
    {
        var d = Dialog("DNS switcher", new DnsPanel(_vm.Settings, _vm.Log), "", "Close");
        d.DefaultButton = ContentDialogButton.Close;
        await ShowDialogAsync(d);
    }

    public string? PickFolder(string initial)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to save episodes", Multiselect = false };
        if (Directory.Exists(initial)) dlg.InitialDirectory = initial;
        return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
    }

    public void Toast(string title, string message, ToastKind kind = ToastKind.Info)
    {
        var (appearance, symbol) = kind switch
        {
            ToastKind.Success => (ControlAppearance.Success, SymbolRegular.CheckmarkCircle24),
            ToastKind.Caution => (ControlAppearance.Caution, SymbolRegular.Warning24),
            ToastKind.Danger => (ControlAppearance.Danger, SymbolRegular.DismissCircle24),
            _ => (ControlAppearance.Secondary, SymbolRegular.Info24),
        };
        _snackbar.Show(title, message, appearance, new SymbolIcon { Symbol = symbol, FontSize = 20 }, TimeSpan.FromSeconds(4));
    }
}
