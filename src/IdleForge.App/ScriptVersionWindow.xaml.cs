using IdleForge.Core;
using System.Windows;

namespace IdleForge.App;
public partial class ScriptVersionWindow : Window
{
    public sealed record Row(UserScript Script, UserScriptVersion Version) { public string ScriptName => Script.Metadata.Name; }
    public Row? Selected => VersionsGrid.SelectedItem as Row;
    public ScriptVersionWindow(IEnumerable<Row> rows) { InitializeComponent(); VersionsGrid.ItemsSource = rows; }
    private void Restore_Click(object sender, RoutedEventArgs e) { if (Selected is null) { MessageBox.Show(this, "Selecione uma versão."); return; } DialogResult = true; }
}
