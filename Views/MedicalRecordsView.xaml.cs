using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TPVC.Models;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class MedicalRecordsView : UserControl
{
    public MedicalRecordsView()
    {
        InitializeComponent();

        Loaded += (s, e) =>
        {
            if (DataContext is MedicalRecordsViewModel vm && vm.SelectedPatient == null && !vm.IsSelectPatientModalOpen)
            {
                vm.OpenSelectPatientModal();
            }
        };

        DataContextChanged += (s, e) =>
        {
            if (e.OldValue is MedicalRecordsViewModel oldVm)
            {
                oldVm.PropertyChanged -= ViewModel_PropertyChanged;
            }
            if (e.NewValue is MedicalRecordsViewModel newVm)
            {
                newVm.PropertyChanged += ViewModel_PropertyChanged;
            }
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MedicalRecordsViewModel vm) return;

        if (e.PropertyName == nameof(MedicalRecordsViewModel.IsSelectPatientModalOpen) && vm.IsSelectPatientModalOpen)
        {
            Dispatcher.InvokeAsync(() =>
            {
                SelectPatientSearchTextBox?.Focus();
                SelectPatientSearchTextBox?.SelectAll();
            }, DispatcherPriority.Input);
        }
        else if (e.PropertyName == nameof(MedicalRecordsViewModel.IsPrescriptionModalOpen) && vm.IsPrescriptionModalOpen)
        {
            Dispatcher.InvokeAsync(() =>
            {
                PrescriptionSearchTextBox?.Focus();
                PrescriptionSearchTextBox?.SelectAll();
            }, DispatcherPriority.Input);
        }
        else if (e.PropertyName == nameof(MedicalRecordsViewModel.IsSuppliesModalOpen) && vm.IsSuppliesModalOpen)
        {
            Dispatcher.InvokeAsync(() =>
            {
                SupplySearchTextBox?.Focus();
                SupplySearchTextBox?.SelectAll();
            }, DispatcherPriority.Input);
        }
        else if (e.PropertyName == nameof(MedicalRecordsViewModel.IsServicesModalOpen) && vm.IsServicesModalOpen)
        {
            Dispatcher.InvokeAsync(() =>
            {
                ServiceModalSearchTextBox?.Focus();
                ServiceModalSearchTextBox?.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape)
        {
            if (DataContext is MedicalRecordsViewModel vm)
            {
                if (vm.IsPrescriptionModalOpen)
                {
                    vm.ClosePrescriptionModal();
                    e.Handled = true;
                    return;
                }
                if (vm.IsSuppliesModalOpen)
                {
                    vm.CloseSuppliesModal();
                    e.Handled = true;
                    return;
                }
                if (vm.IsServicesModalOpen)
                {
                    vm.CloseServicesModal();
                    e.Handled = true;
                    return;
                }
                if (vm.IsSelectPatientModalOpen)
                {
                    vm.CloseSelectPatientModal();
                    e.Handled = true;
                    return;
                }
                if (vm.IsRecordsViewerModalOpen)
                {
                    vm.CloseRecordsViewerModal();
                    e.Handled = true;
                    return;
                }
                if (vm.IsInfoModalVisible)
                {
                    vm.DismissInfoModal();
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    // ==========================================
    // MODAL DE RECETA MÉDICA
    // ==========================================

    private async void PrescriptionSearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Down)
        {
            if (PrescriptionSuggestionsListBox?.Items.Count > 0)
            {
                var nextIndex = PrescriptionSuggestionsListBox.SelectedIndex + 1;
                if (nextIndex >= PrescriptionSuggestionsListBox.Items.Count)
                    nextIndex = 0;
                PrescriptionSuggestionsListBox.SelectedIndex = nextIndex;
                PrescriptionSuggestionsListBox.ScrollIntoView(PrescriptionSuggestionsListBox.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (PrescriptionSuggestionsListBox?.Items.Count > 0)
            {
                var prevIndex = PrescriptionSuggestionsListBox.SelectedIndex - 1;
                if (prevIndex < 0)
                    prevIndex = PrescriptionSuggestionsListBox.Items.Count - 1;
                PrescriptionSuggestionsListBox.SelectedIndex = prevIndex;
                PrescriptionSuggestionsListBox.ScrollIntoView(PrescriptionSuggestionsListBox.SelectedItem);
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
        if (e.Key == Key.Enter && DataContext is MedicalRecordsViewModel vm)
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

    // ==========================================
    // MODAL DE INSUMOS
    // ==========================================

    private async void SupplySearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Down)
        {
            if (SupplySuggestionsListBox?.Items.Count > 0)
            {
                var nextIndex = SupplySuggestionsListBox.SelectedIndex + 1;
                if (nextIndex >= SupplySuggestionsListBox.Items.Count)
                    nextIndex = 0;
                SupplySuggestionsListBox.SelectedIndex = nextIndex;
                SupplySuggestionsListBox.ScrollIntoView(SupplySuggestionsListBox.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (SupplySuggestionsListBox?.Items.Count > 0)
            {
                var prevIndex = SupplySuggestionsListBox.SelectedIndex - 1;
                if (prevIndex < 0)
                    prevIndex = SupplySuggestionsListBox.Items.Count - 1;
                SupplySuggestionsListBox.SelectedIndex = prevIndex;
                SupplySuggestionsListBox.ScrollIntoView(SupplySuggestionsListBox.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var product = SupplySuggestionsListBox?.SelectedItem as Product;
            await vm.SelectAndAddSupplyProductAsync(product);
            SupplySearchTextBox?.Focus();
            e.Handled = true;
        }
    }

    private async void SupplyAuxInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MedicalRecordsViewModel vm)
        {
            var product = SupplySuggestionsListBox?.SelectedItem as Product;
            await vm.SelectAndAddSupplyProductAsync(product);
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
                await vm.SelectAndAddSupplyProductAsync(product);
                SupplySearchTextBox?.Focus();
                e.Handled = true;
            }
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
                await vm.SelectAndAddSupplyProductAsync(product);
                SupplySearchTextBox?.Focus();
                e.Handled = true;
            }
        }
    }

    // ==========================================
    // MODAL DE SERVICIOS
    // ==========================================

    private async void ServiceModalSearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Down)
        {
            if (ServiceSuggestionsListBox?.Items.Count > 0)
            {
                var nextIndex = ServiceSuggestionsListBox.SelectedIndex + 1;
                if (nextIndex >= ServiceSuggestionsListBox.Items.Count)
                    nextIndex = 0;
                ServiceSuggestionsListBox.SelectedIndex = nextIndex;
                ServiceSuggestionsListBox.ScrollIntoView(ServiceSuggestionsListBox.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (ServiceSuggestionsListBox?.Items.Count > 0)
            {
                var prevIndex = ServiceSuggestionsListBox.SelectedIndex - 1;
                if (prevIndex < 0)
                    prevIndex = ServiceSuggestionsListBox.Items.Count - 1;
                ServiceSuggestionsListBox.SelectedIndex = prevIndex;
                ServiceSuggestionsListBox.ScrollIntoView(ServiceSuggestionsListBox.SelectedItem);
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
        if (e.Key == Key.Enter && DataContext is MedicalRecordsViewModel vm)
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
}
