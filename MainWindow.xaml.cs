using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TPVC.ViewModels;

namespace TPVC;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsCommandPaletteOpen))
        {
            if (_viewModel.IsCommandPaletteOpen)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    PaletteSearchTextBox.Focus();
                    PaletteSearchTextBox.SelectAll();
                }, DispatcherPriority.Input);
            }
        }
    }

    private void PaletteSearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            _viewModel.MovePaletteDownCommand.Execute(null);
            if (_viewModel.SelectedPaletteItem != null)
            {
                PaletteResultsListBox.ScrollIntoView(_viewModel.SelectedPaletteItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            _viewModel.MovePaletteUpCommand.Execute(null);
            if (_viewModel.SelectedPaletteItem != null)
            {
                PaletteResultsListBox.ScrollIntoView(_viewModel.SelectedPaletteItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _viewModel.ExecuteSelectedPaletteItemCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.CloseCommandPaletteCommand.Execute(null);
            e.Handled = true;
        }
    }
}