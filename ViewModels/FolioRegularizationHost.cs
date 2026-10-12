using TPVC.Services.Folios;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Paso 8.17: lo que la regularización de folios necesita saber de la pantalla. No corre con una consulta en curso,
/// productos en la caja o el alta o edición de un paciente abierta, y no toca a un paciente abierto.
/// </summary>
public sealed class FolioRegularizationHost : IFolioRegularizationHost
{
    private readonly PatientsWorkspace _patients;
    private readonly PosViewModel _pos;

    public FolioRegularizationHost(PatientsWorkspace patients, PosViewModel pos)
    {
        _patients = patients;
        _pos = pos;
    }

    public bool IsBusy => _patients.HasActiveConsultation || _patients.PatientForm.IsPatientModalOpen || _pos.HasCartItems;

    public bool CanReplacePatient(int patientId) => _patients.CanReplacePatientInstance(patientId);
}
