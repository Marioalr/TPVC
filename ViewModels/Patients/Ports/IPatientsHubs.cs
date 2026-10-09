using System.Collections.ObjectModel;
using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Avisos de los hijos al coordinador (fase 6, plan §1.4). Cada gancho generado de un hijo
/// (OnXChanged) llama aquí en el mismo punto en que antes corría su cuerpo, y el coordinador hace lo
/// de siempre con los demás dueños, en el mismo orden. Los miembros se agregan en el primer paso que
/// los necesita.
/// </summary>
public interface IPatientsHubs
{
    /// <summary>Cambió el paciente seleccionado (Session), antes de su aviso.</summary>
    void OnSelectedPatientChanged(PatientRecord? oldValue, PatientRecord? newValue);

    /// <summary>Cambió la lista de expedientes (Session).</summary>
    void OnPatientsChanged();

    /// <summary>Cambió el historial de consultas del paciente seleccionado (Session).</summary>
    void OnConsultationsHistoryChanged(ObservableCollection<PatientConsultation> value);

    /// <summary>Cambió la página de Pacientes (Navigation).</summary>
    void OnPatientsPageChanged();

    /// <summary>Avanzó (o se reinició) el cronómetro de la consulta en curso (Active).</summary>
    void OnActiveConsultationElapsedTimeChanged();

    /// <summary>Cambiaron los servicios del catálogo (Catalog).</summary>
    void OnServicesChanged();

    /// <summary>Terminó la carga de las plantillas de receta (Templates).</summary>
    void OnPrescriptionTemplatesLoaded();

    /// <summary>Cambió la serie de signos vitales de la gráfica (VitalsChart).</summary>
    void OnVitalSignsEvolutionPointsChanged(ObservableCollection<VitalSignsEvolutionPoint> value);
}
