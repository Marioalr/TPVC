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
    private Window? _hostWindow;
    private MedicalRecordsViewModel? _subscribedViewModel;

    public MedicalRecordsView()
    {
        InitializeComponent();

        // Las suscripciones viven solo mientras la vista está en pantalla. Antes se quitaban en
        // Unloaded con Window.GetWindow(this), que ya devuelve null en ese momento: la ventana y el
        // ViewModel retenían cada instancia vieja de la vista y sus atajos seguían activos.
        Loaded += (s, e) =>
        {
            AttachToWindow(Window.GetWindow(this));
            AttachToViewModel(DataContext as MedicalRecordsViewModel);

            if (DataContext is MedicalRecordsViewModel vm && vm.SelectedPatient == null && !vm.IsSelectPatientModalOpen)
            {
                vm.OpenSelectPatientModal();
            }
        };

        Unloaded += (s, e) =>
        {
            AttachToWindow(null);
            AttachToViewModel(null);
        };

        DataContextChanged += (s, e) =>
        {
            if (IsLoaded) AttachToViewModel(e.NewValue as MedicalRecordsViewModel);
        };
    }

    private void AttachToWindow(Window? window)
    {
        if (_hostWindow != null) _hostWindow.PreviewKeyDown -= Window_PreviewKeyDown;
        _hostWindow = window;
        if (_hostWindow != null) _hostWindow.PreviewKeyDown += Window_PreviewKeyDown;
    }

    private void AttachToViewModel(MedicalRecordsViewModel? viewModel)
    {
        if (_subscribedViewModel != null) _subscribedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _subscribedViewModel = viewModel;
        if (_subscribedViewModel != null) _subscribedViewModel.PropertyChanged += ViewModel_PropertyChanged;
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
        else if (e.PropertyName == nameof(MedicalRecordsViewModel.IsCatalogBrowserModalOpen))
        {
            if (vm.IsCatalogBrowserModalOpen)
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    CatalogBrowserSearchTextBox?.Focus();
                    CatalogBrowserSearchTextBox?.SelectAll();
                }, DispatcherPriority.Input);
            }
            else
            {
                RestoreModalFocus(vm);
            }
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HandleEscapeKey(e);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape)
        {
            HandleEscapeKey(e);
        }
    }

    private void HandleEscapeKey(KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (vm.IsAllergyAlertOpen)
        {
            vm.CancelAllergyPrescription();
            e.Handled = true;
            return;
        }
        if (vm.IsGoogleCalendarTodoModalOpen)
        {
            vm.CloseGoogleCalendarTodo();
            e.Handled = true;
            return;
        }
        if (vm.IsWhatsAppTodoModalOpen)
        {
            vm.CloseWhatsAppTodo();
            e.Handled = true;
            return;
        }
        if (vm.IsEditAppointmentModalOpen)
        {
            vm.CloseEditAppointmentModal();
            e.Handled = true;
            return;
        }
        if (vm.IsCatalogBrowserModalOpen)
        {
            vm.CloseCatalogBrowser();
            e.Handled = true;
            RestoreModalFocus(vm);
            return;
        }
        if (vm.IsCie10DropdownOpen)
        {
            vm.IsCie10DropdownOpen = false;
            e.Handled = true;
            return;
        }
        if (vm.IsAgendaModalOpen)
        {
            vm.CloseAgendaModal();
            e.Handled = true;
            return;
        }
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
        if (vm.IsRecordsViewerModalOpen)
        {
            vm.CloseRecordsViewerModal();
            e.Handled = true;
            return;
        }
        if (vm.IsSelectPatientModalOpen)
        {
            vm.CloseSelectPatientModal();
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

    // ==========================================
    // DIAGNÓSTICO CIE-10
    // ==========================================

    private void DiagnosisTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Down)
        {
            if (ActiveCie10SuggestionsListBox?.Items.Count > 0)
            {
                var cur = ActiveCie10SuggestionsListBox.SelectedIndex;
                var nextIndex = cur + 1;
                if (nextIndex >= ActiveCie10SuggestionsListBox.Items.Count || nextIndex < 0)
                    nextIndex = 0;
                ActiveCie10SuggestionsListBox.SelectedIndex = nextIndex;
                var item = ActiveCie10SuggestionsListBox.Items[nextIndex];
                ActiveCie10SuggestionsListBox.ScrollIntoView(item);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (ActiveCie10SuggestionsListBox?.Items.Count > 0)
            {
                var cur = ActiveCie10SuggestionsListBox.SelectedIndex;
                var prevIndex = cur - 1;
                if (prevIndex < 0 || prevIndex >= ActiveCie10SuggestionsListBox.Items.Count)
                    prevIndex = ActiveCie10SuggestionsListBox.Items.Count - 1;
                ActiveCie10SuggestionsListBox.SelectedIndex = prevIndex;
                var item = ActiveCie10SuggestionsListBox.Items[prevIndex];
                ActiveCie10SuggestionsListBox.ScrollIntoView(item);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (ActiveCie10SuggestionsListBox?.SelectedItem is Cie10Diagnosis item)
            {
                vm.SelectCie10Suggestion(item);
                e.Handled = true;
            }
        }
    }

    private void Cie10SuggestionsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Enter)
        {
            if (ActiveCie10SuggestionsListBox?.SelectedItem is Cie10Diagnosis item)
            {
                vm.SelectCie10Suggestion(item);
                ActiveConsultationDiagnosisTextBox?.Focus();
                e.Handled = true;
            }
        }
    }

    private void Cie10SuggestionsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem && dep != sender)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is ListBoxItem item && item.DataContext is Cie10Diagnosis diag)
        {
            if (DataContext is MedicalRecordsViewModel vm)
            {
                vm.SelectCie10Suggestion(diag);
                ActiveConsultationDiagnosisTextBox?.Focus();
                e.Handled = true;
            }
        }
    }

    private void RestoreModalFocus(MedicalRecordsViewModel vm)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (vm.IsPrescriptionModalOpen)
            {
                PrescriptionSearchTextBox?.Focus();
                PrescriptionSearchTextBox?.SelectAll();
            }
            else if (vm.IsSuppliesModalOpen)
            {
                SupplySearchTextBox?.Focus();
                SupplySearchTextBox?.SelectAll();
            }
            else if (vm.IsServicesModalOpen)
            {
                ServiceModalSearchTextBox?.Focus();
                ServiceModalSearchTextBox?.SelectAll();
            }
            else
            {
                this.Focus();
            }
        }, DispatcherPriority.Input);
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
                var cur = SupplySuggestionsListBox.SelectedIndex;
                var nextIndex = cur + 1;
                if (nextIndex >= SupplySuggestionsListBox.Items.Count || nextIndex < 0)
                    nextIndex = 0;
                SupplySuggestionsListBox.SelectedIndex = nextIndex;
                var item = SupplySuggestionsListBox.Items[nextIndex];
                SupplySuggestionsListBox.ScrollIntoView(item);
                if (item is Product p)
                {
                    vm.SelectedSupplyProduct = p;
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
                    vm.SelectedSupplyProduct = p;
                }
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
        if (DataContext is not MedicalRecordsViewModel vm) return;

        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            SupplySearchTextBox_PreviewKeyDown(sender, e);
            return;
        }

        if (e.Key == Key.Enter)
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
        else if (e.Key == Key.Down || e.Key == Key.Up)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (SupplySuggestionsListBox?.SelectedItem is Product p)
                {
                    vm.SelectedSupplyProduct = p;
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
}
