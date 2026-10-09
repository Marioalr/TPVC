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

public partial class PrescriptionSheetView : UserControl
{
    public PrescriptionSheetView()
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

    private async void PrescriptionSearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Down)
        {
            if (PrescriptionSuggestionsListBox?.Items.Count > 0)
            {
                var cur = PrescriptionSuggestionsListBox.SelectedIndex;
                var nextIndex = cur + 1;
                if (nextIndex >= PrescriptionSuggestionsListBox.Items.Count || nextIndex < 0)
                    nextIndex = 0;
                PrescriptionSuggestionsListBox.SelectedIndex = nextIndex;
                var item = PrescriptionSuggestionsListBox.Items[nextIndex];
                PrescriptionSuggestionsListBox.ScrollIntoView(item);
                if (item is Product p)
                {
                    vm.SelectedPrescriptionProduct = p;
                }
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (PrescriptionSuggestionsListBox?.Items.Count > 0)
            {
                var cur = PrescriptionSuggestionsListBox.SelectedIndex;
                var prevIndex = cur - 1;
                if (prevIndex < 0 || prevIndex >= PrescriptionSuggestionsListBox.Items.Count)
                    prevIndex = PrescriptionSuggestionsListBox.Items.Count - 1;
                PrescriptionSuggestionsListBox.SelectedIndex = prevIndex;
                var item = PrescriptionSuggestionsListBox.Items[prevIndex];
                PrescriptionSuggestionsListBox.ScrollIntoView(item);
                if (item is Product p)
                {
                    vm.SelectedPrescriptionProduct = p;
                }
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var product = PrescriptionSuggestionsListBox?.SelectedItem as Product;
            await vm.SelectAndAddPrescriptionProductAsync(product);
            PrescriptionSearchTextBox?.Focus();
            e.Handled = true;
        }
    }

    private async void PrescriptionAuxInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            PrescriptionSearchTextBox_PreviewKeyDown(sender, e);
            return;
        }

        if (e.Key == Key.Enter)
        {
            var product = PrescriptionSuggestionsListBox?.SelectedItem as Product;
            await vm.SelectAndAddPrescriptionProductAsync(product);
            PrescriptionSearchTextBox?.Focus();
            e.Handled = true;
        }
    }

    private async void PrescriptionSuggestionsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            if (PrescriptionSuggestionsListBox?.SelectedItem is Product product)
            {
                await vm.SelectAndAddPrescriptionProductAsync(product);
                PrescriptionSearchTextBox?.Focus();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Down || e.Key == Key.Up)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (PrescriptionSuggestionsListBox?.SelectedItem is Product p)
                {
                    vm.SelectedPrescriptionProduct = p;
                }
            }, DispatcherPriority.Input);
        }
    }

    private async void PrescriptionSuggestionsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
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
                await vm.SelectAndAddPrescriptionProductAsync(product);
                PrescriptionSearchTextBox?.Focus();
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
            PrescriptionSearchTextBox?.Focus();
            PrescriptionSearchTextBox?.SelectAll();
        }, DispatcherPriority.Input);
    }
}
