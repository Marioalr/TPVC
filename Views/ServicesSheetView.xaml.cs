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

public partial class ServicesSheetView : UserControl
{
    public ServicesSheetView()
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

    private async void ServiceModalSearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Down)
        {
            if (ServiceSuggestionsListBox?.Items.Count > 0)
            {
                var cur = ServiceSuggestionsListBox.SelectedIndex;
                var nextIndex = cur + 1;
                if (nextIndex >= ServiceSuggestionsListBox.Items.Count || nextIndex < 0)
                    nextIndex = 0;
                ServiceSuggestionsListBox.SelectedIndex = nextIndex;
                var item = ServiceSuggestionsListBox.Items[nextIndex];
                ServiceSuggestionsListBox.ScrollIntoView(item);
                if (item is ClinicalService s)
                {
                    vm.SelectedServiceModalItem = s;
                }
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (ServiceSuggestionsListBox?.Items.Count > 0)
            {
                var cur = ServiceSuggestionsListBox.SelectedIndex;
                var prevIndex = cur - 1;
                if (prevIndex < 0 || prevIndex >= ServiceSuggestionsListBox.Items.Count)
                    prevIndex = ServiceSuggestionsListBox.Items.Count - 1;
                ServiceSuggestionsListBox.SelectedIndex = prevIndex;
                var item = ServiceSuggestionsListBox.Items[prevIndex];
                ServiceSuggestionsListBox.ScrollIntoView(item);
                if (item is ClinicalService s)
                {
                    vm.SelectedServiceModalItem = s;
                }
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var service = ServiceSuggestionsListBox?.SelectedItem as ClinicalService;
            await vm.SelectAndAddServiceModalSuggestionAsync(service);
            ServiceModalSearchTextBox?.Focus();
            e.Handled = true;
        }
    }

    private async void ServiceModalAuxInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            ServiceModalSearchTextBox_PreviewKeyDown(sender, e);
            return;
        }

        if (e.Key == Key.Enter)
        {
            var service = ServiceSuggestionsListBox?.SelectedItem as ClinicalService;
            await vm.SelectAndAddServiceModalSuggestionAsync(service);
            ServiceModalSearchTextBox?.Focus();
            e.Handled = true;
        }
    }

    private async void ServiceSuggestionsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            if (ServiceSuggestionsListBox?.SelectedItem is ClinicalService service)
            {
                await vm.SelectAndAddServiceModalSuggestionAsync(service);
                ServiceModalSearchTextBox?.Focus();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Down || e.Key == Key.Up)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (ServiceSuggestionsListBox?.SelectedItem is ClinicalService s)
                {
                    vm.SelectedServiceModalItem = s;
                }
            }, DispatcherPriority.Input);
        }
    }

    private async void ServiceSuggestionsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem && dep != sender)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is ListBoxItem item && item.DataContext is ClinicalService service)
        {
            if (DataContext is MedicalRecordsViewModel vm)
            {
                await vm.SelectAndAddServiceModalSuggestionAsync(service);
                ServiceModalSearchTextBox?.Focus();
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
            ServiceModalSearchTextBox?.Focus();
            ServiceModalSearchTextBox?.SelectAll();
        }, DispatcherPriority.Input);
    }
}
