using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class PosView : UserControl
{
    public PosView()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                SearchTextBox?.Focus();
                SearchTextBox?.SelectAll();
            }, DispatcherPriority.Input);
        };
    }

    private void PosView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not PosViewModel vm) return;

        // F1: Enfocar barra de búsqueda rápida
        if (e.Key == Key.F1)
        {
            SearchTextBox?.Focus();
            SearchTextBox?.SelectAll();
            e.Handled = true;
            return;
        }

        // F2: Abrir o alternar Catálogo Completo
        if (e.Key == Key.F2)
        {
            if (vm.IsCatalogModalOpen)
            {
                vm.CloseCatalogModal();
            }
            else
            {
                vm.OpenCatalogModal();
                Dispatcher.InvokeAsync(() =>
                {
                    CatalogSearchTextBox?.Focus();
                    CatalogSearchTextBox?.SelectAll();
                }, DispatcherPriority.Input);
            }
            e.Handled = true;
            return;
        }

        // F5 o Ctrl+Enter: Cobrar venta si hay artículos
        if (e.Key == Key.F5 || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
        {
            if (vm.HasCartItems && !vm.IsTicketModalOpen)
            {
                _ = vm.ProcessSaleAsync();
                e.Handled = true;
                return;
            }
        }

        // Escape: Cerrar modales en cascada
        if (e.Key == Key.Escape)
        {
            if (vm.IsTicketModalOpen)
            {
                vm.CloseTicketModal();
                SearchTextBox?.Focus();
                e.Handled = true;
                return;
            }

            if (vm.IsCatalogModalOpen)
            {
                vm.CloseCatalogModal();
                SearchTextBox?.Focus();
                e.Handled = true;
                return;
            }

            if (vm.IsServicesModalOpen)
            {
                vm.CloseServicesModal();
                SearchTextBox?.Focus();
                e.Handled = true;
                return;
            }

            if (vm.IsCreateServiceModalOpen)
            {
                vm.CloseCreateServiceModal();
                e.Handled = true;
                return;
            }

            if (vm.IsSelectPatientModalOpen)
            {
                vm.CloseSelectPatientModal();
                e.Handled = true;
                return;
            }

            if (vm.IsSearchDropdownOpen)
            {
                vm.CloseSearchDropdown();
                e.Handled = true;
                return;
            }
        }
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

