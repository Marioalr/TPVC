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
            if (e.OldValue is MedicalRecordsViewModel oldVm) oldVm.CatalogBrowser.PropertyChanged -= ViewModel_PropertyChanged;
            if (e.NewValue is MedicalRecordsViewModel newVm) newVm.CatalogBrowser.PropertyChanged += ViewModel_PropertyChanged;
        };
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
            CatalogBrowserSearchTextBox?.Focus();
            CatalogBrowserSearchTextBox?.SelectAll();
        }, DispatcherPriority.Input);
    }
}
