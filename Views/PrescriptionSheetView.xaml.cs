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

/// <summary>
/// Teclas y foco de la hoja de receta. Desde el 2026-10-14 elegir el medicamento (Enter o clic en las sugerencias, o el
/// catálogo) solo lo elige y el cursor pasa a "Indicación"; Enter ahí (o en "Cantidad") y "Agregar" lo agregan, y el
/// cursor vuelve al buscador. Las teclas del campo "Indicación" (indicaciones frecuentes) son de
/// <see cref="Helpers.IndicationDropdown"/>; qué se elige y qué se agrega, de <see cref="PrescriptionSheetViewModel"/>.
/// </summary>
public partial class PrescriptionSheetView : UserControl
{
    private MedicalRecordsViewModel? _subscribed;

    public PrescriptionSheetView()
    {
        InitializeComponent();
        // Al abrirse, el cursor queda en la búsqueda; también al volver del catálogo
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) FocusSearch();
        };
        Loaded += (_, _) => FocusSearch();
        DataContextChanged += (_, e) => Attach(e.NewValue as MedicalRecordsViewModel);
    }

    private void Attach(MedicalRecordsViewModel? viewModel)
    {
        if (_subscribed != null)
        {
            _subscribed.CatalogBrowser.PropertyChanged -= CatalogBrowser_PropertyChanged;
            _subscribed.Prescription.FocusSearchRequested -= FocusSearch;
        }
        _subscribed = viewModel;
        if (_subscribed != null)
        {
            _subscribed.CatalogBrowser.PropertyChanged += CatalogBrowser_PropertyChanged;
            _subscribed.Prescription.FocusSearchRequested += FocusSearch;
        }
    }

    /// <summary>
    /// Buscador: ↓ abre las sugerencias (con existencias primero) y ↑ ↓ las recorren; Enter elige la marcada, o lo escrito
    /// si la lista está cerrada, y pasa a "Indicación". Esc (lista primero) lo atiende la escalera de Esc de Pacientes.
    /// </summary>
    private void PrescriptionSearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm || Keyboard.Modifiers != ModifierKeys.None) return;
        var prescription = vm.Prescription;

        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                prescription.MovePrescriptionSuggestion(e.Key == Key.Down ? 1 : -1);
                if (prescription.SelectedPrescriptionProduct is { } selected) PrescriptionSuggestionsListBox?.ScrollIntoView(selected);
                e.Handled = true;
                break;
            case Key.Enter:
                if (prescription.ConfirmPrescriptionSearch()) FocusIndication();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Con el foco en la lista de sugerencias, Enter elige la marcada.</summary>
    private void PrescriptionSuggestionsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MedicalRecordsViewModel vm || e.Key != Key.Enter) return;
        if (PrescriptionSuggestionsListBox?.SelectedItem is Product product)
        {
            vm.Prescription.ChoosePrescriptionProduct(product);
            FocusIndication();
            e.Handled = true;
        }
    }

    /// <summary>Clic en una sugerencia: la elige (no la agrega) y pasa a "Indicación".</summary>
    private void PrescriptionSuggestionsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not ListBoxItem && dep != sender)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is ListBoxItem item && item.DataContext is Product product && DataContext is MedicalRecordsViewModel vm)
        {
            vm.Prescription.ChoosePrescriptionProduct(product);
            FocusIndication();
            e.Handled = true;
        }
    }

    /// <summary>Al cerrar el catálogo: con un medicamento elegido, a escribir su indicación; si no, al buscador.</summary>
    private void CatalogBrowser_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CatalogBrowserViewModel.IsCatalogBrowserModalOpen)
            && sender is CatalogBrowserViewModel { IsCatalogBrowserModalOpen: false }
            && IsVisible)
        {
            if (_subscribed?.Prescription.IsMedicineChosen == true) FocusIndication();
            else FocusSearch();
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

    private void FocusIndication()
    {
        if (!IsVisible) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (PrescriptionDosageTextBox == null) return;
            PrescriptionDosageTextBox.Focus();
            PrescriptionDosageTextBox.CaretIndex = PrescriptionDosageTextBox.Text.Length;
        }, DispatcherPriority.Input);
    }
}
