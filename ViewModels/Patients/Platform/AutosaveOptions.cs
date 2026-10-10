using System;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Pausa al escribir antes de guardar la copia local, la misma para la consulta en curso y para el
/// alta de paciente (las pruebas la acortan).
/// </summary>
public sealed class AutosaveOptions
{
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Reloj y temporizador del guardado automático de la consulta. Las pruebas ponen uno manual para
    /// decidir cuándo se dispara la escritura en lugar de esperar un tiempo fijo.
    /// </summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;
}
