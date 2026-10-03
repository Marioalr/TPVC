using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class InventoryView : UserControl
{
    public InventoryView()
    {
        InitializeComponent();
    }

    private async void NewProduct_Click(object sender, RoutedEventArgs e)
    {
        var inventoryService = ((App)Application.Current).Services.GetRequiredService<InventoryService>();
        var dialogVm = new ProductDialogViewModel(inventoryService);
        var dialog = new ProductDialog(dialogVm)
        {
            Owner = Window.GetWindow(this)
        };
        await dialogVm.InitializeAsync(null);

        dialog.ShowDialog();

        if (dialogVm.DialogResult && DataContext is InventoryViewModel vm)
        {
            await vm.FilterProductsAsync();
        }
    }

    private async void EditProduct_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.DataContext is Product product)
        {
            var inventoryService = ((App)Application.Current).Services.GetRequiredService<InventoryService>();
            var dialogVm = new ProductDialogViewModel(inventoryService);
            var dialog = new ProductDialog(dialogVm)
            {
                Owner = Window.GetWindow(this)
            };
            await dialogVm.InitializeAsync(product);

            dialog.ShowDialog();

            if (dialogVm.DialogResult && DataContext is InventoryViewModel vm)
            {
                await vm.FilterProductsAsync();
            }
        }
    }
}
