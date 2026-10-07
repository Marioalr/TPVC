using System;
using System.Collections.Generic;

namespace TPVC.Models;

/// <summary>
/// Copia de trabajo del alta o la edición de un paciente (guardado automático). Archivo local del
/// equipo: no se sincroniza y se borra al guardar o cancelar el formulario.
/// </summary>
public sealed class PatientFormDraft
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public DateTime SavedAt { get; set; }

    /// <summary>0 = paciente nuevo.</summary>
    public int PatientId { get; set; }

    /// <summary>Campos del formulario por nombre, como texto.</summary>
    public Dictionary<string, string> Fields { get; set; } = new();
}
