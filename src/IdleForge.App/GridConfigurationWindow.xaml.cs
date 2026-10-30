using IdleForge.Core;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace IdleForge.App;

public partial class GridConfigurationWindow : Window
{
    private sealed class AccountChoice(GameProfile profile, bool selected) : INotifyPropertyChanged
    {
        private bool _isSelected = selected;
        public Guid Id => profile.Id;
        public string Name => profile.Name;
        public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly AccountChoice[] _choices;
    public IReadOnlyList<Guid> SelectedProfileIds => _choices.Where(x => x.IsSelected).Select(x => x.Id).ToArray();
    public bool AllAccountsSelected => _choices.Length > 0 && _choices.All(x => x.IsSelected);

    public GridConfigurationWindow(IEnumerable<GameProfile> profiles, IEnumerable<Guid> selectedIds)
    {
        InitializeComponent();
        var selected = selectedIds.ToHashSet();
        _choices = profiles.Select(x => new AccountChoice(x, selected.Contains(x.Id))).ToArray();
        AccountsList.ItemsSource = _choices;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var choice in _choices) choice.IsSelected = true; }
    private void Clear_Click(object sender, RoutedEventArgs e) { foreach (var choice in _choices) choice.IsSelected = false; }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_choices.Any(x => x.IsSelected)) { MessageBox.Show(this, "Selecione pelo menos uma conta para a grade.", "Configurar grade", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        DialogResult = true;
    }
}
