using System;

namespace TPVC.ViewModels.Patients;

public static class UiThreadExtensions
{
    /// <summary>
    /// El ComboBox vuelve a "sin selección" para poder aplicar la misma plantilla al siguiente
    /// medicamento; se difiere para no modificar SelectedItem dentro de su propio cambio. Sin hilo de
    /// interfaz (o si ponerlo en cola falla) se hace en ese momento.
    /// </summary>
    public static void ResetSelectionLater(this IUiThread ui, Action reset)
    {
        try
        {
            if (ui.TryPostBackground(reset)) return;
        }
        catch
        {
        }
        reset();
    }
}
