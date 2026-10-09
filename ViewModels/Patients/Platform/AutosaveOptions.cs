using System;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Pausa al escribir antes de guardar la copia local, la misma para la consulta en curso y para el
/// alta de paciente (las pruebas la acortan).
/// </summary>
public sealed class AutosaveOptions
{
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(1500);
}
