using IdleForge.Core;
using IdleForge.Core.Persistence;
using IdleForge.Core.UserScripts;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace IdleForge.App;

public partial class LocalScriptUpdatesWindow : Window
{
    private readonly IdleForgeRepository _repository;
    private readonly LocalScriptUpdateService _updates;
    private readonly IReadOnlyList<GameProfile> _openProfiles;
    private readonly Func<IReadOnlyList<GameProfile>, Task> _reloadProfiles;
    private List<UserScript> _scripts;
    private List<Row> _rows = [];

    public sealed class Row
    {
        public required LocalScriptUpdateCheck Check { get; init; }
        public bool Selected { get; set; }
        public string Name => Check.Script.Metadata.Name;
        public string InstalledVersion => Check.Script.Metadata.Version;
        public string LocalVersion => Check.LocalMetadata?.Version ?? "—";
        public string Path => Check.LocalPath ?? "Não vinculado";
        public string StateText => Check.State switch
        {
            LocalScriptUpdateState.UpToDate => "Atualizado",
            LocalScriptUpdateState.UpdateAvailable when Check.PossibleDowngrade => "Alterado · possível downgrade",
            LocalScriptUpdateState.UpdateAvailable when Check.LocalMetadata?.Version == Check.Script.Metadata.Version => "Conteúdo alterado",
            LocalScriptUpdateState.UpdateAvailable => "Atualização disponível",
            LocalScriptUpdateState.FileNotFound => "Arquivo não encontrado",
            LocalScriptUpdateState.NotLinked => "Origem não vinculada",
            LocalScriptUpdateState.InvalidFile => "Arquivo inválido",
            _ => "Erro de leitura"
        };
    }

    public LocalScriptUpdatesWindow(IReadOnlyList<UserScript> scripts, IdleForgeRepository repository, IReadOnlyList<GameProfile> openProfiles, Func<IReadOnlyList<GameProfile>, Task> reloadProfiles)
    {
        InitializeComponent(); _scripts = scripts.ToList(); _repository = repository; _updates = new(repository); _openProfiles = openProfiles; _reloadProfiles = reloadProfiles;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            IsEnabled = false; StatusText.Text = "Lendo somente os arquivos vinculados…";
            var checks = await _updates.CheckAsync(_scripts);
            _rows = checks.Select(x => new Row { Check = x, Selected = x.CanUpdate }).ToList();
            UpdatesGrid.ItemsSource = _rows;
            var changed = checks.Count(x => x.CanUpdate); var unlinked = checks.Count(x => x.State == LocalScriptUpdateState.NotLinked);
            SummaryText.Text = changed == 0 ? "Todos os scripts vinculados estão atualizados." : $"{changed} atualização(ões) encontrada(s). Revise e selecione antes de instalar.";
            StatusText.Text = unlinked == 0 ? "Nenhum monitoramento permanente está ativo." : $"{unlinked} script(s) sem arquivo local vinculado não foram verificados.";
        }
        finally { IsEnabled = true; }
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void SelectChanged_Click(object sender, RoutedEventArgs e) { foreach (var row in _rows) row.Selected = row.Check.CanUpdate; UpdatesGrid.Items.Refresh(); }

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        if (UpdatesGrid.SelectedItem is not Row row) { MessageBox.Show(this, "Selecione primeiro o script que deseja vincular."); return; }
        var picker = new OpenFileDialog { Filter = "Userscripts (*.user.js)|*.user.js|JavaScript (*.js)|*.js" }; if (picker.ShowDialog(this) != true) return;
        try
        {
            var linked = await _updates.LinkAsync(row.Check.Script, picker.FileName);
            var index = _scripts.FindIndex(x => x.Id == linked.Id); _scripts[index] = linked; await RefreshAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Não foi possível vincular", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(x => x.Selected && x.Check.CanUpdate).ToArray();
        if (selected.Length == 0) { MessageBox.Show(this, "Nenhuma atualização foi selecionada."); return; }
        var updatedIds = new HashSet<Guid>();
        foreach (var row in selected)
        {
            try
            {
                if (row.Check.PossibleDowngrade && MessageBox.Show(this, $"{row.Name}: a versão local {row.LocalVersion} parece anterior à instalada {row.InstalledVersion}. Aplicar mesmo assim?", "Possível downgrade", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) continue;
                var changes = LocalScriptUpdateService.PermissionChanges(row.Check.Script.Metadata, row.Check.LocalMetadata!);
                var broad = row.Check.Script.BroadConnectApproved; var main = row.Check.Script.MainWorldCompatibilityApproved;
                if (changes.Count > 0)
                {
                    if (MessageBox.Show(this, $"{row.Name} solicita novos escopos ou permissões:\n\n{string.Join("\n", changes)}\n\nDeseja revisar e autorizar esta atualização?", "Permissões alteradas", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) continue;
                    var source = await File.ReadAllTextAsync(row.Check.LocalPath!);
                    var preview = new ScriptPreviewWindow(row.Check.LocalMetadata!, new ScriptRiskAnalyzer().Analyze(row.Check.LocalMetadata!), source) { Owner = this };
                    if (preview.ShowDialog() != true) continue;
                    broad = preview.BroadConnectApproved; main = preview.MainWorldCompatibilityApproved;
                }
                var updated = await _updates.ApplyAsync(row.Check, broad, main);
                var index = _scripts.FindIndex(x => x.Id == updated.Id); _scripts[index] = updated; updatedIds.Add(updated.Id);
            }
            catch (Exception ex) { MessageBox.Show(this, $"{row.Name}: {ex.Message}", "Atualização não aplicada", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        if (updatedIds.Count == 0) { await RefreshAsync(); return; }
        var eligible = new List<GameProfile>();
        string outcome;
        foreach (var profile in _openProfiles)
            if ((await _repository.GetScriptsForProfileAsync(profile.Id)).Any(x => updatedIds.Contains(x.Id) && x.Enabled)) eligible.Add(profile);
        if (eligible.Count > 0)
        {
            var picker = new ProfileReloadWindow(eligible, null, "Aplicar userscripts atualizados", "Selecione somente as contas que podem ser recarregadas agora. Contas não marcadas continuarão executando a versão anterior até a próxima recarga.", "Recarregar selecionadas") { Owner = this };
            if (picker.ShowDialog() == true && picker.SelectedProfiles.Count > 0)
            {
                await _reloadProfiles(picker.SelectedProfiles);
                outcome = $"{updatedIds.Count} script(s) atualizado(s); {picker.SelectedProfiles.Count} conta(s) recarregada(s) com a nova versão.";
            }
            else outcome = $"{updatedIds.Count} script(s) atualizado(s) sem recarregar contas. As sessões atuais continuam na versão anterior até serem recarregadas.";
        }
        else outcome = $"{updatedIds.Count} script(s) atualizado(s). Nenhuma conta aberta utilizava esses scripts.";
        await RefreshAsync();
        StatusText.Text = outcome;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
