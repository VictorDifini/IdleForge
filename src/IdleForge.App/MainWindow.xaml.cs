using IdleForge.App.Browser;
using IdleForge.App.Downloads;
using IdleForge.App.Updates;
using IdleForge.Core;
using IdleForge.Core.Persistence;
using IdleForge.Core.UserScripts;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.Json;
using System.Windows.Threading;

namespace IdleForge.App;

public partial class MainWindow : Window
{
    private readonly IdleForgeRepository _repository;
    private readonly DownloadManager _downloads;
    private readonly Dictionary<Guid, WebView2GameHost> _hosts = [];
    private IReadOnlyList<GameDefinition> _games = [];
    private IReadOnlyList<GameProfile> _profiles = [];
    private IReadOnlyList<UserScript> _scripts = [];
    private WebView2GameHost? _activeHost;
    private Guid? _activeProfileId;
    private bool _gridMode;
    private Guid? _focusedProfileId;
    private IReadOnlyList<GameProfile> _gridProfiles = [];
    private readonly HashSet<Guid> _configuredGridProfileIds = [];
    private bool _gridUsesAllAccounts = true;
    private readonly SemaphoreSlim _hostCreationGate = new(1, 1);
    private int _profileOpenVersion;
    private readonly Dictionary<Guid, string> _hostStatuses = [];
    private readonly string _layoutPath;
    private LayoutPreferences _layout = new();
    private bool _loadingLayout;
    private readonly DispatcherTimer _metricsTimer;
    private bool _metricsSampling;
    private readonly UpdateCoordinator _updates = new();
    private readonly SemaphoreSlim _layoutSaveGate = new(1, 1);
    private readonly DispatcherTimer _layoutSaveTimer;
    private bool _initialLoadCompleted;
    private readonly Dictionary<Guid, string> _lastSafeUris = [];
    private sealed record LayoutPreferences(int Columns = 0, bool KeepOthersVisible = true, bool ExpandOnPanelClick = false, Guid[]? SelectedProfiles = null, bool SidebarCollapsed = false, double SidebarWidth = 286, bool ShowMetrics = false, string PerformanceMode = "Balanced", Guid[]? OpenProfiles = null, Guid? ActiveProfile = null, bool GridMode = false, Guid? FocusedProfile = null, double WindowLeft = double.NaN, double WindowTop = double.NaN, double WindowWidth = 1400, double WindowHeight = 900, string WindowState = "Normal", Dictionary<Guid,double>? Zoom = null, Dictionary<string,bool>? SidebarGroups = null, Dictionary<Guid,string>? LastSafeUris = null);

