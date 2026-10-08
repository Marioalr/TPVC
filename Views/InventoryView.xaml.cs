using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TPVC.Models;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class InventoryView : UserControl
{
    public InventoryView()
    {
        InitializeComponent();
    }

    /// <summary>Doble clic en una fila: editar el producto en el panel lateral.</summary>
    private void ProductsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is InventoryViewModel vm
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(ProductsList, source) is ListBoxItem { DataContext: Product product })
        {
            _ = vm.EditProductAsync(product);
        }
    }
}
