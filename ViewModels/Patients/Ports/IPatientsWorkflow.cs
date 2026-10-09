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
}
