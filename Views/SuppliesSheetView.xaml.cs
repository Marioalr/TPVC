using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TPVC.Models;
using TPVC.ViewModels;
using TPVC.ViewModels.Patients;

namespace TPVC.Views;

public partial class SuppliesSheetView : UserControl
{
    public SuppliesSheetView()
    {
        InitializeComponent();
        // Al abrirse, el cursor queda en la búsqueda; también al volver del catálogo
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) FocusSearch();
        };
        Loaded += (_, _) => FocusSearch();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MedicalRecordsViewModel oldVm) oldVm.CatalogBrowser.PropertyChanged -= ViewModel_PropertyChanged;
            if (e.NewValue is MedicalRecordsViewModel newVm) newVm.CatalogBrowser.PropertyChanged += ViewModel_PropertyChanged;
        };
    }

    private async void SupplySearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Down)
        {
            if (SupplySuggestionsListBox?.Items.Count > 0)
            {
                var cur = SupplySuggestionsListBox.SelectedIndex;
                var nextIndex = cur + 1;
                if (nextIndex >= SupplySuggestionsListBox.Items.Count || nextIndex < 0)
                    nextIndex = 0;
                SupplySuggestionsListBox.SelectedIndex = nextIndex;
                var item = SupplySuggestionsListBox.Items[nextIndex];
                SupplySuggestionsListBox.ScrollIntoView(item);
                if (item is Product p)
                {
                    vm.Supplies.SelectedSupplyProduct = p;
                }
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (SupplySuggestionsListBox?.Items.Count > 0)
            {
                var cur = SupplySuggestionsListBox.SelectedIndex;
                var prevIndex = cur - 1;
                if (prevIndex < 0 || prevIndex >= SupplySuggestionsListBox.Items.Count)
                    prevIndex = SupplySuggestionsListBox.Items.Count - 1;
                SupplySuggestionsListBox.SelectedIndex = prevIndex;
                var item = SupplySuggestionsListBox.Items[prevIndex];
                SupplySuggestionsListBox.ScrollIntoView(item);
                if (item is Product p)
                {
                    vm.Supplies.SelectedSupplyProduct = p;
                }
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var product = SupplySuggestionsListBox?.SelectedItem as Product;
            await vm.Supplies.SelectAndAddSupplyProductAsync(product);
            SupplySearchTextBox?.Focus();
            e.Handled = true;
        }
    }

    private async void SupplyAuxInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            SupplySearchTextBox_PreviewKeyDown(sender, e);
            return;
        }

        if (e.Key == Key.Enter)
        {
            var product = SupplySuggestionsListBox?.SelectedItem as Product;
            await vm.Supplies.SelectAndAddSupplyProductAsync(product);
            SupplySearchTextBox?.Focus();
            e.Handled = true;
        }
    }

    private async void SupplySuggestionsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            if (SupplySuggestionsListBox?.SelectedItem is Product product)
            {
                await vm.Supplies.SelectAndAddSupplyProductAsync(product);
                SupplySearchTextBox?.Focus();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Down || e.Key == Key.Up)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (SupplySuggestionsListBox?.SelectedItem is Product p)
                {
                    vm.Supplies.SelectedSupplyProduct = p;
                }
            }, DispatcherPriority.Input);
        }
    }

    private async void SupplySuggestionsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem && dep != sender)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is ListBoxItem item && item.DataContext is Product product)
        {
            if (DataContext is MedicalRecordsViewModel vm)
            {
                await vm.Supplies.SelectAndAddSupplyProductAsync(product);
                SupplySearchTextBox?.Focus();
                e.Handled = true;
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CatalogBrowserViewModel.IsCatalogBrowserModalOpen)
            && sender is CatalogBrowserViewModel { IsCatalogBrowserModalOpen: false }
            && IsVisible)
        {
            FocusSearch();
        }
    }

    private void FocusSearch()
    {
        if (!IsVisible) return;
        Dispatcher.InvokeAsync(() =>
        {
            SupplySearchTextBox?.Focus();
            SupplySearchTextBox?.SelectAll();
        }, DispatcherPriority.Input);
    }
}
