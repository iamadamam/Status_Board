using System.Windows.Controls;

namespace StatusBoard.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Keyboard focus into the drawer: the first setting.</summary>
    public void FocusFirst() => RefreshBox.Focus();
}
