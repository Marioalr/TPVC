using System.Threading.Tasks;
using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Flujos que un hijo le pide correr al coordinador (fase 6, plan §1.4). El coordinador los corre con
/// los mismos cuerpos de antes, en el mismo orden, sobre todos los dueños. Los miembros se agregan en
/// el primer paso que los necesita.
/// </summary>
public interface IPatientsWorkflow
{
    /// <summary>Cambió la consulta en curso en el servicio (inicio, cargos, cierre): todos los dueños se ponen al día.</summary>
    void SyncActiveConsultation();

    /// <summary>Cambió la receta de la consulta o la anterior: ficha, hoja de receta y vista previa.</summary>
    void NotifyPrescriptionStateChanged();

    /// <summary>Cambió el historial o el plan de la consulta: las indicaciones de la receta (vista previa) se vuelven a leer.</summary>
    void RaisePrescriptionIndications();

    /// <summary>Se agendó, movió, confirmó o canceló una cita: agenda, citas de hoy y las del expediente abierto.</summary>
    Task RefreshAgendaAfterChangeAsync(int patientId);

    /// <summary>Recarga los expedientes de la base (después de guardar el alta o la edición).</summary>
    Task LoadPatientsAsync();

    /// <summary>Recarga servicios y productos del catálogo y vuelve a filtrar las hojas y el explorador.</summary>
    Task LoadServicesAsync();

    /// <summary>Inicia la consulta del paciente seleccionado ("Guardar e iniciar consulta").</summary>
    void StartActiveConsultation();

    /// <summary>Cambió la cuenta (inicio de la consulta): consulta anterior, honorario y total.</summary>
    void NotifyAccountChanged();

    /// <summary>Recalcula la gráfica de signos del paciente dado o, si no se da, del paciente abierto.</summary>
    void RefreshVitalSignsEvolution(PatientRecord? patient);

    /// <summary>Se cobró la consulta (o se terminó sin cargos): se cierra, la cita queda atendida y se vuelve al inicio.</summary>
    Task OnConsultationChargedAsync(SaleReceipt? receipt);

    /// <summary>Vuelve a la consulta en curso con su paciente (al cerrar el alta abierta desde ella).</summary>
    void ShowConsultation();

    /// <summary>Abre la ficha del paciente (primer resultado del buscador con Enter).</summary>
    void OpenPatientChart(PatientRecord? patient);
}
