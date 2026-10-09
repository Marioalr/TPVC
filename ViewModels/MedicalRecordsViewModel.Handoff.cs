namespace TPVC.ViewModels;

/// <summary>
/// Consulta entre equipos: vive en Handoff (ConsultationHandoffViewModel). Aquí queda cerrar las hojas de
/// la consulta, que cruza a varios dueños; Handoff lo pide por el flujo (IPatientsWorkflow).
/// </summary>
public partial class MedicalRecordsViewModel
{
    private void CloseConsultationSheets()
    {
        Prescription.IsPrescriptionModalOpen = false;
        Supplies.IsSuppliesModalOpen = false;
        ClinicalServices.IsServicesModalOpen = false;
        CatalogBrowser.IsCatalogBrowserModalOpen = false;
        Preview.IsPrescriptionPdfModalOpen = false;
        Prescription.IsAllergyAlertOpen = false;
        Agenda.IsAgendaPanelOpen = false;
    }
}
