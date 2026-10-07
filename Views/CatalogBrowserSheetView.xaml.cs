using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TPVC.Models;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class CatalogBrowserSheetView : UserControl
{
    public CatalogBrowserSheetView()
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
            if (e.OldValue is MedicalRecordsViewModel oldVm) oldVm.PropertyChanged -= ViewModel_PropertyChanged;
            if (e.NewValue is MedicalRecordsViewModel newVm) newVm.PropertyChanged += ViewModel_PropertyChanged;
        };
    }


    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MedicalRecordsViewModel.IsCatalogBrowserModalOpen)
            && sender is MedicalRecordsViewModel { IsCatalogBrowserModalOpen: false }
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
            CatalogBrowserSearchTextBox?.Focus();
            CatalogBrowserSearchTextBox?.SelectAll();
        }, DispatcherPriority.Input);
    }
}
