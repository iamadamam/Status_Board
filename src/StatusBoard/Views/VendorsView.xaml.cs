using System.Windows;
using System.Windows.Controls;
using StatusBoard.ViewModels;

namespace StatusBoard.Views;

public partial class VendorsView : UserControl
{
    public VendorsView()
    {
        InitializeComponent();
        AddVendorForm.DataContextChanged += (_, _) => TokenBox.Clear(); // a fresh form starts without the last token
    }

    /// <summary>Keyboard focus into the drawer: the search box.</summary>
    public void FocusFirst()
    {
        ListScroller.ScrollToTop();
        VendorSearch.Focus();
    }

    // PasswordBox can't be data-bound.
    private void Token_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: AddVendorViewModel add } box) add.Token = box.Password;
    }
}
