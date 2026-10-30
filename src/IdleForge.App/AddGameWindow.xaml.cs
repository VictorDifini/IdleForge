using System.Windows;

namespace IdleForge.App;

public partial class AddGameWindow : Window
{
    public string GameName => NameBox.Text.Trim();
    public string ProfileName => ProfileBox.Text.Trim();
    public Uri GameUri { get; private set; } = new("https://example.com");
    public AddGameWindow() => InitializeComponent();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(GameName) || string.IsNullOrWhiteSpace(ProfileName) || !Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        { MessageBox.Show(this, "Informe nome, perfil e uma URL HTTP/HTTPS válida.", "Dados inválidos", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        GameUri = uri; DialogResult = true;
    }
}
