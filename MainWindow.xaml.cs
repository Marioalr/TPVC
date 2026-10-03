using System.Windows;
using TPVC.ViewModels;

namespace TPVC;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}