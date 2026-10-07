using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TPVC.Models;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class PosCatalogSheetView : UserControl
{
    public PosCatalogSheetView()
    {
        InitializeComponent();
        // Se reutiliza cada vez que se abre: el foco vuelve al buscador del catálogo
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) FocusSearch();
        };
        Loaded += (_, _) => FocusSearch();
    }

    private void FocusSearch()
    {
        Dispatcher.InvokeAsync(() =>
        {
            CatalogSearchBox.Focus();
            CatalogSearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void ProductsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PosViewModel vm
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(ProductsList, source) is ListBoxItem { DataContext: Product product })
        {
            vm.AddToCart(product);
        }
    }
}
