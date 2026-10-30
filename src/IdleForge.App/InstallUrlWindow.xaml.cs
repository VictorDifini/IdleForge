using System.Windows;
namespace IdleForge.App;
public partial class InstallUrlWindow : Window
{
    public InstallUrlWindow() => InitializeComponent();
    public Uri ScriptUri => new(UrlBox.Text.Trim(), UriKind.Absolute);
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) { MessageBox.Show(this, "Informe uma URL HTTPS válida."); return; }
        DialogResult = true;
    }
}
