using System.Windows.Controls;
using System.Windows.Input;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class PosView : UserControl
{
    public PosView()
    {
        InitializeComponent();
    }

    private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not PosViewModel vm)
            return;

        if (!vm.IsSearchDropdownOpen || vm.SearchSuggestions.Count == 0)
            return;

        if (e.Key == Key.Down)
        {
            vm.MoveSuggestionDown();
            if (vm.SelectedSuggestion != null)
            {
                SuggestionsListBox?.ScrollIntoView(vm.SelectedSuggestion);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            vm.MoveSuggestionUp();
            if (vm.SelectedSuggestion != null)
            {
                SuggestionsListBox?.ScrollIntoView(vm.SelectedSuggestion);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            vm.ConfirmSelectedSuggestion();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CloseSearchDropdown();
            e.Handled = true;
        }
    }
}

