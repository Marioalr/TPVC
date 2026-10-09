namespace TPVC.ViewModels.Patients;

/// <summary>
/// Esc por capas y "escribir para buscar" de Pacientes (fase 6, paso 2d). Antes vivían en
/// MedicalRecordsView.xaml.cs; la vista solo atiende las teclas y el foco, y pregunta aquí qué hacer.
/// </summary>
public partial class PatientsWorkspace
{
    /// <summary>Hay una hoja, diálogo o aviso abierto encima de la pantalla.</summary>
    public bool IsAnySheetOpen =>
        Info.IsInfoModalVisible || Prescription.IsAllergyAlertOpen || CatalogBrowser.IsCatalogBrowserModalOpen
        || Preview.IsPrescriptionPdfModalOpen || Consultation.IsFinishConsultationOpen || Prescription.IsPrescriptionModalOpen || Supplies.IsSuppliesModalOpen
        || ClinicalServices.IsServicesModalOpen || Handoff.IsSendConsultationSheetOpen;

    /// <summary>En el inicio o la ficha, sin agenda ni hojas encima, lo que se teclea va al buscador.</summary>
    public bool CanTypeToSearch => (Navigation.IsHomePage || Navigation.IsChartPage) && !Agenda.IsAgendaPanelOpen && !IsAnySheetOpen;

    /// <summary>Lo tecleado con el foco fuera de un campo se agrega al buscador.</summary>
    public void AppendTypedSearchText(string text) => Home.HomeSearchText += text;

    /// <summary>
    /// Esc: cierra lo de más arriba (aviso, hojas, agenda, búsqueda) o vuelve de la ficha al inicio.
    /// false si no había nada que cerrar (la tecla sigue su camino).
    /// </summary>
    public bool TryCloseTopmost()
    {
        if (Info.IsInfoModalVisible) Info.DismissInfoModal();
        else if (Prescription.IsAllergyAlertOpen) Prescription.CancelAllergyPrescription();
        else if (CatalogBrowser.IsCatalogBrowserModalOpen) CatalogBrowser.CloseCatalogBrowser();
        else if (Preview.IsPrescriptionPdfModalOpen) Preview.ClosePrescriptionPdfPreview();
        // Mientras se envía no se cierra (la consulta quedaría en solo lectura sin explicación)
        else if (Handoff.IsSendConsultationSheetOpen) Handoff.CloseSendConsultationSheet();
        else if (Consultation.IsCie10DropdownOpen) Consultation.IsCie10DropdownOpen = false;
        // Esc en "Terminar consulta" = volver a la consulta (no cobra ni la cierra)
        else if (Consultation.IsFinishConsultationOpen) Consultation.CloseFinishConsultation();
        else if (Prescription.IsPrescriptionModalOpen) Prescription.ClosePrescriptionModal();
        else if (Supplies.IsSuppliesModalOpen) Supplies.CloseSuppliesModal();
        else if (ClinicalServices.IsServicesModalOpen) ClinicalServices.CloseServicesModal();
        else if (Agenda.IsMovingAppointment) Agenda.CancelMovingAppointment();
        else if (Agenda.IsAgendaPanelOpen) Agenda.CloseAgendaPanel();
        else if (Navigation.IsHomePage && Home.HasHomeQuery) Home.HomeSearchText = string.Empty;
        else if (Navigation.IsChartPage) Navigation.GoToPatientsHome();
        else return false;
        return true;
    }
}
