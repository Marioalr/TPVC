using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TPVC.ViewModels;

namespace TPVC.Views;

public partial class MainHubView : UserControl
{
    public MainHubView()
    {
        InitializeComponent();

        Loaded += (s, e) =>
        {
            var win = Window.GetWindow(this);
            if (win != null)
            {
                win.PreviewKeyDown -= Window_PreviewKeyDown;
                win.PreviewKeyDown += Window_PreviewKeyDown;
            }
        };

        Unloaded += (s, e) =>
        {
            var win = Window.GetWindow(this);
            if (win != null)
            {
                win.PreviewKeyDown -= Window_PreviewKeyDown;
            }
        };
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
