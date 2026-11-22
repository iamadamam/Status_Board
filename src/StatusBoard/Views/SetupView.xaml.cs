using System.Windows.Controls;
using StatusBoard.ViewModels;

namespace StatusBoard.Views;

public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
        // Each step puts the keyboard where you'd type next.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SetupViewModel old) old.PropertyChanged -= OnChanged;
            if (e.NewValue is SetupViewModel vm) vm.PropertyChanged += OnChanged;
        };
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SetupViewModel.Step) || sender is not SetupViewModel vm) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (vm.IsNameStep) NameBox.Focus();
            else if (vm.IsVendorsStep) SetupSearch.Focus();
            else if (vm.IsDoneStep) FinishButton.Focus();
            else NextButton.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void FocusFirst() => NameBox.Focus();
}
