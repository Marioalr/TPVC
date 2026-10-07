using System.Windows.Controls;
using System.Windows.Threading;

namespace TPVC.Views;

public partial class PosServicesSheetView : UserControl
{
    public PosServicesSheetView()
    {
        InitializeComponent();
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
            ServiceSearchBox.Focus();
            ServiceSearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }
}
