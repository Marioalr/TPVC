using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TPVC.ViewModels;

namespace TPVC.Views;

/// <summary>
/// Punto de venta. Teclado: F1 buscador, F2 catálogo, F3 servicios, F5 o Ctrl+Enter cobrar,
/// F6 corte de caja y Esc cierra lo de más arriba. En el buscador: ↑↓ eligen sugerencia y Enter
/// la agrega; con el buscador vacío, ↑↓ eligen el renglón de la cuenta, + y − cambian la
/// cantidad y Supr lo quita.
/// </summary>
public partial class PosView : UserControl
{
    private Window? _hostWindow;
    private PosViewModel? _subscribedViewModel;

    public PosView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AttachToWindow(Window.GetWindow(this));
            AttachToViewModel(DataContext as PosViewModel);
            FocusSearchBox(selectAll: true);
        };
        Unloaded += (_, _) =>
        {
            AttachToWindow(null);
            AttachToViewModel(null);
        };
        DataContextChanged += (_, e) =>
        {
            if (IsLoaded) AttachToViewModel(e.NewValue as PosViewModel);
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) FocusSearchBox(selectAll: true);
        };
        SearchTextBox.PreviewTextInput += SearchTextBox_PreviewTextInput;
    }

    private void AttachToWindow(Window? window)
    {
        if (_hostWindow != null) _hostWindow.PreviewKeyDown -= Window_PreviewKeyDown;
        _hostWindow = window;
        if (_hostWindow != null) _hostWindow.PreviewKeyDown += Window_PreviewKeyDown;
    }

    private void AttachToViewModel(PosViewModel? viewModel)
    {
        if (_subscribedViewModel != null) _subscribedViewModel.FocusSearchRequested -= OnFocusSearchRequested;
        _subscribedViewModel = viewModel;
        if (_subscribedViewModel != null) _subscribedViewModel.FocusSearchRequested += OnFocusSearchRequested;
    }

    private void OnFocusSearchRequested() => FocusSearchBox(selectAll: false);

    private void FocusSearchBox(bool selectAll)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!SearchTextBox.IsVisible || IsAnySheetOpen()) return;
            SearchTextBox.Focus();
            if (selectAll) SearchTextBox.SelectAll();
            else SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
        }, DispatcherPriority.Input);
    }

    private bool IsAnySheetOpen() => DataContext is PosViewModel vm
        && (vm.IsCatalogModalOpen || vm.IsServicesModalOpen || vm.IsCreateServiceModalOpen || vm.IsTicketModalOpen
            || vm.IsCashCutModalOpen || vm.IsTicketHistoryModalOpen);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible || DataContext is not PosViewModel vm) return;
        bool sheetOpen = IsAnySheetOpen();

        switch (e.Key)
        {
            case Key.F1:
                if (!sheetOpen) FocusSearchBox(selectAll: true);
                e.Handled = true;
                break;
            case Key.F2:
                if (vm.IsCatalogModalOpen) vm.CloseCatalogModal();
                else if (!sheetOpen) vm.OpenCatalogModal();
                e.Handled = true;
                break;
            case Key.F3:
                if (vm.IsServicesModalOpen) vm.CloseServicesModal();
                else if (!sheetOpen) _ = vm.OpenServicesModalAsync();
                e.Handled = true;
                break;
            case Key.F5:
                if (!sheetOpen && vm.Checkout.CanCharge) _ = vm.ProcessSaleAsync();
                e.Handled = true;
                break;
            case Key.Enter when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                if (!sheetOpen && vm.Checkout.CanCharge) _ = vm.ProcessSaleAsync();
                e.Handled = true;
                break;
            case Key.Enter when vm.IsTicketModalOpen && !vm.IsTicketHistoryModalOpen:
                // Ticket de una venta recién cobrada: Enter pasa a la siguiente venta
                vm.CloseTicketModal();
                e.Handled = true;
                break;
            case Key.F6:
                if (vm.IsCashCutModalOpen) vm.CloseCashCutModal();
                else if (!sheetOpen) _ = vm.OpenCashCutModalAsync();
                e.Handled = true;
                break;
            case Key.Escape:
                e.Handled = HandleEscape(vm);
                break;
        }
    }

    /// <summary>Esc cierra lo de más arriba; en el buscador, primero borra lo escrito.</summary>
    private bool HandleEscape(PosViewModel vm)
    {
        if (vm.IsCancelConfirmModalOpen) vm.CloseCancelConfirmModal();
        else if (vm.IsTicketModalOpen) vm.CloseTicketModal();
        else if (vm.IsCreateServiceModalOpen) vm.CloseCreateServiceModal();
        else if (vm.IsCatalogModalOpen) vm.CloseCatalogModal();
        else if (vm.IsServicesModalOpen) vm.CloseServicesModal();
        else if (vm.IsTicketHistoryModalOpen) vm.CloseTicketHistoryModal();
        else if (vm.IsCashCutModalOpen) vm.CloseCashCutModal();
        else if (vm.HasSearchText) vm.ClearSearch();
        else return false;
        return true;
    }

    private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not PosViewModel vm) return;
        bool searching = vm.HasSearchText;

        switch (e.Key)
        {
            case Key.Down when searching:
                vm.MoveSuggestionDown();
                ScrollSuggestionIntoView(vm);
                e.Handled = true;
                break;
            case Key.Up when searching:
                vm.MoveSuggestionUp();
                ScrollSuggestionIntoView(vm);
                e.Handled = true;
                break;
            case Key.Down:
                vm.Checkout.SelectNextItem(1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.Checkout.SelectNextItem(-1);
                e.Handled = true;
                break;
            case Key.Enter when searching && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                vm.ConfirmSelectedSuggestion();
                e.Handled = true;
                break;
            case Key.Delete when !searching && vm.Checkout.SelectedItem != null:
                vm.RemoveFromCart(vm.Checkout.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Con el buscador vacío, "+" y "−" cambian la cantidad del renglón elegido (en cualquier distribución de teclado).</summary>
    private void SearchTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (DataContext is not PosViewModel vm || vm.HasSearchText || vm.Checkout.SelectedItem == null) return;
        if (e.Text == "+")
        {
            vm.IncreaseQuantity(vm.Checkout.SelectedItem);
            e.Handled = true;
        }
        else if (e.Text == "-")
        {
            vm.DecreaseQuantity(vm.Checkout.SelectedItem);
            e.Handled = true;
        }
    }

    private void ScrollSuggestionIntoView(PosViewModel vm)
    {
        if (vm.SelectedSuggestion != null) SuggestionsListBox.ScrollIntoView(vm.SelectedSuggestion);
    }

    /// <summary>Un clic en una sugerencia la agrega (con la cantidad escrita, si hay).</summary>
    private void SuggestionsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PosViewModel vm) return;
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(SuggestionsListBox, source) is ListBoxItem { DataContext: Models.Product product })
        {
            vm.SelectSuggestion(product);
            e.Handled = true;
        }
    }
}
