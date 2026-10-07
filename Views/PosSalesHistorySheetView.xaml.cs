using System.Windows.Controls;
using System.Windows.Threading;

namespace TPVC.Views;

public partial class PosSalesHistorySheetView : UserControl
{
    public PosSalesHistorySheetView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.InvokeAsync(() => FolioSearchBox.Focus(), DispatcherPriority.Input);
        };
    }
}
