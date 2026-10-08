using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TPVC.Models;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class ServicesCatalogView : UserControl
{
    public ServicesCatalogView()
    {
        InitializeComponent();
    }

    /// <summary>Doble clic en una fila: editar el servicio en el panel lateral.</summary>
    private void ServicesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ServicesCatalogViewModel vm
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(ServicesList, source) is ListBoxItem { DataContext: ClinicalService service })
        {
            _ = vm.EditServiceAsync(service);
        }
    }
}
