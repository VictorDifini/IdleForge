using IdleForge.Core;
using System.ComponentModel;
using System.Windows;

namespace IdleForge.App;
public partial class ProfileReloadWindow : Window
{
    public sealed class Choice(GameProfile profile, bool selected) : INotifyPropertyChanged { private bool _selected = selected; public GameProfile Profile => profile; public string Name => profile.Name; public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } } public event PropertyChangedEventHandler? PropertyChanged; }
    private readonly Choice[] _choices;
    private readonly Guid? _activeProfile;
    public IReadOnlyList<GameProfile> SelectedProfiles => _choices.Where(x => x.Selected).Select(x => x.Profile).ToArray();
    public ProfileReloadWindow(IEnumerable<GameProfile> profiles, Guid? activeProfile, string? title = null, string? description = null, string? confirmText = null) { InitializeComponent(); _activeProfile = activeProfile; _choices = profiles.Select(x => new Choice(x, x.Id == activeProfile)).ToArray(); ProfilesList.ItemsSource = _choices; if (title is not null) { Title = title; Heading.Text = title; } if (description is not null) Description.Text = description; if (confirmText is not null) ConfirmButton.Content = confirmText; }
    private void Current_Click(object sender, RoutedEventArgs e) { foreach (var choice in _choices) choice.Selected = choice.Profile.Id == _activeProfile; }
    private void All_Click(object sender, RoutedEventArgs e) { foreach (var choice in _choices) choice.Selected = true; }
    private void None_Click(object sender, RoutedEventArgs e) { foreach (var choice in _choices) choice.Selected = false; }
    private void Apply_Click(object sender, RoutedEventArgs e) { if (!_choices.Any(x => x.Selected)) { MessageBox.Show(this, "Marque pelo menos uma conta aberta."); return; } DialogResult = true; }
}
