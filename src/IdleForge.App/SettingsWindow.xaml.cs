using IdleForge.App.Updates;
using IdleForge.Core.Updates;
using Microsoft.Win32;
using System.Windows;

namespace IdleForge.App;
public partial class SettingsWindow : Window
{
    private readonly UpdateCoordinator _updates; private readonly Func<Task> _saveState; private IdleForgeSettings _settings; private string? _package;
    public SettingsWindow(UpdateCoordinator updates, Func<Task> saveState) { InitializeComponent(); _updates = updates; _saveState = saveState; _settings = updates.LoadSettings(); RestoreSessionCheck.IsChecked = _settings.RestoreLastSession; RestoreProfilesCheck.IsChecked = _settings.RestoreProfiles; RestoreLayoutCheck.IsChecked = _settings.RestoreLayout; RememberScriptsCheck.IsChecked = _settings.RememberScriptPreferences; UpdateFolderBox.Text = _settings.UpdateFolder ?? ""; VersionLabel.Text = "Versão instalada: " + updates.GetCurrentVersion(); }
    private void ChooseFolder_Click(object sender, RoutedEventArgs e) { var picker = new OpenFolderDialog { Title = "Pasta contendo releases do IdleForge", Multiselect = false }; if (picker.ShowDialog(this) == true) UpdateFolderBox.Text = picker.FolderName; }
    private async void Check_Click(object sender, RoutedEventArgs e) { try { var found = await _updates.FindNewestAsync(UpdateFolderBox.Text); if (found is null) { UpdateStatus.Text = "Nenhum pacote completo e válido encontrado."; return; } _package = found.Value.Directory; UpdateStatus.Text = $"Pacote encontrado: {found.Value.Manifest.Version}\n{_package}"; UpdateButton.IsEnabled = Version.Parse(found.Value.Manifest.Version) > Version.Parse(_updates.GetCurrentVersion()); } catch (Exception ex) { UpdateStatus.Text = ex.Message; } }
    private async void Update_Click(object sender, RoutedEventArgs e) { if (_package is null) return; try { await SaveSettingsAsync(); await _updates.LaunchUpdateAsync(_package, _saveState); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Atualização bloqueada", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private async void Rollback_Click(object sender, RoutedEventArgs e) { var backup = _updates.Backups().FirstOrDefault(); if (backup is null) { MessageBox.Show(this, "Nenhuma versão anterior disponível."); return; } try { await _updates.LaunchUpdateAsync(backup, _saveState); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Rollback bloqueado", MessageBoxButton.OK, MessageBoxImage.Error); } }
    private async Task SaveSettingsAsync() { _settings = new(RestoreSessionCheck.IsChecked == true, RestoreProfilesCheck.IsChecked == true, RestoreLayoutCheck.IsChecked == true, RememberScriptsCheck.IsChecked == true, string.IsNullOrWhiteSpace(UpdateFolderBox.Text) ? null : UpdateFolderBox.Text); await _updates.SaveSettingsAsync(_settings); }
    private async void Save_Click(object sender, RoutedEventArgs e) { await SaveSettingsAsync(); DialogResult = true; }
}
