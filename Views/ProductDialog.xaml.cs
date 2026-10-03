using System.Windows;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class ProductDialog : Window
{
    public ProductDialog(ProductDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
