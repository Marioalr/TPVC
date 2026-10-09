using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Candado de la consulta (fase 6, plan §1.4): lo que no se puede hacer mientras la consulta está
/// enviada a otro equipo o se está recibiendo una. Lo responde el coordinador con lo de Handoff
/// (ConsultationHandoffViewModel). Los miembros se agregan en el primer paso que los necesita.
/// </summary>
public interface IConsultationGate
{
    /// <summary>La consulta se mandó a otro equipo (o se está mandando): aquí no se modifica. true y un aviso.</summary>
    bool BlockIfConsultationReadOnly();

    /// <summary>El expediente no se edita aquí mientras su consulta está en otro equipo (o se actualiza con lo de allá): true y un aviso.</summary>
    bool BlockIfPatientHandedOff(PatientRecord? patient);

    /// <summary>Se está recibiendo una consulta de otro equipo: true y un aviso.</summary>
    bool BlockIfReceivingHandoff();
}
