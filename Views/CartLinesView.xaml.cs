using System.Windows.Controls;

namespace TPVC.Views;

public partial class CartLinesView : UserControl
{
    public CartLinesView()
    {
        InitializeComponent();
        // El renglón elegido con el teclado (o recién agregado) siempre a la vista
        Lines.SelectionChanged += (_, _) =>
        {
            if (Lines.SelectedItem != null) Lines.ScrollIntoView(Lines.SelectedItem);
        };
    }
}
