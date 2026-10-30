using IdleForge.App.Browser;
using IdleForge.Core;
using IdleForge.Core.Persistence;
using System.Windows;
using System.Windows.Controls;

namespace IdleForge.App;

public partial class ScriptManagerWindow : Window
{
    private readonly List<UserScript> _scripts; private readonly GameProfile _profile; private readonly WebView2GameHost _host; private readonly IdleForgeRepository _repository; private readonly Func<Task> _applyAndReload; private readonly IReadOnlyList<GameProfile> _openProfiles; private readonly Func<IReadOnlyList<GameProfile>, Task> _reloadProfiles;
    private List<PanelRow> _rows = []; private bool _loading;
    private sealed class PanelRow
    {
        public required ScriptPanelBinding Binding { get; set; }
        public string Label => Binding.Label; public string Detection => Binding.AutomaticallyDetected ? "Automática" : "Manual";
        public string State => Binding.LastVisible ? "Visível" : "Oculto"; public string Preference => Binding.Preference switch { PanelVisibilityPreference.AlwaysShow => "Sempre mostrar", PanelVisibilityPreference.StartHidden => "Iniciar oculto", _ => "Lembrar estado" };
    }
    public ScriptManagerWindow(IReadOnlyList<UserScript> scripts, GameProfile profile, WebView2GameHost host, IdleForgeRepository repository, Func<Task> applyAndReload, IReadOnlyList<GameProfile> openProfiles, Func<IReadOnlyList<GameProfile>, Task> reloadProfiles)
    { InitializeComponent(); _scripts = scripts.ToList(); _profile = profile; _host = host; _repository = repository; _applyAndReload = applyAndReload; _openProfiles = openProfiles; _reloadProfiles = reloadProfiles; ProfileLabel.Text = $"Conta: {profile.Name} · preferências independentes"; ScriptsList.ItemsSource = _scripts; if (scripts.Count > 0) ScriptsList.SelectedIndex = 0; }
    private UserScript? SelectedScript => ScriptsList.SelectedItem as UserScript;
    private PanelRow? SelectedPanel => PanelsGrid.SelectedItem as PanelRow;
    private async void ScriptsList_SelectionChanged(object sender, SelectionChangedEventArgs e) { ExecutionStatus.Text = SelectedScript is { Enabled: true } ? "Ativo nesta conta." : "Desativado para a próxima carga. Se ainda estiver executando, use Aplicar e recarregar conta."; await LoadPanelsAsync(); }

