using System;
using System.Windows.Controls;
using TPVC.Models;

namespace TPVC.Views;

public partial class PrescriptionPreviewSheetView : UserControl
{
    public PrescriptionPreviewSheetView()
    {
        InitializeComponent();

        // La receta lleva la fecha del día en que se imprime. La hoja se crea una vez y se muestra u oculta:
        // la fecha se pone cada vez que se abre (también si el programa sigue abierto al día siguiente)
        ShowToday();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ShowToday();
        };
    }

    private void ShowToday() => PrescriptionDate.Text = PrescriptionSheet.FormatDate(DateTime.Now);
}
