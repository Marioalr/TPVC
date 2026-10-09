using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TPVC.ViewModels;

namespace TPVC.Views;

/// <summary>
/// Contenedor de Pacientes. El comportamiento de cada pieza vive en su propio archivo; aquí solo
/// quedan las teclas de Esc por capas (cierra lo de más arriba) y de "escribir para buscar" en el
/// inicio y la ficha, y el foco del buscador. Qué cierra Esc y cuándo se puede buscar lo decide el
/// ViewModel (TryCloseTopmost, CanTypeToSearch).
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

    /// <summary>En el inicio o la ficha, teclear con el foco fuera de un campo escribe en el buscador.</summary>
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!IsVisible || DataContext is not MedicalRecordsViewModel vm) return;
        if (!vm.CanTypeToSearch) return;
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox) return;

        vm.AppendTypedSearchText(e.Text);
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
        // Marcada antes de cerrar, como siempre: si cerrar una capa falla, Esc no sigue a otros controles
        e.Handled = true;
        e.Handled = vm.TryCloseTopmost();
    }
}
