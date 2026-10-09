using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Lo del envío entre equipos que el borrador de la consulta guarda y recupera (fase 6, plan §1.4 y §6):
/// la marca de "enviada a otro equipo" y la consulta recibida (su envío y la ficha con que se tomó). El
/// guardado automático (ConsultationAutosave) arma y aplica el borrador; esto lo responde el coordinador
/// con lo de Handoff (ConsultationHandoffViewModel).
/// </summary>
public interface IDraftHandoffParticipant
{
    /// <summary>Marca del envío propio en curso (null si la consulta no se envió): va en todas las copias.</summary>
    ConsultationHandoffMarker? OutgoingMarker { get; }

    /// <summary>Envío con que llegó la consulta en pantalla (si se recibió de otro equipo).</summary>
    string? ReceivedHandoffId { get; set; }

    /// <summary>Ficha con que este equipo tomó la consulta recibida.</summary>
    string? ReceivedHandoffToken { get; set; }

    /// <summary>Al abrir el programa con una consulta enviada: vuelve en solo lectura.</summary>
    void RestoreOutgoingHandoff(ConsultationHandoffMarker marker);

    /// <summary>La copia de una consulta recibida quedó en disco: se anota como recibida y se borra su copia de la nube.</summary>
    void OnReceivedHandoffPersisted(ConsultationDraft draft);
}
