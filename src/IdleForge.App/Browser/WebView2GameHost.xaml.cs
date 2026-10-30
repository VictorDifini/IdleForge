using IdleForge.Core;
using IdleForge.Core.Persistence;
using IdleForge.Core.Networking;
using IdleForge.Core.UserScripts;
using IdleForge.App.Downloads;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdleForge.App.Browser;

public partial class WebView2GameHost : UserControl, IGameBrowserHost
{
    public enum PresentationState { VisibleSingle, VisibleGrid, ActiveThumbnail, Focused, HiddenConnected, TimersLimited, Suspended, Disconnected }
    private bool _suspended;
    private int _recoveryAttempts;
    private readonly IdleForgeRepository _repository;
    private readonly string _userDataFolder;
    private readonly bool _useExperimentalBackgroundFlags;
    private CoreWebView2DevToolsProtocolEventReceiver? _bindingReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _exceptionReceiver;
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private readonly Dictionary<string, ScriptScope> _capabilities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptScope> _bindings = new(StringComparer.Ordinal);
    private readonly SecureHttpBroker _httpBroker = new();
    private readonly RequireCache _requireCache;
    private readonly DownloadManager _downloads;
    private readonly bool _enablePanelTracking;
    private readonly Action<Uri> _externalLauncher;
    private string _profileName = "Conta";
    private Guid _profileId;
    private Uri? _gameStartUri;
    private Uri? _externalFlowStartUri;
    private IReadOnlyList<UserScript> _startedScripts = [];
    private CoreWebView2MemoryUsageTargetLevel _desiredMemoryTarget = CoreWebView2MemoryUsageTargetLevel.Normal;
    private sealed record ScriptScope(UserScript Script, Guid ProfileId)
    {
        public string RegistrationId { get; set; } = string.Empty;
        public string? BindingName { get; set; }
        public string? WorldName { get; set; }
        public string Capability { get; set; } = string.Empty;
    }
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ScriptException;
    public event EventHandler<PresentationState>? PresentationStateChanged;
    public sealed record NavigationState(Uri? CurrentUri, bool CanGoBack, bool CanGoForward, bool IsAtGame, Uri? GameUri);
    public event EventHandler<NavigationState>? NavigationStateChanged;
    public PresentationState CurrentPresentationState { get; private set; } = PresentationState.Disconnected;
    public int NavigationCount { get; private set; }
    public int EnabledScriptCount { get; private set; }
    public WebView2GameHost(IdleForgeRepository repository, string? userDataFolder = null, bool useExperimentalBackgroundFlags = false, DownloadManager? downloads = null, bool enablePanelTracking = true, Action<Uri>? externalLauncher = null)
    {
        _repository = repository;
        _userDataFolder = userDataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdleForge", "BrowserData");
        _useExperimentalBackgroundFlags = useExperimentalBackgroundFlags;
        _downloads = downloads ?? new DownloadManager(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdleForge"));
        _enablePanelTracking = enablePanelTracking;
        _externalLauncher = externalLauncher ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        _requireCache = new RequireCache(Path.Combine(_userDataFolder, "RequireCache"));
        InitializeComponent();
    }

    public async Task StartAsync(GameDefinition game, GameProfile profile, IReadOnlyList<UserScript> scripts, CancellationToken cancellationToken = default)
    {
        _profileName = profile.Name;
        _profileId = profile.Id; _gameStartUri = game.StartUri; _startedScripts = scripts.Where(x => x.Enabled).ToArray();
        EnabledScriptCount = scripts.Count(x => x.Enabled);
        Browser.CreationProperties = new CoreWebView2CreationProperties
        {
            UserDataFolder = _userDataFolder,
            ProfileName = profile.BrowserProfileName, AreBrowserExtensionsEnabled = false,
            AdditionalBrowserArguments = _useExperimentalBackgroundFlags ? "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows" : null
        };
        await Browser.EnsureCoreWebView2Async(); cancellationToken.ThrowIfCancellationRequested(); ConfigureSecurity();
        await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
        _bindingReceiver = Browser.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled");
        _bindingReceiver.DevToolsProtocolEventReceived += OnCdpBindingCalled;
        _exceptionReceiver = Browser.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
        _exceptionReceiver.DevToolsProtocolEventReceived += (_, e) => ScriptException?.Invoke(this, e.ParameterObjectAsJson);
        foreach (var script in scripts.Where(x => x.Enabled))
        {
            var capability = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var scope = new ScriptScope(script, profile.Id) { Capability = capability };
            var values = await _repository.GetScriptValuesAsync(script.Id, profile.Id, cancellationToken);
            try
            {
                var dependencies = await _requireCache.ResolveAsync(script.Metadata.Requires, cancellationToken);
                if (script.MainWorldCompatibilityApproved && string.Equals(script.Metadata.Sandbox, "raw", StringComparison.OrdinalIgnoreCase))
                    await RegisterMainWorldAsync(scope, values, dependencies);
                else
                    await RegisterIsolatedAsync(scope, values, dependencies);
            }
            catch (Exception ex) { StatusChanged?.Invoke(this, $"{script.Metadata.Name}: bloqueado; isolamento/dependência falhou: {ex.Message}"); }
        }
        Browser.CoreWebView2.ProcessFailed += OnProcessFailed;
        Browser.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && !IsGameOrigin(target)) _externalFlowStartUri ??= target;
            if (IsGoogleAuthenticationUri(e.Uri)) { e.Cancel = true; OpenExternal((_externalFlowStartUri ?? target)?.AbsoluteUri ?? e.Uri); SetStatus("Votação aberta no navegador padrão", "O fluxo externo completo foi reiniciado fora do WebView para preservar parâmetros, cookies e retorno OAuth do serviço.", false); return; }
            NavigationCount++; SetStatus("Carregando", game.Name, true);
        };
        Browser.CoreWebView2.NavigationCompleted += async (_, e) => { if (e.IsSuccess) { _recoveryAttempts = 0; if (Browser.Source is { } current && IsGameOrigin(current)) _externalFlowStartUri = null; _ = ReapplyPanelPreferencesAsync(); } RaiseNavigationState(); SetStatus(e.IsSuccess ? "Ativo" : "Falha na navegação", e.IsSuccess ? game.Name : e.WebErrorStatus.ToString(), !e.IsSuccess); await Task.CompletedTask; };
        Browser.CoreWebView2.HistoryChanged += (_, _) => RaiseNavigationState();
        Browser.CoreWebView2.SourceChanged += (_, _) => RaiseNavigationState();
        Browser.Source = game.StartUri;
        SetPresentationState(PresentationState.VisibleSingle);
    }

    private async void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        SetStatus("Processo interrompido", e.ProcessFailedKind.ToString(), true);
        if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)) return;
        if (++_recoveryAttempts > 3) { SetStatus("Recuperação interrompida", "Três tentativas falharam; reinicie a sessão.", true); return; }
        SetStatus("Recuperando sessão", $"Tentativa {_recoveryAttempts} de 3", true);
        await Task.Delay(TimeSpan.FromMilliseconds(250 * _recoveryAttempts));
        try { Browser.CoreWebView2.Reload(); } catch (Exception ex) { SetStatus("Falha na recuperação", ex.Message, true); }
    }

    private async Task RegisterIsolatedAsync(ScriptScope scope, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values, IReadOnlyList<string>? dependencies = null)
    {
        scope.BindingName ??= $"__idleforge_{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))}";
        scope.WorldName ??= $"idleforge:{scope.ProfileId:N}:{scope.Script.Id:N}";
        await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.addBinding", System.Text.Json.JsonSerializer.Serialize(new { name = scope.BindingName, executionContextName = scope.WorldName }));
        var source = UserScriptBootstrapBuilder.Build(scope.Script, scope.ProfileId, scope.Capability, values, scope.BindingName, dependencies, enablePanelTracking: _enablePanelTracking);
        var response = await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.addScriptToEvaluateOnNewDocument", System.Text.Json.JsonSerializer.Serialize(new { source, worldName = scope.WorldName, runImmediately = false }));
        using var result = System.Text.Json.JsonDocument.Parse(response); scope.RegistrationId = result.RootElement.GetProperty("identifier").GetString()!;
        _bindings[scope.BindingName] = scope;
        _capabilities[scope.Capability] = scope;
    }

    private async Task RegisterMainWorldAsync(ScriptScope scope, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values, IReadOnlyList<string>? dependencies = null)
    {
        scope.BindingName = null;
        scope.WorldName = null;
        var source = UserScriptBootstrapBuilder.Build(scope.Script, scope.ProfileId, scope.Capability, values, dependencies: dependencies, waitForDocumentElement: true);
        scope.RegistrationId = await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(source);
        _capabilities[scope.Capability] = scope;
    }

    private async void OnCdpBindingCalled(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var envelope = System.Text.Json.JsonDocument.Parse(e.ParameterObjectAsJson); var root = envelope.RootElement;
            if (!root.TryGetProperty("name", out var name) || name.GetString() is not { } binding || !_bindings.TryGetValue(binding, out var scope) ||
                !root.TryGetProperty("payload", out var payload) || payload.GetString() is not { } json) return;
            using var message = System.Text.Json.JsonDocument.Parse(json);
            var requestId = message.RootElement.TryGetProperty("requestId", out var request) ? request.GetString() : null;
            try
            {
                var op = message.RootElement.GetProperty("op").GetString();
                if (op == "http.abort" && requestId is not null) { _httpBroker.Abort(requestId); return; }
                if (op == "http.request" && requestId is not null && root.TryGetProperty("executionContextId", out var httpContext)) { await HandleHttpAsync(message.RootElement, scope, httpContext.GetInt32(), requestId); return; }
                if (op == "download.abort" && requestId is not null) { _downloads.AbortScript(requestId); return; }
                if (op == "download.request" && requestId is not null && root.TryGetProperty("executionContextId", out var downloadContext)) { await HandleDownloadAsync(message.RootElement, scope, downloadContext.GetInt32(), requestId); return; }
                await ApplyStorageMessageAsync(message.RootElement, scope);
                if (requestId is not null && root.TryGetProperty("executionContextId", out var context)) await SendCdpAcknowledgementAsync(context.GetInt32(), requestId, true, null);
            }
            catch (Exception ex)
            {
                if (requestId is not null && root.TryGetProperty("executionContextId", out var context)) await SendCdpAcknowledgementAsync(context.GetInt32(), requestId, false, ex.Message);
                throw;
            }
        }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"Falha no bridge isolado: {ex.Message}"); }
    }

    private void ConfigureSecurity()
    {
        var settings = Browser.CoreWebView2.Settings; settings.AreDevToolsEnabled = true; settings.IsWebMessageEnabled = false; settings.AreHostObjectsAllowed = false;
        Browser.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (!e.IsUserInitiated) { SetStatus("Pop-up bloqueado", "A janela não foi iniciada por uma ação do usuário.", false); return; }
            OpenExternal(e.Uri); SetStatus("Página externa aberta", "A URL inicial completa foi aberta no navegador padrão; o jogo continua carregado nesta conta.", false);
        };
        Browser.CoreWebView2.PermissionRequested += (_, e) => { if (e.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Geolocation) e.State = CoreWebView2PermissionState.Deny; };
        Browser.CoreWebView2.DownloadStarting += (_, e) => _downloads.HandleNativeDownload(Window.GetWindow(this), _profileName, e);
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson); var root = document.RootElement;
            if (!root.TryGetProperty("channel", out var channel) || channel.GetString() != "idleforge-gm-v1" ||
                !root.TryGetProperty("capability", out var token) || token.GetString() is not { } capability ||
                !_capabilities.TryGetValue(capability, out var scope) || !root.TryGetProperty("op", out var opElement) ||
                !root.TryGetProperty("key", out _)) return;
            await ApplyStorageMessageAsync(root, scope);
        }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"Falha no armazenamento GM: {ex.Message}"); }
    }

    private async Task SendCdpAcknowledgementAsync(int contextId, string requestId, bool success, string? error)
    {
        var expression = $"globalThis.__idleforgeResolve?.({System.Text.Json.JsonSerializer.Serialize(requestId)},{success.ToString().ToLowerInvariant()},{System.Text.Json.JsonSerializer.Serialize(error)})";
        await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", System.Text.Json.JsonSerializer.Serialize(new { expression, contextId, returnByValue = true }));
    }

    private async Task HandleHttpAsync(System.Text.Json.JsonElement root, ScriptScope scope, int contextId, string requestId)
    {
        try
        {
            var request = root.GetProperty("request");
            var headers = request.TryGetProperty("headers", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Object
                ? h.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase) : null;
            var dto = new BrokerRequest(requestId, request.GetProperty("method").GetString() ?? "GET", new Uri(request.GetProperty("url").GetString()!, UriKind.Absolute), headers,
                request.TryGetProperty("body", out var body) && body.ValueKind != System.Text.Json.JsonValueKind.Null ? body.GetString() : null,
                request.TryGetProperty("timeout", out var timeout) ? timeout.GetInt32() : 30000,
                request.TryGetProperty("responseType", out var type) ? type.GetString() ?? "text" : "text");
            var response = await _httpBroker.SendAsync(dto, new ConnectPolicy(scope.Script.Metadata.Connect, scope.Script.BroadConnectApproved));
            await SendCdpHttpResultAsync(contextId, requestId, true, null, response, "load");
        }
        catch (OperationCanceledException ex) { await SendCdpHttpResultAsync(contextId, requestId, false, ex.Message, null, "timeout"); }
        catch (Exception ex) { await SendCdpHttpResultAsync(contextId, requestId, false, ex.Message, null, "error"); }
    }

    private async Task HandleDownloadAsync(System.Text.Json.JsonElement root, ScriptScope scope, int contextId, string requestId)
    {
        if (!scope.Script.Metadata.Grants.Any(x => x.Equals("GM_download", StringComparison.OrdinalIgnoreCase) || x.Equals("GM.download", StringComparison.OrdinalIgnoreCase)))
        { await SendCdpDownloadResultAsync(contextId, requestId, false, "@grant GM_download/GM.download não declarado.", null); return; }
        try
        {
            var request = root.GetProperty("request"); var uri = new Uri(request.GetProperty("url").GetString()!, UriKind.Absolute);
            var headers = request.TryGetProperty("headers", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.Object ? h.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase) : null;
            var name = request.TryGetProperty("name", out var n) ? n.GetString() : null;
            var item = uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
                ? await _downloads.SaveDataUrlAsync(_profileName, requestId, uri, name)
                : uri.Scheme is "http" or "https"
                    ? await _downloads.DownloadFromScriptAsync(_profileName, requestId, uri, name, headers, new ConnectPolicy(scope.Script.Metadata.Connect, scope.Script.BroadConnectApproved))
                    : throw new NotSupportedException("GM_download aceita HTTP/HTTPS e data:. Para blob:, use um link download no próprio documento.");
            await SendCdpDownloadResultAsync(contextId, requestId, true, null, new { loaded = item.BytesReceived, total = item.TotalBytes, lengthComputable = true }, "progress");
            await SendCdpDownloadResultAsync(contextId, requestId, true, null, new { item.FileName, item.Path, item.BytesReceived });
        }
        catch (OperationCanceledException) { await SendCdpDownloadResultAsync(contextId, requestId, false, "Download cancelado.", null, "abort"); }
        catch (Exception ex) { await SendCdpDownloadResultAsync(contextId, requestId, false, ex.Message, null); }
    }

    private async Task SendCdpDownloadResultAsync(int contextId, string requestId, bool success, string? error, object? result, string eventName = "load")
    {
        var expression = $"globalThis.__idleforgeResolve?.({System.Text.Json.JsonSerializer.Serialize(requestId)},{success.ToString().ToLowerInvariant()},{System.Text.Json.JsonSerializer.Serialize(error)},{System.Text.Json.JsonSerializer.Serialize(result)},{System.Text.Json.JsonSerializer.Serialize(eventName)})";
        await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", System.Text.Json.JsonSerializer.Serialize(new { expression, contextId, returnByValue = true }));
    }

    private async Task SendCdpHttpResultAsync(int contextId, string requestId, bool success, string? error, BrokerResponse? response, string eventName)
    {
        var payload = response is null ? "null" : System.Text.Json.JsonSerializer.Serialize(new { status = response.Status, statusText = response.StatusText, finalUrl = response.FinalUrl.AbsoluteUri, responseHeaders = string.Join("\r\n", response.Headers.SelectMany(x => x.Value.Select(v => $"{x.Key}: {v}"))), responseText = response.ResponseText, response = response.Response });
        var expression = $"globalThis.__idleforgeResolve?.({System.Text.Json.JsonSerializer.Serialize(requestId)},{success.ToString().ToLowerInvariant()},{System.Text.Json.JsonSerializer.Serialize(error)},{payload},{System.Text.Json.JsonSerializer.Serialize(eventName)})";
        await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", System.Text.Json.JsonSerializer.Serialize(new { expression, contextId, returnByValue = true }));
    }

    private async Task ApplyStorageMessageAsync(System.Text.Json.JsonElement root, ScriptScope scope)
    {
        if (!root.TryGetProperty("op", out var opElement) || !root.TryGetProperty("key", out var keyElement) || keyElement.GetString() is not { Length: > 0 } key || key.Length > 512) return;
        await _storageGate.WaitAsync();
        try
        {
            if (opElement.GetString() == "set" && root.TryGetProperty("value", out var value))
            { await _repository.SetScriptValueAsync(scope.Script.Id, scope.ProfileId, key, value.Clone()); await RefreshRegistrationAsync(scope); }
            else if (opElement.GetString() == "delete")
            { await _repository.DeleteScriptValueAsync(scope.Script.Id, scope.ProfileId, key); await RefreshRegistrationAsync(scope); }
        }
        finally { _storageGate.Release(); }
    }

    private async Task RefreshRegistrationAsync(ScriptScope scope)
    {
        var values = await _repository.GetScriptValuesAsync(scope.Script.Id, scope.ProfileId);
        if (scope.BindingName is not null)
        {
            if (!string.IsNullOrEmpty(scope.RegistrationId)) await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.removeScriptToEvaluateOnNewDocument", System.Text.Json.JsonSerializer.Serialize(new { identifier = scope.RegistrationId }));
            await RegisterIsolatedAsync(scope, values, await _requireCache.ResolveAsync(scope.Script.Metadata.Requires));
        }
        else
        {
            if (!string.IsNullOrEmpty(scope.RegistrationId)) Browser.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(scope.RegistrationId);
            await RegisterMainWorldAsync(scope, values, await _requireCache.ResolveAsync(scope.Script.Metadata.Requires));
        }
    }

    private void SetStatus(string title, string detail, bool overlay)
    {
        Dispatcher.Invoke(() => { StatusTitle.Text = title; StatusDetail.Text = detail; StatusOverlay.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed; });
        StatusChanged?.Invoke(this, title);
    }
    public Task NavigateAsync(Uri uri) { Browser.CoreWebView2?.Navigate(uri.AbsoluteUri); return Task.CompletedTask; }
    public Task ReloadAsync() { Browser.CoreWebView2?.Reload(); return Task.CompletedTask; }
    public double ZoomFactor { get => Browser.ZoomFactor; set => Browser.ZoomFactor = Math.Clamp(value, 0.5, 3.0); }
    public void GoBack() { if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack(); }
    public void GoForward() { if (Browser.CoreWebView2?.CanGoForward == true) Browser.CoreWebView2.GoForward(); }
    public async Task ReturnToGameAsync()
    {
        if (Browser.CoreWebView2 is null || _gameStartUri is null || IsGameOrigin(Browser.Source)) return;
        try
        {
            var raw = await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}"); using var doc = JsonDocument.Parse(raw); var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            var match = entries.LastOrDefault(x => x.TryGetProperty("url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) && IsGameOrigin(uri));
            if (match.ValueKind != JsonValueKind.Undefined) { await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.navigateToHistoryEntry", JsonSerializer.Serialize(new { entryId = match.GetProperty("id").GetInt32() })); return; }
        }
        catch { }
        Browser.CoreWebView2.Navigate(_gameStartUri.AbsoluteUri);
    }
    public async Task ApplyScriptsAndReloadAsync(IReadOnlyList<UserScript> scripts)
    {
        if (Browser.CoreWebView2 is null) return;
        foreach (var scope in _bindings.Values.ToArray())
        {
            if (!string.IsNullOrEmpty(scope.RegistrationId)) await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.removeScriptToEvaluateOnNewDocument", JsonSerializer.Serialize(new { identifier = scope.RegistrationId }));
            if (!string.IsNullOrEmpty(scope.BindingName)) try { await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.removeBinding", JsonSerializer.Serialize(new { name = scope.BindingName })); } catch { }
        }
        foreach (var scope in _capabilities.Values.Concat(_bindings.Values).Distinct().Where(x => x.BindingName is null)) if (!string.IsNullOrEmpty(scope.RegistrationId)) Browser.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(scope.RegistrationId);
        _bindings.Clear(); _capabilities.Clear(); _startedScripts = scripts.Where(x => x.Enabled).ToArray(); EnabledScriptCount = _startedScripts.Count;
        foreach (var script in _startedScripts)
        {
            var scope = new ScriptScope(script, _profileId) { Capability = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) }; var values = await _repository.GetScriptValuesAsync(script.Id, _profileId); var dependencies = await _requireCache.ResolveAsync(script.Metadata.Requires);
            if (script.MainWorldCompatibilityApproved && string.Equals(script.Metadata.Sandbox, "raw", StringComparison.OrdinalIgnoreCase)) await RegisterMainWorldAsync(scope, values, dependencies); else await RegisterIsolatedAsync(scope, values, dependencies);
        }
        Browser.CoreWebView2.Reload();
    }
    public Task<string> ExecuteScriptForDiagnosticsAsync(string script) => Browser.CoreWebView2.ExecuteScriptAsync(script);
    public async Task SetFileInputForDiagnosticsAsync(string selector, string filePath)
    {
        var documentJson = await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{}");
        using var document = System.Text.Json.JsonDocument.Parse(documentJson); var rootNodeId = document.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
        var queryJson = await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.querySelector", System.Text.Json.JsonSerializer.Serialize(new { nodeId = rootNodeId, selector }));
        using var query = System.Text.Json.JsonDocument.Parse(queryJson); var nodeId = query.RootElement.GetProperty("nodeId").GetInt32();
        if (nodeId == 0) throw new InvalidOperationException($"Input {selector} não encontrado.");
        await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", System.Text.Json.JsonSerializer.Serialize(new { files = new[] { Path.GetFullPath(filePath) }, nodeId }));
    }
    public sealed record PanelDescriptor(string Selector, string Label, bool Automatic, double Width, double Height, string Position, int InteractiveElements);
    public async Task<IReadOnlyList<PanelDescriptor>> DiscoverPanelsAsync(UserScript script)
    {
        if (Browser.CoreWebView2 is null || !_startedScripts.Any(x => x.Id == script.Id)) return [];
        var owner = PanelOwner(_profileId, script.Id); var expression = PanelRuntime + $";JSON.stringify(globalThis.__idleforgePanelRuntime.discover({JsonSerializer.Serialize(owner)}))";
        var raw = await Browser.CoreWebView2.ExecuteScriptAsync(expression); var json = JsonSerializer.Deserialize<string>(raw) ?? "[]";
        return JsonSerializer.Deserialize<List<PanelDescriptor>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }
    public async Task<bool> SetPanelVisibilityAsync(string selector, bool visible)
    {
        if (Browser.CoreWebView2 is null) return false; var expression = PanelRuntime + $";globalThis.__idleforgePanelRuntime.setVisible({JsonSerializer.Serialize(selector)},{visible.ToString().ToLowerInvariant()})";
        return JsonSerializer.Deserialize<bool>(await Browser.CoreWebView2.ExecuteScriptAsync(expression));
    }
    public async Task<PanelDescriptor?> SelectPanelManuallyAsync(CancellationToken cancellationToken = default)
    {
        if (Browser.CoreWebView2 is null) return null;
        var expression = PanelRuntime + ";globalThis.__idleforgePanelRuntime.selectManual()";
        var result = await Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true }));
        using var document = JsonDocument.Parse(result); if (!document.RootElement.TryGetProperty("result", out var runtime) || !runtime.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) return null;
        return JsonSerializer.Deserialize<PanelDescriptor>(value.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    public async Task ReapplyPanelPreferencesAsync()
    {
        if (_profileId == Guid.Empty || Browser.CoreWebView2 is null) return;
        foreach (var delay in new[] { 200, 2000, 5000 })
        {
            await Task.Delay(delay);
            if (Browser.CoreWebView2 is null) return;
            var bindings = await _repository.GetPanelBindingsAsync(_profileId);
            foreach (var binding in bindings)
            {
                var visible = binding.Preference == PanelVisibilityPreference.AlwaysShow || binding.Preference == PanelVisibilityPreference.RememberLastState && binding.LastVisible;
                await SetPanelVisibilityAsync(binding.Selector, visible);
            }
        }
    }
    private static string PanelOwner(Guid profileId, Guid scriptId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{profileId:N}:{scriptId:N}:panel")))[..24].ToLowerInvariant();
    private const string PanelRuntime = """
      (()=>{if(globalThis.__idleforgePanelRuntime)return;const hidden='data-idleforge-panel-hidden';
      const roots=()=>{const out=[document],walk=r=>{for(const e of r.querySelectorAll('*'))if(e.shadowRoot){out.push(e.shadowRoot);walk(e.shadowRoot)}};walk(document);return out};
      const style=r=>{if(r.querySelector?.('#idleforge-panel-visibility-style'))return;const s=document.createElement('style');s.id='idleforge-panel-visibility-style';s.textContent='[data-idleforge-panel-hidden="1"]{visibility:hidden!important;opacity:0!important;pointer-events:none!important}';r===document?(document.head||document.documentElement).append(s):r.append(s)};
      const visual=e=>{const own=e.getBoundingClientRect(),nodes=e.shadowRoot?[...e.shadowRoot.querySelectorAll('*')]:[],child=nodes.filter(x=>{const r=x.getBoundingClientRect(),c=getComputedStyle(x);return r.width>=40&&r.height>=20&&c.display!=='none'&&c.visibility!=='hidden'}).sort((a,b)=>{const x=a.getBoundingClientRect(),y=b.getBoundingClientRect();return y.width*y.height-x.width*x.height})[0],r=own.width>=40&&own.height>=20?own:child?.getBoundingClientRect()||own,c=getComputedStyle(child||e);return{r,c,interactive:(e.shadowRoot||e).querySelectorAll('button,input,select,textarea,a,[role=button]').length}};
      const candidates=owner=>{const all=roots().flatMap(r=>[...r.querySelectorAll(`[data-idleforge-panel-owner="${CSS.escape(owner)}"]`)]);return all.filter(e=>{if(!e.isConnected||/^(SCRIPT|STYLE|LINK|META|TEMPLATE)$/.test(e.tagName))return false;if(e.getRootNode() instanceof ShadowRoot&&e.getRootNode().host?.getAttribute('data-idleforge-panel-owner')===owner)return false;const v=visual(e);return v.r.width>=40&&v.r.height>=20&&(v.c.position==='fixed'||v.c.position==='absolute'||v.c.position==='sticky'||v.interactive>0||e.getAttribute('role')==='dialog'||!!e.shadowRoot)}).filter(e=>!all.some(p=>p!==e&&p.contains(e)&&visual(p).r.width>=40&&visual(p).r.height>=20))};
      const resolve=selector=>{if(selector.startsWith('owned:')){const [,owner,id]=selector.split(':');const wanted=decodeURIComponent(id);return roots().flatMap(r=>[...r.querySelectorAll(`[data-idleforge-panel-owner="${CSS.escape(owner)}"]`)]).find(e=>e.id===wanted)||null}if(selector.startsWith('auto:')){const [,owner,index]=selector.split(':');return candidates(owner)[Number(index)]||null}let root=document,parts=selector.split(' >>> '),el=null;for(let i=0;i<parts.length;i++){el=root.querySelector(parts[i]);if(!el)return null;if(i<parts.length-1)root=el.shadowRoot}return el};
      const describe=(e,selector,automatic)=>{const v=visual(e),r=v.r,c=v.c,root=e.shadowRoot||e;return{selector,label:(e.getAttribute('aria-label')||e.getAttribute('title')||e.id||root.querySelector('h1,h2,h3,[role=heading]')?.textContent||e.tagName).trim().slice(0,80),automatic,width:Math.round(r.width),height:Math.round(r.height),position:c.position,interactiveElements:v.interactive}};
      const cssPath=e=>{const segments=[];for(;;){const root=e.getRootNode();let part;if(e.id&&root.querySelectorAll(`#${CSS.escape(e.id)}`).length===1)part=`#${CSS.escape(e.id)}`;else{const tag=e.tagName.toLowerCase(),siblings=[...e.parentElement?.children||[]].filter(x=>x.tagName===e.tagName);part=tag+(siblings.length>1?`:nth-of-type(${siblings.indexOf(e)+1})`:'')}segments.unshift(part);if(root===document)break;e=root.host;segments.unshift('>>>')}return segments.join(' ').replace(/ >>> /g,' >>> ')};
      globalThis.__idleforgePanelRuntime={discover:owner=>candidates(owner).map((e,i)=>describe(e,e.id?`owned:${owner}:${encodeURIComponent(e.id)}`:`auto:${owner}:${i}`,true)),setVisible:(selector,visible)=>{const e=resolve(selector);if(!e)return false;style(e.getRootNode());visible?e.removeAttribute(hidden):e.setAttribute(hidden,'1');return true},selectManual:()=>new Promise(resolve=>{let current;const over=e=>{current=e.target;current.style.outline='3px solid #38d39f';current.style.outlineOffset='2px'},out=e=>{if(e.target===current){current.style.outline='';current.style.outlineOffset=''}},click=e=>{e.preventDefault();e.stopImmediatePropagation();cleanup();const t=e.target,r=t.getBoundingClientRect(),essential=t===document.body||t===document.documentElement||r.width>innerWidth*.9&&r.height>innerHeight*.9;resolve({...describe(t,cssPath(t),false),essential})},cleanup=()=>{removeEventListener('mouseover',over,true);removeEventListener('mouseout',out,true);removeEventListener('click',click,true);if(current){current.style.outline='';current.style.outlineOffset=''}};addEventListener('mouseover',over,true);addEventListener('mouseout',out,true);addEventListener('click',click,true)})}})()
      """;
    public Task CrashRendererForDiagnosticsAsync() => Browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.crash", "{}");
    public uint BrowserProcessId => Browser.CoreWebView2?.BrowserProcessId ?? 0;
    public string ActiveProfileName => Browser.CoreWebView2?.Profile.ProfileName ?? string.Empty;
    public bool IsSuspended => Browser.CoreWebView2?.IsSuspended ?? false;
    public void SetPresentationState(PresentationState state)
    {
        if (_suspended && state != PresentationState.Suspended) return;
        CurrentPresentationState = state; PresentationStateChanged?.Invoke(this, state);
    }
    public void SetMemoryTarget(bool low)
    {
        _desiredMemoryTarget = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
        if (!_suspended && Browser.CoreWebView2 is not null) Browser.CoreWebView2.MemoryUsageTargetLevel = _desiredMemoryTarget;
    }
    public string MemoryTarget => Browser.CoreWebView2?.MemoryUsageTargetLevel.ToString() ?? _desiredMemoryTarget.ToString();
    public void OpenCurrentPageExternally() { var uri = Browser.Source?.AbsoluteUri; if (!string.IsNullOrWhiteSpace(uri)) OpenExternal(uri); }
    private bool IsGameOrigin(Uri? uri) => uri is not null && _gameStartUri is not null && uri.Scheme.Equals(_gameStartUri.Scheme, StringComparison.OrdinalIgnoreCase) && uri.IdnHost.Equals(_gameStartUri.IdnHost, StringComparison.OrdinalIgnoreCase) && uri.Port == _gameStartUri.Port;
    private void RaiseNavigationState() { var core = Browser.CoreWebView2; NavigationStateChanged?.Invoke(this, new(Browser.Source, core?.CanGoBack == true, core?.CanGoForward == true, IsGameOrigin(Browser.Source), _gameStartUri)); }
    private static bool IsGoogleAuthenticationUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.IdnHost.Equals("accounts.google.com", StringComparison.OrdinalIgnoreCase) || uri.IdnHost.EndsWith(".accounts.google.com", StringComparison.OrdinalIgnoreCase));
    private void OpenExternal(string uri) { if (Uri.TryCreate(uri, UriKind.Absolute, out var target) && target.Scheme is "http" or "https") _externalLauncher(target); }
    public Task<ProcessMetricsSnapshot> CaptureProcessMetricsAsync(TimeSpan duration) => ProcessMetricsCollector.CaptureAsync(Browser.CoreWebView2.Environment, duration);
    public async Task SuspendAsync()
    {
        if (_suspended || Browser.CoreWebView2 is null) return;
        Browser.Visibility = Visibility.Collapsed;
        var suspended = await Browser.CoreWebView2.TrySuspendAsync();
        if (!suspended) { Browser.Visibility = Visibility.Visible; SetStatus("Não foi possível suspender", "A página possui atividade que impediu a suspensão.", true); return; }
        _suspended = true; SetPresentationState(PresentationState.Suspended); SetStatus("Suspenso", "Timers e animações JavaScript estão pausados.", true);
    }
    public Task ResumeAsync() { if (_suspended && Browser.CoreWebView2 is not null) { Browser.CoreWebView2.Resume(); Browser.CoreWebView2.MemoryUsageTargetLevel = _desiredMemoryTarget; Browser.Visibility = Visibility.Visible; _suspended = false; StatusOverlay.Visibility = Visibility.Collapsed; SetPresentationState(PresentationState.VisibleSingle); } return Task.CompletedTask; }
}
