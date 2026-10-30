using IdleForge.Core;
using System.Windows;
using System.Windows.Media;

namespace IdleForge.App;

public partial class ScriptPreviewWindow : Window
{
    private readonly bool _requiresBroadConnect;
    private readonly bool _requiresMainWorld;
    public bool BroadConnectApproved => BroadConnectCheck.IsChecked == true;
    public bool MainWorldCompatibilityApproved => MainWorldCheck.IsChecked == true;
    public ScriptPreviewWindow(UserScriptMetadata metadata, ScriptRisk risk, string source)
    {
        InitializeComponent(); TitleText.Text = $"{metadata.Name}  ·  {metadata.Version}"; SourceBox.Text = source;
        PermissionsText.Text = "Permissões: " + (metadata.Grants.Count == 0 ? "nenhuma (@grant none)" : string.Join(", ", metadata.Grants)) + "\n@connect: " + (metadata.Connect.Count == 0 ? "nenhum" : string.Join(", ", metadata.Connect)) + "\n@require: " + metadata.Requires.Count + "\n@sandbox: " + (metadata.Sandbox ?? "isolado");
        _requiresBroadConnect = metadata.Connect.Contains("*"); BroadConnectCheck.Visibility = _requiresBroadConnect ? Visibility.Visible : Visibility.Collapsed;
        _requiresMainWorld = string.Equals(metadata.Sandbox, "raw", StringComparison.OrdinalIgnoreCase); MainWorldCheck.Visibility = _requiresMainWorld ? Visibility.Visible : Visibility.Collapsed;
        RiskText.Text = $"Risco {risk.Level}: {string.Join("  ", risk.Findings)}";
        RiskBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(risk.Level >= RiskLevel.High ? "#8C3D3D" : risk.Level == RiskLevel.Medium ? "#806522" : "#176B54"));
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_requiresBroadConnect && BroadConnectCheck.IsChecked != true) { MessageBox.Show(this, "A instalação exige confirmação específica para @connect *.", "Permissão ampla", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (_requiresMainWorld && MainWorldCheck.IsChecked != true) { MessageBox.Show(this, "@sandbox raw exige confirmação específica para execução no mundo principal.", "Compatibilidade com a página", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }
}