    private async Task SetEnabledAsync(bool enabled)
    {
        if (SelectedScript is not { } script) return; await _repository.SetScriptEnabledForProfileAsync(script.Id, _profile.Id, enabled); var index = _scripts.FindIndex(x => x.Id == script.Id); _scripts[index] = script with { Enabled = enabled }; ScriptsList.Items.Refresh(); ScriptsList.SelectedIndex = index;
        ExecutionStatus.Text = enabled ? "Ativação salva. Recarregue a conta para iniciar uma única instância." : "Desativação salva. A instância atual só para após recarregar a conta.";
    }
    private async void Enable_Click(object sender, RoutedEventArgs e) { await SetEnabledAsync(true); await _applyAndReload(); ExecutionStatus.Text = "Conta recarregada; script ativo em uma única instância."; }
    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Desativar e recarregar esta conta agora? Atividades temporárias desta conta serão interrompidas; as demais não serão afetadas.", "Desativar userscript", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await SetEnabledAsync(false); await _applyAndReload(); ExecutionStatus.Text = "Conta recarregada; script desativado e instância anterior encerrada.";
    }
    private async void ApplyReload_Click(object sender, RoutedEventArgs e) { await _applyAndReload(); ExecutionStatus.Text = SelectedScript is { Enabled: true } ? "Conta recarregada; script ativo." : "Conta recarregada; script desativado e sem instância antiga."; }
    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScript is not { } script) return;
        var preserve = MessageBox.Show(this, $"Remover {script.Metadata.Name} somente da conta {_profile.Name}?\n\nSim: preservar dados GM e aprendizado (recomendado).\nNão: escolher excluir os dados desta conta.\nCancelar: não remover.", "Desinstalar userscript", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (preserve == MessageBoxResult.Cancel) return;
        if (preserve == MessageBoxResult.No && MessageBox.Show(this, "Excluir permanentemente os dados GM e associações de painéis deste script nesta conta? Esta ação não afeta cookies nem outros scripts.", "Confirmar exclusão de dados", MessageBoxButton.YesNo, MessageBoxImage.Stop) != MessageBoxResult.Yes) return;
        await _repository.RemoveScriptFromProfileAsync(script.Id, _profile.Id); if (preserve == MessageBoxResult.No) await _repository.DeleteScriptDataAsync(script.Id, _profile.Id); await SetEnabledAsync(false);
        await _applyAndReload(); ExecutionStatus.Text = preserve == MessageBoxResult.Yes ? "Removido desta conta; dados preservados e instância encerrada." : "Removido desta conta, dados associados excluídos e instância encerrada.";
    }
    private async void UninstallProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScript is not { } script) return; var picker = new ProfileReloadWindow(_openProfiles, _profile.Id, "Desinstalar em contas", "Selecione exatamente as contas das quais o script será removido. As demais não serão alteradas.", "Remover das selecionadas") { Owner = this }; if (picker.ShowDialog() != true) return;
        var deleteData = MessageBox.Show(this, "Preservar dados GM e aprendizado nas contas selecionadas?\n\nSim é recomendado. Não solicitará confirmação adicional para excluir os dados deste script.", "Dados do userscript", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No;
        if (deleteData && MessageBox.Show(this, "Excluir permanentemente os dados deste script nas contas selecionadas?", "Confirmação adicional", MessageBoxButton.YesNo, MessageBoxImage.Stop) != MessageBoxResult.Yes) return;
        foreach (var profile in picker.SelectedProfiles) { await _repository.RemoveScriptFromProfileAsync(script.Id, profile.Id); if (deleteData) await _repository.DeleteScriptDataAsync(script.Id, profile.Id); }
        await _reloadProfiles(picker.SelectedProfiles); await SetEnabledAsync(false); ExecutionStatus.Text = $"Removido de {picker.SelectedProfiles.Count} conta(s); demais contas preservadas.";
    }
    private async Task LoadPanelsAsync(bool discover = true)
    {
        if (SelectedScript is not { } script) return; _loading = true;
        if (discover)
        {
            var existing = await _repository.GetPanelBindingsAsync(_profile.Id, script.Id);
            foreach (var panel in await _host.DiscoverPanelsAsync(script))
                if (!existing.Any(x => x.Selector == panel.Selector)) await _repository.SavePanelBindingAsync(new(Guid.NewGuid(), script.Id, _profile.Id, panel.Selector, panel.Label, true, PanelVisibilityPreference.AlwaysShow, true, DateTimeOffset.UtcNow));
        }
        _rows = (await _repository.GetPanelBindingsAsync(_profile.Id, script.Id)).Select(x => new PanelRow { Binding = x }).ToList(); PanelsGrid.ItemsSource = _rows; PanelsGrid.Items.Refresh();
        PanelSummary.Text = _rows.Count == 0 ? "Nenhum painel identificado automaticamente. O script continua executando; use a seleção manual avançada somente se necessário." : $"{_rows.Count} painel(is) identificado(s). Sem seleção, Mostrar/Ocultar controla todos.";
        CompatibilityLabel.Text = script.MainWorldCompatibilityApproved && string.Equals(script.Metadata.Sandbox, "raw", StringComparison.OrdinalIgnoreCase) ? "@sandbox raw: identificação automática usa somente evidências estáticas de criação e inserção; seleção manual permanece disponível." : "";
        PreferenceBox.SelectedIndex = -1; _loading = false;
    }
    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScript is not { } script) return; var found = await _host.DiscoverPanelsAsync(script);
        foreach (var panel in found) if (!_rows.Any(x => x.Binding.Selector == panel.Selector)) await _repository.SavePanelBindingAsync(new(Guid.NewGuid(), script.Id, _profile.Id, panel.Selector, panel.Label, true, PanelVisibilityPreference.AlwaysShow, true, DateTimeOffset.UtcNow));
        await LoadPanelsAsync(false); if (found.Count == 0) MessageBox.Show(this, "Nenhuma interface com autoria e características de painel foi encontrada. Use a seleção manual se o script realmente possuir uma interface.", "Painéis", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async void Manual_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedScript is not { } script) return; Hide(); WebView2GameHost.PanelDescriptor? panel = null;
        try { panel = await _host.SelectPanelManuallyAsync(); } finally { Show(); Activate(); }
        if (panel is null) return; var warning = panel.Width > 900 || panel.Height > 700 ? "\n\nAVISO: o elemento ocupa grande parte da página e pode ser essencial ao jogo." : "";
        if (MessageBox.Show(this, $"Associar '{panel.Label}' ({panel.Width:0}×{panel.Height:0}, {panel.InteractiveElements} controles) ao script {script.Metadata.Name}?{warning}", "Confirmar painel", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _repository.SavePanelBindingAsync(new(Guid.NewGuid(), script.Id, _profile.Id, panel.Selector, panel.Label, false, PanelVisibilityPreference.AlwaysShow, true, DateTimeOffset.UtcNow)); await LoadPanelsAsync(false);
    }
    private async Task SetVisibleAsync(bool visible)
    {
        var targets = SelectedPanel is { } selected ? new[] { selected } : _rows.ToArray();
        foreach (var row in targets) { await _host.SetPanelVisibilityAsync(row.Binding.Selector, visible); row.Binding = row.Binding with { LastVisible = visible, UpdatedAt = DateTimeOffset.UtcNow }; await _repository.SavePanelBindingAsync(row.Binding); }
        await LoadPanelsAsync(false);
    }
    private async void Show_Click(object sender, RoutedEventArgs e) => await SetVisibleAsync(true);
    private async void Hide_Click(object sender, RoutedEventArgs e) => await SetVisibleAsync(false);
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var targets = SelectedPanel is { } selected ? new[] { selected } : _rows.ToArray();
        foreach (var row in targets) { await _host.SetPanelVisibilityAsync(row.Binding.Selector, true); row.Binding = row.Binding with { Preference = PanelVisibilityPreference.AlwaysShow, LastVisible = true, UpdatedAt = DateTimeOffset.UtcNow }; await _repository.SavePanelBindingAsync(row.Binding); }
        await LoadPanelsAsync(false);
    }
    private async void PreferenceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PreferenceBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse<PanelVisibilityPreference>(tag, out var preference)) return;
        var targets = SelectedPanel is { } selected ? new[] { selected } : _rows.ToArray(); foreach (var row in targets) { row.Binding = row.Binding with { Preference = preference, UpdatedAt = DateTimeOffset.UtcNow }; await _repository.SavePanelBindingAsync(row.Binding); }
        await LoadPanelsAsync(false);
    }
    private void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        new LocalScriptUpdatesWindow(_scripts, _repository, _openProfiles, _reloadProfiles) { Owner = this }.ShowDialog();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