    public MainWindow()
    {
        InitializeComponent();
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdleForge");
        _layoutPath = Path.Combine(data, "layout.json");
        _repository = new IdleForgeRepository(Path.Combine(data, "idleforge.db"));
        _downloads = new DownloadManager(data);
        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _layoutSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) }; _layoutSaveTimer.Tick += async (_, _) => { _layoutSaveTimer.Stop(); await SaveLayoutAsync(); };
        _metricsTimer.Tick += async (_, _) => await UpdateMetricsAsync();
        StateChanged += (_, _) => { ApplyPerformanceProfile(); ScheduleLayoutSave(); }; LocationChanged += (_, _) => ScheduleLayoutSave(); SizeChanged += (_, _) => ScheduleLayoutSave(); Closing += async (_, _) => await SaveLayoutAsync();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var firstLoad = !_initialLoadCompleted;
        var selectedProfileId = _activeProfileId;
        await _repository.InitializeAsync(); _games = await _repository.GetGamesAsync(); _profiles = await _repository.GetProfilesAsync(); _scripts = await _repository.GetScriptsAsync();
        _loadingLayout = true; ProfilesList.ItemsSource = _profiles; EngineStatus.Text = $"WebView2 · {_profiles.Count} perfil(is)";
        if (File.Exists(_layoutPath)) try { _layout = JsonSerializer.Deserialize<LayoutPreferences>(await File.ReadAllTextAsync(_layoutPath)) ?? new(); } catch { _layout = new(); }
        _lastSafeUris.Clear();
        foreach (var pair in _layout.LastSafeUris ?? []) _lastSafeUris[pair.Key] = pair.Value;
        KeepOthersVisibleCheck.IsChecked = _layout.KeepOthersVisible; ExpandOnClickCheck.IsChecked = _layout.ExpandOnPanelClick;
        ShowMetricsCheck.IsChecked = _layout.ShowMetrics; ApplySidebarState(_layout.SidebarCollapsed, _layout.SidebarWidth); ApplySidebarGroups();
        PerformanceModeBox.SelectedIndex = _layout.PerformanceMode switch { "Maximum" => 0, "Economy" => 2, _ => 1 };
        if (_layout.Columns is >= 2 and <= 4) ColumnsBox.SelectedIndex = _layout.Columns - 1;
        _configuredGridProfileIds.Clear();
        _gridUsesAllAccounts = _layout.SelectedProfiles is not { Length: > 0 };
        foreach (var profileId in _gridUsesAllAccounts ? _profiles.Select(x => x.Id) : _layout.SelectedProfiles!) _configuredGridProfileIds.Add(profileId);
        ProfilesList.SelectedItem = selectedProfileId is Guid activeId ? _profiles.FirstOrDefault(x => x.Id == activeId) : null;
        _loadingLayout = false;
        if (_layout.ShowMetrics) { _metricsTimer.Start(); await UpdateMetricsAsync(); }
        if (firstLoad)
        {
            ConfirmUpdateHealth();
            var settings = _updates.LoadSettings(); if (settings.RestoreLayout) ApplyWindowPlacement();
            if (settings.RestoreLastSession && settings.RestoreProfiles && await CanRestoreSafelyAsync())
            {
                // WebView2 initialization is unreliable while the first WPF layout pass is still running.
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(250);
                await RestoreSessionAsync(settings);
            }
            _initialLoadCompleted = true; CompleteRestoreGuard();
            await NotifyAvailableUpdateAsync(settings);
        }
    }

    private async Task NotifyAvailableUpdateAsync(IdleForgeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.UpdateFolder)) return;
        try
        {
            var found = await _updates.FindNewestAsync(settings.UpdateFolder);
            if (found is not null && Version.Parse(found.Value.Manifest.Version) > Version.Parse(_updates.GetCurrentVersion()))
                FooterStatus.Text = $"Nova versão {found.Value.Manifest.Version} disponível · abra Configurações para atualizar";
        }
        catch { /* Uma pasta indisponível não deve impedir a inicialização. */ }
    }

    private async void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddGameWindow { Owner = this }; if (dialog.ShowDialog() != true) return;
        var game = new GameDefinition(Guid.NewGuid(), dialog.GameName, dialog.GameUri); var profile = new GameProfile(Guid.NewGuid(), game.Id, dialog.ProfileName, $"profile-{Guid.NewGuid():N}");
        await _repository.SaveGameAsync(game); await _repository.SaveProfileAsync(profile); await LoadAsync();
        if (_gridUsesAllAccounts) _configuredGridProfileIds.Add(profile.Id);
        ProfilesList.SelectedItem = _profiles.First(x => x.Id == profile.Id);
    }

    private async void ProfilesList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var item = source is null ? null : ItemsControl.ContainerFromElement(ProfilesList, source) as ListBoxItem;
        if (_loadingLayout || item?.DataContext is not GameProfile profile) return;
        e.Handled = true; ProfilesList.SelectedItem = profile;
        var requestVersion = Interlocked.Increment(ref _profileOpenVersion);
        var host = await EnsureHostAsync(profile);
        if (host is null || requestVersion != _profileOpenVersion) return;
        var game = _games.First(x => x.Id == profile.GameId);
        if (_gridMode && _gridProfiles.Count > 0) FocusProfile(profile.Id); else ShowSingle(profile, host, false);
        AddressBox.Text = game.StartUri.AbsoluteUri;
    }

    private async Task<WebView2GameHost?> EnsureHostAsync(GameProfile profile)
    {
        if (_hosts.TryGetValue(profile.Id, out var existing)) return existing;
        await _hostCreationGate.WaitAsync();
        try
        {
            if (_hosts.TryGetValue(profile.Id, out existing)) return existing;
            var game = _games.First(x => x.Id == profile.GameId);
            var host = new WebView2GameHost(_repository, downloads: _downloads); _hosts[profile.Id] = host; _hostStatuses[profile.Id] = "Conectando";
            host.StatusChanged += (_, status) => Dispatcher.Invoke(() => { _hostStatuses[profile.Id] = status; FooterStatus.Text = $"{profile.Name} · {status}"; });
            host.NavigationStateChanged += (_, state) => Dispatcher.Invoke(() =>
            {
                if (TryGetSafeRestoreUri(game.StartUri, state.CurrentUri, out var safeUri)) { _lastSafeUris[profile.Id] = safeUri; ScheduleLayoutSave(); }
                if (_activeProfileId != profile.Id) return;
                AddressBox.Text = state.CurrentUri?.AbsoluteUri ?? ""; BackButton.IsEnabled = state.CanGoBack; ForwardButton.IsEnabled = state.CanGoForward; ReturnToGameButton.IsEnabled = !state.IsAtGame;
            });
            try
            {
                // The WPF WebView2 controller must be connected to a rendered visual tree.
                // Starting an unattached control can wait indefinitely during session restore.
                Detach(host); BrowserArea.Children.Clear(); BrowserArea.Children.Add(host);
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                await host.StartAsync(game, profile, await _repository.GetScriptsForProfileAsync(profile.Id));
            }
            catch (Exception ex) { Detach(host); _hosts.Remove(profile.Id); MessageBox.Show(this, ex.Message, "Falha ao iniciar o navegador", MessageBoxButton.OK, MessageBoxImage.Error); return null; }
            return host;
        }
        finally { _hostCreationGate.Release(); }
    }

    private void ShowSingle(GameProfile profile, WebView2GameHost host, bool focused)
    {
        Detach(host); BrowserArea.Children.Clear(); BrowserArea.Children.Add(host); _activeHost = host;
        _activeProfileId = profile.Id;
        host.SetPresentationState(focused ? WebView2GameHost.PresentationState.Focused : WebView2GameHost.PresentationState.VisibleSingle);
        _focusedProfileId = focused ? profile.Id : null;
        ApplyPerformanceProfile();
        if (_initialLoadCompleted) ScheduleLayoutSave();
    }

    private async void ShowAllAccounts_Click(object sender, RoutedEventArgs e)
    {
        var chosen = (_gridUsesAllAccounts ? _profiles : _profiles.Where(x => _configuredGridProfileIds.Contains(x.Id))).ToArray();
        if (chosen.Length == 0) chosen = _profiles.ToArray();
        if (chosen.Length == 0) { MessageBox.Show(this, "Adicione pelo menos uma conta."); return; }
        foreach (var profile in chosen) if (await EnsureHostAsync(profile) is null) return;
        _gridProfiles = chosen; _gridMode = true; _focusedProfileId = null;
        _activeProfileId = chosen[0].Id; _activeHost = _hosts[chosen[0].Id];
        RenderGrid(); await SaveLayoutAsync();
    }

    private async void ConfigureGrid_Click(object sender, RoutedEventArgs e)
    {
        var selected = _gridUsesAllAccounts ? _profiles.Select(x => x.Id) : _configuredGridProfileIds;
        var dialog = new GridConfigurationWindow(_profiles, selected) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _configuredGridProfileIds.Clear();
        foreach (var id in dialog.SelectedProfileIds) _configuredGridProfileIds.Add(id);
        _gridUsesAllAccounts = dialog.AllAccountsSelected;
        await SaveLayoutAsync();
        FooterStatus.Text = _gridUsesAllAccounts ? "Grade configurada para todas as contas" : $"Grade configurada para {_configuredGridProfileIds.Count} conta(s)";
    }

    private void RenderGrid()
    {
        if (_gridProfiles.Count == 0) return; BrowserArea.Children.Clear();
        var columns = SelectedColumns(); var rows = (int)Math.Ceiling(_gridProfiles.Count / (double)columns); var grid = CreateResizableGrid(rows, columns);
        for (var i = 0; i < _gridProfiles.Count; i++)
        {
            var profile = _gridProfiles[i]; var host = _hosts[profile.Id]; Detach(host); host.SetPresentationState(WebView2GameHost.PresentationState.VisibleGrid);
            var card = CreateCard(profile, host, compact: false); Grid.SetRow(card, (i / columns) * 2); Grid.SetColumn(card, (i % columns) * 2); grid.Children.Add(card);
        }
        BrowserArea.Children.Add(grid); FooterStatus.Text = $"Grade ativa · {_gridProfiles.Count} contas reais e interativas";
        ApplyPerformanceProfile();
    }

    private Grid CreateResizableGrid(int rows, int columns)
    {
        var grid = new Grid();
        for (var c = 0; c < columns; c++) { grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 }); if (c < columns - 1) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) }); }
        for (var r = 0; r < rows; r++) { grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 }); if (r < rows - 1) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) }); }
        for (var c = 1; c < grid.ColumnDefinitions.Count; c += 2) { var splitter = new GridSplitter { Width = 5, Background = new SolidColorBrush(Color.FromRgb(38, 54, 78)), HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(splitter, c); Grid.SetRowSpan(splitter, grid.RowDefinitions.Count); grid.Children.Add(splitter); }
        for (var r = 1; r < grid.RowDefinitions.Count; r += 2) { var splitter = new GridSplitter { Height = 5, Background = new SolidColorBrush(Color.FromRgb(38, 54, 78)), VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows }; Grid.SetRow(splitter, r); Grid.SetColumnSpan(splitter, grid.ColumnDefinitions.Count); grid.Children.Add(splitter); }
        return grid;
    }

    private Border CreateCard(GameProfile profile, WebView2GameHost host, bool compact)
    {
        var status = new TextBlock { Text = $"{PresentationLabel(host.CurrentPresentationState)} · {_hostStatuses.GetValueOrDefault(profile.Id, "Ativo")} · {host.EnabledScriptCount} script(s)", Foreground = new SolidColorBrush(Color.FromRgb(143, 161, 184)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        var expand = new Button { Content = "Ampliar", Padding = new Thickness(8, 3, 8, 3), FontSize = 11, Tag = profile.Id }; expand.Click += (_, _) => FocusProfile(profile.Id);
        var header = new DockPanel { Height = compact ? 25 : 32, Background = new SolidColorBrush(Color.FromRgb(16, 24, 39)) };
        DockPanel.SetDock(expand, Dock.Right); header.Children.Add(expand); header.Children.Add(new TextBlock { Text = profile.Name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }); header.Children.Add(status);
        var body = new Grid(); body.Children.Add(host); var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); Grid.SetRow(header, 0); Grid.SetRow(body, 1); layout.Children.Add(header); layout.Children.Add(body);
        var border = new Border { Child = layout, BorderBrush = new SolidColorBrush(Color.FromRgb(38, 54, 78)), BorderThickness = new Thickness(1), Margin = new Thickness(2), Background = Brushes.Black };
        border.PreviewMouseLeftButtonDown += (_, args) => { if (ExpandOnClickCheck.IsChecked == true && !header.IsMouseOver) { FocusProfile(profile.Id); args.Handled = true; } };
        return border;
    }

    private void FocusProfile(Guid profileId)
    {
        if (!_hosts.TryGetValue(profileId, out var host)) return; var profile = _profiles.First(x => x.Id == profileId); _gridMode = true; _focusedProfileId = profileId; _activeHost = host;
        _activeProfileId = profileId; ProfilesList.SelectedItem = profile; host.SetPresentationState(WebView2GameHost.PresentationState.Focused);
        foreach (var pair in _hosts.Where(x => _gridProfiles.Any(p => p.Id == x.Key))) { Detach(pair.Value); pair.Value.SetPresentationState(pair.Key == profileId ? WebView2GameHost.PresentationState.Focused : WebView2GameHost.PresentationState.HiddenConnected); }
        BrowserArea.Children.Clear();
        if (KeepOthersVisibleCheck.IsChecked != true || _gridProfiles.Count == 1) { BrowserArea.Children.Add(host); ApplyPerformanceProfile(); ScheduleLayoutSave(); return; }
        var focusGrid = new Grid(); focusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) }); focusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) }); focusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
        var splitter = new GridSplitter { Width = 5, Background = new SolidColorBrush(Color.FromRgb(38, 54, 78)), HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetColumn(splitter, 1); focusGrid.Children.Add(splitter);
        var main = CreateCard(profile, host, false); Grid.SetColumn(main, 0); focusGrid.Children.Add(main);
        var side = new Grid(); var others = _gridProfiles.Where(x => x.Id != profileId).ToArray(); for (var i = 0; i < others.Length; i++) { side.RowDefinitions.Add(new RowDefinition()); var otherHost = _hosts[others[i].Id]; otherHost.SetPresentationState(WebView2GameHost.PresentationState.ActiveThumbnail); var card = CreateCard(others[i], otherHost, true); Grid.SetRow(card, i); side.Children.Add(card); } Grid.SetColumn(side, 2); focusGrid.Children.Add(side); BrowserArea.Children.Add(focusGrid); ApplyPerformanceProfile(); ScheduleLayoutSave();
    }

    private void BackToGrid_Click(object sender, RoutedEventArgs e) { _focusedProfileId = null; RenderGrid(); ScheduleLayoutSave(); }
    private void LayoutOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLayout || string.IsNullOrEmpty(_layoutPath)) return;
        if (_gridMode)
        {
            if (_focusedProfileId is Guid focusedProfileId) FocusProfile(focusedProfileId);
            else RenderGrid();
        }
        _ = SaveLayoutAsync();
    }
    private static string PresentationLabel(WebView2GameHost.PresentationState state) => state switch { WebView2GameHost.PresentationState.VisibleGrid => "Visível na grade", WebView2GameHost.PresentationState.ActiveThumbnail => "Miniatura ativa", WebView2GameHost.PresentationState.Focused => "Ampliada", WebView2GameHost.PresentationState.HiddenConnected => "Oculta · timers podem ser limitados", WebView2GameHost.PresentationState.Suspended => "Suspensa", _ => "Visível" };
    private int SelectedColumns()
    {
        if (ColumnsBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var chosen)) return chosen;
        var count = Math.Max(1, _gridProfiles.Count); var aspect = Math.Max(.5, BrowserArea.ActualWidth / Math.Max(1, BrowserArea.ActualHeight)); return Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count * aspect)));
    }
    private async Task SaveLayoutAsync()
    {
        if (!IsLoaded && !_initialLoadCompleted) return; await _layoutSaveGate.WaitAsync();
        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds; var configuredProfiles = _gridUsesAllAccounts ? null : _configuredGridProfileIds.ToArray();
            var restoring = !_initialLoadCompleted;
            var openProfiles = restoring ? _layout.OpenProfiles : _hosts.Keys.ToArray();
            var activeProfile = restoring ? _layout.ActiveProfile : _activeProfileId;
            _layout = new(Columns: ColumnsBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var c) ? c : 0, KeepOthersVisible: KeepOthersVisibleCheck.IsChecked == true, ExpandOnPanelClick: ExpandOnClickCheck.IsChecked == true, SelectedProfiles: configuredProfiles, SidebarCollapsed: CompactSidebar.Visibility == Visibility.Visible, SidebarWidth: CompactSidebar.Visibility == Visibility.Visible ? Math.Max(220, _layout.SidebarWidth) : SidebarColumn.Width.Value, ShowMetrics: ShowMetricsCheck.IsChecked == true, PerformanceMode: SelectedPerformanceMode(), OpenProfiles: openProfiles, ActiveProfile: activeProfile, GridMode: restoring ? _layout.GridMode : _gridMode, FocusedProfile: restoring ? _layout.FocusedProfile : _focusedProfileId, WindowLeft: bounds.Left, WindowTop: bounds.Top, WindowWidth: bounds.Width, WindowHeight: bounds.Height, WindowState: WindowState.ToString(), Zoom: restoring ? _layout.Zoom : _hosts.ToDictionary(x => x.Key, x => x.Value.ZoomFactor), SidebarGroups: CaptureSidebarGroups(), LastSafeUris: new(_lastSafeUris));
            await UpdateCoordinator.AtomicWriteAsync(_layoutPath, JsonSerializer.Serialize(_layout));
        }
        finally { _layoutSaveGate.Release(); }
    }
    private void ScheduleLayoutSave() { if (!_initialLoadCompleted) return; _layoutSaveTimer.Stop(); _layoutSaveTimer.Start(); }
    private void ApplyWindowPlacement()
    {
        if (double.IsFinite(_layout.WindowLeft) && double.IsFinite(_layout.WindowTop) && _layout.WindowWidth >= MinWidth && _layout.WindowHeight >= MinHeight)
        {
            var visible = _layout.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && _layout.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight && _layout.WindowLeft + _layout.WindowWidth > SystemParameters.VirtualScreenLeft && _layout.WindowTop + _layout.WindowHeight > SystemParameters.VirtualScreenTop;
            if (visible) { WindowStartupLocation = WindowStartupLocation.Manual; Left = _layout.WindowLeft; Top = _layout.WindowTop; Width = _layout.WindowWidth; Height = _layout.WindowHeight; }
        }
        if (Enum.TryParse<WindowState>(_layout.WindowState, out var state) && state != WindowState.Minimized) WindowState = state;
    }
    private async Task RestoreSessionAsync(IdleForgeSettings settings)
    {
        var wanted = (_layout.OpenProfiles ?? []).Distinct().ToArray(); if (wanted.Length == 0) return;
        var failures = new List<string>();
        foreach (var profile in _profiles.Where(x => wanted.Contains(x.Id)))
        {
            try
            {
                var host = await EnsureHostAsync(profile);
                if (host is not null && _layout.Zoom?.TryGetValue(profile.Id, out var zoom) == true) host.ZoomFactor = zoom;
                if (host is not null && _lastSafeUris.TryGetValue(profile.Id, out var saved) && Uri.TryCreate(saved, UriKind.Absolute, out var savedUri)) await host.NavigateAsync(savedUri);
            }
            catch (Exception ex) { _hostStatuses[profile.Id] = "Restauração falhou: " + ex.Message; failures.Add($"{profile.Name}: {ex.Message}"); }
        }
        if (failures.Count > 0) FooterStatus.Text = $"Restauração parcial · {string.Join(" | ", failures)}";
        var open = _profiles.Where(x => _hosts.ContainsKey(x.Id) && wanted.Contains(x.Id)).ToArray(); if (open.Length == 0) return;
        if (settings.RestoreLayout && _layout.GridMode)
        {
            _gridProfiles = open.Where(x => _gridUsesAllAccounts || _configuredGridProfileIds.Contains(x.Id)).ToArray(); if (_gridProfiles.Count == 0) _gridProfiles = open; _gridMode = true; RenderGrid();
            if (_layout.FocusedProfile is Guid focused && _hosts.ContainsKey(focused)) FocusProfile(focused);
            else
            {
                var active = open.FirstOrDefault(x => x.Id == _layout.ActiveProfile) ?? open[0];
                _activeProfileId = active.Id; _activeHost = _hosts[active.Id]; ProfilesList.SelectedItem = active;
            }
        }
        else
        {
            var active = open.FirstOrDefault(x => x.Id == _layout.ActiveProfile) ?? open[0]; ShowSingle(active, _hosts[active.Id], false); ProfilesList.SelectedItem = active;
        }
    }
    private static bool TryGetSafeRestoreUri(Uri gameUri, Uri? currentUri, out string safeUri)
    {
        safeUri = string.Empty;
        if (currentUri is null || !string.Equals(gameUri.Scheme, currentUri.Scheme, StringComparison.OrdinalIgnoreCase) || !string.Equals(gameUri.Host, currentUri.Host, StringComparison.OrdinalIgnoreCase) || gameUri.Port != currentUri.Port) return false;
        var sanitized = new UriBuilder(currentUri) { Query = string.Empty, Fragment = string.Empty };
        safeUri = sanitized.Uri.AbsoluteUri;
        return true;
    }
    private string RestoreGuardPath => Path.Combine(Path.GetDirectoryName(_layoutPath)!, "restore-guard.json");
    private async Task<bool> CanRestoreSafelyAsync()
    {
        var attempts = 0; try { using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(RestoreGuardPath)); attempts = doc.RootElement.GetProperty("attempts").GetInt32(); } catch { }
        if (attempts >= 2) { MessageBox.Show(this, "A restauração automática foi ignorada após falhas repetidas. As contas podem ser abertas manualmente; reative a restauração em Configurações.", "Inicialização segura", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        await UpdateCoordinator.AtomicWriteAsync(RestoreGuardPath, JsonSerializer.Serialize(new { attempts = attempts + 1, startedAt = DateTimeOffset.UtcNow })); return true;
    }
    private void CompleteRestoreGuard() { try { if (File.Exists(RestoreGuardPath)) File.Delete(RestoreGuardPath); } catch { } }
    private static void ConfirmUpdateHealth()
    {
        var args = Environment.GetCommandLineArgs(); var fileIndex = Array.IndexOf(args, "--update-health-file"); var tokenIndex = Array.IndexOf(args, "--update-health-token");
        if (fileIndex >= 0 && fileIndex + 1 < args.Length && tokenIndex >= 0 && tokenIndex + 1 < args.Length) try { Directory.CreateDirectory(Path.GetDirectoryName(args[fileIndex + 1])!); File.WriteAllText(args[fileIndex + 1], args[tokenIndex + 1]); } catch { }
    }
    private static void Detach(FrameworkElement element) { if (element.Parent is Panel panel) panel.Children.Remove(element); else if (element.Parent is Decorator decorator) decorator.Child = null; else if (element.Parent is ContentControl content) content.Content = null; }

    private async void UpdateScript_Click(object sender, RoutedEventArgs e)
    {
        var choice = MessageBox.Show(this, "Escolha a origem da atualização:\n\nSim — arquivo .user.js\nNão — URL\nCancelar — não atualizar", "Atualizar script", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        string source; Uri origin; UserScriptMetadata metadata;
        try
        {
            if (choice == MessageBoxResult.Yes)
            {
                var picker = new OpenFileDialog { Filter = "Userscripts (*.user.js)|*.user.js|JavaScript (*.js)|*.js" }; if (picker.ShowDialog(this) != true) return;
                source = await File.ReadAllTextAsync(picker.FileName); origin = new Uri(picker.FileName); metadata = new UserScriptParser().Parse(source);
            }
            else
            {
                var input = new InstallUrlWindow { Owner = this }; if (input.ShowDialog() != true) return; var acquired = await new ScriptAcquisitionService().AcquireAsync(input.ScriptUri); source = acquired.Source; origin = acquired.SourceUri; metadata = acquired.Metadata;
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Atualização inválida", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var existing = _scripts.FirstOrDefault(x => x.Metadata.Name == metadata.Name && x.Metadata.Namespace == metadata.Namespace);
        if (existing is null) { MessageBox.Show(this, "O arquivo/URL não corresponde a um script instalado. Use Instalar para um script novo.", "Atualizar script", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (existing.Metadata.Version == metadata.Version && MessageBox.Show(this, $"A versão informada continua sendo {metadata.Version}. Aplicar mesmo assim?", "Mesma versão", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var preview = new ScriptPreviewWindow(metadata, new ScriptRiskAnalyzer().Analyze(metadata), source) { Owner = this }; if (preview.ShowDialog() != true) return;
        var updated = existing with { Metadata = metadata, Source = source, InstallSource = origin, LocalSourcePath = origin.IsFile ? origin.LocalPath : null, ContentSha256 = LocalScriptUpdateService.ComputeHash(source), UpdatedAt = DateTimeOffset.UtcNow, BroadConnectApproved = preview.BroadConnectApproved, MainWorldCompatibilityApproved = preview.MainWorldCompatibilityApproved };
        await _repository.SaveScriptAsync(updated); _scripts = await _repository.GetScriptsAsync();
        await SelectProfilesAndApplyAsync($"{metadata.Name} atualizado para {metadata.Version}. Selecione as contas que devem recarregar agora.");
    }

    private async void RestoreScript_Click(object sender, RoutedEventArgs e)
    {
        var rows = new List<ScriptVersionWindow.Row>(); foreach (var script in _scripts) foreach (var version in await _repository.GetScriptVersionsAsync(script.Id)) rows.Add(new(script, version));
        if (rows.Count == 0) { MessageBox.Show(this, "Ainda não existem versões arquivadas.", "Histórico de scripts"); return; }
        var dialog = new ScriptVersionWindow(rows) { Owner = this }; if (dialog.ShowDialog() != true || dialog.Selected is not { } selected) return;
        var restored = selected.Script with { Metadata = selected.Version.Metadata, Source = selected.Version.Source, ContentSha256 = selected.Version.ContentSha256 ?? LocalScriptUpdateService.ComputeHash(selected.Version.Source), LocalSourcePath = selected.Version.LocalSourcePath ?? selected.Script.LocalSourcePath, UpdatedAt = DateTimeOffset.UtcNow };
        await _repository.SaveScriptAsync(restored); _scripts = await _repository.GetScriptsAsync();
        await SelectProfilesAndApplyAsync($"Versão {selected.Version.Version} restaurada. Selecione as contas que devem recarregar agora.");
    }

    private async Task SelectProfilesAndApplyAsync(string message)
    {
        var openProfiles = _profiles.Where(x => _hosts.ContainsKey(x.Id)).ToArray();
        if (openProfiles.Length == 0) { MessageBox.Show(this, message + "\n\nNenhuma conta está aberta; a atualização será usada na próxima abertura."); return; }
        MessageBox.Show(this, message, "Userscript salvo", MessageBoxButton.OK, MessageBoxImage.Information);
        var picker = new ProfileReloadWindow(openProfiles, _activeProfileId) { Owner = this }; if (picker.ShowDialog() != true) return;
        foreach (var profile in picker.SelectedProfiles) await ReloadProfileScriptsAsync(profile);
        FooterStatus.Text = $"Scripts aplicados em {picker.SelectedProfiles.Count} conta(s); demais contas não foram interrompidas.";
    }

    private async void InstallScript_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Userscripts (*.user.js)|*.user.js|JavaScript (*.js)|*.js" }; if (picker.ShowDialog(this) != true) return;
        var source = await File.ReadAllTextAsync(picker.FileName); UserScriptMetadata metadata;
        try { metadata = new UserScriptParser().Parse(source); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Userscript inválido", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var preview = new ScriptPreviewWindow(metadata, new ScriptRiskAnalyzer().Analyze(metadata), source) { Owner = this }; if (preview.ShowDialog() != true) return;
        var existing = _scripts.FirstOrDefault(x => x.Metadata.Name == metadata.Name && x.Metadata.Namespace == metadata.Namespace);
        var script = existing is null
            ? new UserScript(Guid.NewGuid(), metadata, source, true, new Uri(picker.FileName), DateTimeOffset.UtcNow, BroadConnectApproved: preview.BroadConnectApproved, MainWorldCompatibilityApproved: preview.MainWorldCompatibilityApproved, LocalSourcePath: Path.GetFullPath(picker.FileName), ContentSha256: LocalScriptUpdateService.ComputeHash(source))
            : existing with { Metadata = metadata, Source = source, Enabled = true, InstallSource = new Uri(picker.FileName), LocalSourcePath = Path.GetFullPath(picker.FileName), ContentSha256 = LocalScriptUpdateService.ComputeHash(source), UpdatedAt = DateTimeOffset.UtcNow, BroadConnectApproved = preview.BroadConnectApproved, MainWorldCompatibilityApproved = preview.MainWorldCompatibilityApproved };
        await _repository.SaveScriptAsync(script); await LoadAsync();
        MessageBox.Show(this, existing is null ? "Script instalado. Reinicie sessões abertas para aplicá-lo." : "Script atualizado. Reinicie sessões abertas para aplicar a autorização.", "IdleForge", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void InstallScriptUrl_Click(object sender, RoutedEventArgs e)
    {
        var input = new InstallUrlWindow { Owner = this }; if (input.ShowDialog() != true) return;
        try
        {
            var acquired = await new ScriptAcquisitionService().AcquireAsync(input.ScriptUri);
            var preview = new ScriptPreviewWindow(acquired.Metadata, new ScriptRiskAnalyzer().Analyze(acquired.Metadata), acquired.Source) { Owner = this }; if (preview.ShowDialog() != true) return;
            var existing = _scripts.FirstOrDefault(x => x.Metadata.Name == acquired.Metadata.Name && x.Metadata.Namespace == acquired.Metadata.Namespace);
            var script = existing is null
                ? new UserScript(Guid.NewGuid(), acquired.Metadata, acquired.Source, true, acquired.SourceUri, DateTimeOffset.UtcNow, BroadConnectApproved: preview.BroadConnectApproved, MainWorldCompatibilityApproved: preview.MainWorldCompatibilityApproved, ContentSha256: LocalScriptUpdateService.ComputeHash(acquired.Source))
                : existing with { Metadata = acquired.Metadata, Source = acquired.Source, InstallSource = acquired.SourceUri, LocalSourcePath = null, ContentSha256 = LocalScriptUpdateService.ComputeHash(acquired.Source), UpdatedAt = DateTimeOffset.UtcNow, BroadConnectApproved = preview.BroadConnectApproved, MainWorldCompatibilityApproved = preview.MainWorldCompatibilityApproved };
            await _repository.SaveScriptAsync(script); await LoadAsync();
            MessageBox.Show(this, existing is null ? "Script instalado." : "Script atualizado; a versão anterior foi preservada.", "IdleForge");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Instalação bloqueada", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void ShowScripts_Click(object sender, RoutedEventArgs e)
    {
        if (_activeHost is null || _activeProfileId is not Guid profileId) { MessageBox.Show(this, "Abra ou selecione uma conta antes de gerenciar painéis."); return; }
        var profile = _profiles.First(x => x.Id == profileId); var scripts = await _repository.GetScriptsForProfileAsync(profileId);
        var openProfiles = _profiles.Where(x => _hosts.ContainsKey(x.Id)).ToArray();
        new ScriptManagerWindow(scripts, profile, _activeHost, _repository, async () => { await ReloadProfileScriptsAsync(profile); _scripts = await _repository.GetScriptsAsync(); }, openProfiles, async selected => { foreach (var item in selected) await ReloadProfileScriptsAsync(item); }) { Owner = this }.ShowDialog();
        _scripts = await _repository.GetScriptsAsync();
    }

    private async Task ReloadProfileScriptsAsync(GameProfile profile)
    {
        if (_hosts.TryGetValue(profile.Id, out var host)) await host.ApplyScriptsAndReloadAsync(await _repository.GetScriptsForProfileAsync(profile.Id));
    }

    private async void ReloadAccounts_Click(object sender, RoutedEventArgs e)
    {
        var open = _profiles.Where(x => _hosts.ContainsKey(x.Id)).ToArray();
        if (open.Length == 0) { MessageBox.Show(this, "Nenhuma conta está aberta."); return; }
        var picker = new ProfileReloadWindow(open, _activeProfileId, "Recarregar contas", "As páginas e userscripts das contas selecionadas serão reiniciados. Atividades temporárias nessas contas podem ser interrompidas; as demais continuarão funcionando.") { Owner = this };
        if (picker.ShowDialog() != true) return;
        foreach (var profile in picker.SelectedProfiles) await ReloadProfileScriptsAsync(profile);
        FooterStatus.Text = $"{picker.SelectedProfiles.Count} conta(s) recarregada(s); contas não selecionadas permaneceram intactas.";
    }
    private void ShowDownloads_Click(object sender, RoutedEventArgs e) => new DownloadsWindow(_downloads) { Owner = this }.Show();
    private void OpenExternal_Click(object sender, RoutedEventArgs e) => _activeHost?.OpenCurrentPageExternally();
    private void Back_Click(object sender, RoutedEventArgs e) => _activeHost?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _activeHost?.GoForward();
    private async void ReturnToGame_Click(object sender, RoutedEventArgs e) { if (_activeHost is not null) await _activeHost.ReturnToGameAsync(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { if (_activeHost is null) return; _activeHost.ZoomFactor -= .1; ScheduleLayoutSave(); }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { if (_activeHost is null) return; _activeHost.ZoomFactor += .1; ScheduleLayoutSave(); }
    private async Task PrepareForUpdateAsync()
    {
        await SaveLayoutAsync();
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdleForge");
        await _repository.CreateBackupAsync(Path.Combine(data, "DatabaseBackups", $"pre-update-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db"));
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(_updates, PrepareForUpdateAsync) { Owner = this }.ShowDialog();
    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProfileId is not Guid profileId) return; var profile = _profiles.FirstOrDefault(x => x.Id == profileId); if (profile is not null) await ReloadProfileScriptsAsync(profile);
    }
    private async void Suspend_Click(object sender, RoutedEventArgs e) { if (_activeHost is not null) await _activeHost.SuspendAsync(); }
    private async void Resume_Click(object sender, RoutedEventArgs e) { if (_activeHost is not null) await _activeHost.ResumeAsync(); }
    private async void AddressBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && _activeHost is not null && Uri.TryCreate(AddressBox.Text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") await _activeHost.NavigateAsync(uri); }
    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        ApplySidebarState(CompactSidebar.Visibility != Visibility.Visible, _layout.SidebarWidth); _ = SaveLayoutAsync();
    }
    private void ApplySidebarState(bool collapsed, double width = 286)
    {
        SidebarColumn.Width = new GridLength(collapsed ? 54 : Math.Clamp(width, 220, 390)); FullSidebar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible; CompactSidebar.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        SidebarToggleButton.ToolTip = collapsed ? "Expandir lateral" : "Usar lateral compacta";
    }
    private Dictionary<string, bool> CaptureSidebarGroups() => new() { [nameof(AccountsGroup)] = AccountsGroup.IsExpanded, [nameof(ViewGroup)] = ViewGroup.IsExpanded, [nameof(ScriptsGroup)] = ScriptsGroup.IsExpanded, [nameof(PerformanceGroup)] = PerformanceGroup.IsExpanded, [nameof(SystemGroup)] = SystemGroup.IsExpanded };
    private void ApplySidebarGroups() { var values = _layout.SidebarGroups; if (values is null) return; foreach (var item in new[] { AccountsGroup, ViewGroup, ScriptsGroup, PerformanceGroup, SystemGroup }) if (values.TryGetValue(item.Name, out var expanded)) item.IsExpanded = expanded; }
    private void SidebarGroup_Changed(object sender, RoutedEventArgs e) { if (!_loadingLayout) ScheduleLayoutSave(); }
    private void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) { if (CompactSidebar.Visibility == Visibility.Visible && SidebarColumn.Width.Value > 80) { CompactSidebar.Visibility = Visibility.Collapsed; FullSidebar.Visibility = Visibility.Visible; } ScheduleLayoutSave(); }
    private void MetricsOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLayout || string.IsNullOrEmpty(_layoutPath)) return;
        if (ShowMetricsCheck.IsChecked == true) { _metricsTimer.Start(); _ = UpdateMetricsAsync(); }
        else { _metricsTimer.Stop(); MetricsStatus.Text = string.Empty; }
        _ = SaveLayoutAsync();
    }
    private string SelectedPerformanceMode() => PerformanceModeBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "Balanced";
    private void PerformanceMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLayout || !IsLoaded) return; ApplyPerformanceProfile(); _ = SaveLayoutAsync();
    }
    private void ApplyPerformanceProfile()
    {
        if (_hosts.Count == 0 || PerformanceModeBox is null) return;
        var mode = SelectedPerformanceMode(); var minimized = WindowState == WindowState.Minimized;
        foreach (var host in _hosts.Values)
        {
            var low = minimized || (mode switch
            {
                "Maximum" => false,
                "Economy" => host.CurrentPresentationState is not (WebView2GameHost.PresentationState.Focused or WebView2GameHost.PresentationState.VisibleSingle),
                _ => host.CurrentPresentationState is WebView2GameHost.PresentationState.ActiveThumbnail or WebView2GameHost.PresentationState.HiddenConnected
            });
            host.SetMemoryTarget(low);
        }
        FooterStatus.Text = $"Perfil {mode switch { "Maximum" => "Desempenho máximo", "Economy" => "Econômico", _ => "Equilibrado" }} · memória {(minimized ? "Low (janela minimizada)" : "adaptativa")}";
    }
    private async Task UpdateMetricsAsync()
    {
        if (_metricsSampling || ShowMetricsCheck.IsChecked != true) return;
        var host = _activeHost ?? _hosts.Values.FirstOrDefault();
        if (host is null) { MetricsStatus.Text = "CPU/RAM · abra uma conta"; return; }
        _metricsSampling = true;
        try
        {
            var metrics = await host.CaptureProcessMetricsAsync(TimeSpan.FromSeconds(1));
            var gpu = metrics.ProcessesByKind.GetValueOrDefault("Gpu");
            MetricsStatus.Text = $"CPU {metrics.CpuPercent:0.00}% · RAM {metrics.PrivateMemoryMb:0} MB · trabalho {metrics.WorkingSetMb:0} MB · {metrics.ProcessCount} proc. · GPU {gpu}";
        }
        catch (Exception ex) { MetricsStatus.Text = $"CPU/RAM indisponível · {ex.Message}"; }
        finally { _metricsSampling = false; }
    }
}
