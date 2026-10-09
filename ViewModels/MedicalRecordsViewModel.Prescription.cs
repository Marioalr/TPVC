using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

/// <summary>
/// El expediente durante la consulta en curso. La receta empieza vacía: la última del paciente no
/// se arrastra (antes se imprimía y guardaba en la consulta nueva si no se borraba a mano), pero
/// se puede repetir con un clic. Si la consulta termina sin receta, la última emitida sigue siendo
/// la del expediente; si se descarta, el expediente vuelve a como estaba antes de empezarla.
/// </summary>
public partial class MedicalRecordsViewModel
{
    // La "última receta" de la ficha (ChartPrescription) es de Chart.

    // Las indicaciones de la receta (vista previa, impresión y PDF) son de Preview; el plan de la
    // consulta (Consultation) le avisa al cambiar.

    private void NotifyPrescriptionStateChanged()
    {
        Preview.RaisePrescriptionIndications();
        Prescription.RaiseRepeatState();
        Chart.RaisePrescriptionState();
    }
}
