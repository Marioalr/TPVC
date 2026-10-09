using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Candado de la consulta (fase 6, plan §1.4): lo que no se puede hacer mientras la consulta está
/// enviada a otro equipo o se está recibiendo una. Lo responde el coordinador (y después, la hoja de
/// envío). Los miembros se agregan en el primer paso que los necesita.
/// </summary>
public interface IConsultationGate
{
    /// <summary>El expediente no se edita aquí mientras su consulta está en otro equipo (o se actualiza con lo de allá): true y un aviso.</summary>
    bool BlockIfPatientHandedOff(PatientRecord? patient);
}
