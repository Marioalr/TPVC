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
    /// <summary>Cambió la receta de la consulta o la anterior: ficha, hoja de receta y vista previa.</summary>
    void NotifyPrescriptionStateChanged();

    /// <summary>Se agendó, movió, confirmó o canceló una cita: agenda, citas de hoy y las del expediente abierto.</summary>
    Task RefreshAgendaAfterChangeAsync(int patientId);

    /// <summary>Recarga los expedientes de la base (después de guardar el alta o la edición).</summary>
    Task LoadPatientsAsync();

    /// <summary>Inicia la consulta del paciente seleccionado ("Guardar e iniciar consulta").</summary>
    void StartActiveConsultation();

    /// <summary>Vuelve a la consulta en curso con su paciente (al cerrar el alta abierta desde ella).</summary>
    void ShowConsultation();

    /// <summary>Abre la ficha del paciente (primer resultado del buscador con Enter).</summary>
    void OpenPatientChart(PatientRecord? patient);
}
