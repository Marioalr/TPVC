using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TPVC.ViewModels;

namespace TPVC.Views;

/// <summary>
/// Contenedor de Pacientes. El comportamiento de cada pieza vive en su propio archivo; aquí solo
/// quedan Esc por capas (cierra lo de más arriba) y "escribir para buscar" en el inicio y la ficha.
/// </summary>
public partial class MedicalRecordsView : UserControl
{
    private Window? _hostWindow;
    private MedicalRecordsViewModel? _subscribedViewModel;

    public MedicalRecordsView()
    {
        InitializeComponent();

        // Las suscripciones viven solo mientras la vista está en pantalla (Window.GetWindow ya es
        // null en Unloaded: se guarda la ventana para poder soltarla)
        Loaded += (_, _) =>
        {
            AttachToWindow(Window.GetWindow(this));
            AttachToViewModel(DataContext as MedicalRecordsViewModel);
            if (DataContext is MedicalRecordsViewModel { IsHomePage: true }) FocusSearchBox();
        };
        Unloaded += (_, _) =>
        {
            AttachToWindow(null);
            AttachToViewModel(null);
        };
        DataContextChanged += (_, e) =>
        {
            if (IsLoaded) AttachToViewModel(e.NewValue as MedicalRecordsViewModel);
        };
    }

    private void AttachToWindow(Window? window)
    {
        if (_hostWindow != null)
        {
            _hostWindow.PreviewKeyDown -= Window_PreviewKeyDown;
            _hostWindow.PreviewTextInput -= Window_PreviewTextInput;
        }
        _hostWindow = window;
        if (_hostWindow != null)
        {
            _hostWindow.PreviewKeyDown += Window_PreviewKeyDown;
            _hostWindow.PreviewTextInput += Window_PreviewTextInput;
        }
    }

    private void AttachToViewModel(MedicalRecordsViewModel? viewModel)
    {
        if (_subscribedViewModel != null) _subscribedViewModel.FocusSearchRequested -= FocusSearchBox;
        _subscribedViewModel = viewModel;
        if (_subscribedViewModel != null) _subscribedViewModel.FocusSearchRequested += FocusSearchBox;
    }

    private void FocusSearchBox()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (PatientSearchBox.IsVisible)
            {
                PatientSearchBox.Focus();
                PatientSearchBox.CaretIndex = PatientSearchBox.Text.Length;
            }
        }, DispatcherPriority.Input);
    }

    private static bool IsAnySheetOpen(MedicalRecordsViewModel vm) =>
        vm.IsInfoModalVisible || vm.IsAllergyAlertOpen || vm.IsCreateServiceModalOpen || vm.IsCatalogBrowserModalOpen
        || vm.IsPrescriptionPdfModalOpen || vm.IsPosModalOpen || vm.IsPrescriptionModalOpen || vm.IsSuppliesModalOpen
        || vm.IsServicesModalOpen;

    /// <summary>En el inicio o la ficha, teclear con el foco fuera de un campo escribe en el buscador.</summary>
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!IsVisible || DataContext is not MedicalRecordsViewModel vm) return;
        if (!(vm.IsHomePage || vm.IsChartPage) || vm.IsAgendaPanelOpen || IsAnySheetOpen(vm)) return;
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox) return;

        vm.HomeSearchText += e.Text;
        FocusSearchBox();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsVisible) HandleEscapeKey(e);
    }

    private void HandleEscapeKey(KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm) return;
        e.Handled = true;

        if (vm.IsInfoModalVisible) vm.DismissInfoModal();
        else if (vm.IsAllergyAlertOpen) vm.CancelAllergyPrescription();
        else if (vm.IsCreateServiceModalOpen) vm.CloseCreateServiceModal();
        else if (vm.IsCatalogBrowserModalOpen) vm.CloseCatalogBrowser();
        else if (vm.IsPrescriptionPdfModalOpen) vm.ClosePrescriptionPdfPreview();
        else if (vm.IsCie10DropdownOpen) vm.IsCie10DropdownOpen = false;
        // Esc en "Terminar consulta" = volver a la consulta (no cobra ni la cierra)
        else if (vm.IsPosModalOpen) vm.ClosePosModal();
        else if (vm.IsPrescriptionModalOpen) vm.ClosePrescriptionModal();
        else if (vm.IsSuppliesModalOpen) vm.CloseSuppliesModal();
        else if (vm.IsServicesModalOpen) vm.CloseServicesModal();
        else if (vm.IsMovingAppointment) vm.CancelMovingAppointment();
        else if (vm.IsAgendaPanelOpen) vm.CloseAgendaPanel();
        else if (vm.IsHomePage && vm.HasHomeQuery) vm.HomeSearchText = string.Empty;
        else if (vm.IsChartPage) vm.GoToPatientsHome();
        else e.Handled = false;
    }
}
