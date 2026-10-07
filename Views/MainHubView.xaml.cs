using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class MainHubView : UserControl
{
    private Window? _hostWindow;

    public MainHubView()
    {
        InitializeComponent();

        // Se guarda la ventana al cargar: en Unloaded Window.GetWindow(this) ya devuelve null
        Loaded += (s, e) => AttachToWindow(Window.GetWindow(this));
        Unloaded += (s, e) => AttachToWindow(null);
    }

    private void AttachToWindow(Window? window)
    {
        if (_hostWindow != null) _hostWindow.PreviewKeyDown -= Window_PreviewKeyDown;
        _hostWindow = window;
        if (_hostWindow != null) _hostWindow.PreviewKeyDown += Window_PreviewKeyDown;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (DataContext is MainHubViewModel vm && vm.IsSuggestedPurchasesOpen)
            {
                vm.CloseSuggestedPurchases();
                e.Handled = true;
            }
        }
    }
}
