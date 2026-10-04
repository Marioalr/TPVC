using System.Windows;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class ProductDialog : Window
{
    public ProductDialog(ProductDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.RequestClose += () =>
        {
            DialogResult = viewModel.DialogResult;
            Close();
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = (DataContext as ProductDialogViewModel)?.DialogResult ?? false;
        Close();
    }
}
