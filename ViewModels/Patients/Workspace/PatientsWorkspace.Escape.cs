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
        || Preview.IsPrescriptionPdfModalOpen || Labs.IsPreviewOpen || ConsultationDetail.IsOpen || Consultation.IsFinishConsultationOpen || Prescription.IsPrescriptionModalOpen || Supplies.IsSuppliesModalOpen
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
        else if (Labs.IsPreviewOpen) Labs.ClosePreview();
        // Corrigiendo la receta, Esc cancela la corrección; si no, cierra la hoja de la consulta
        else if (ConsultationDetail.IsEditingPrescription) ConsultationDetail.CancelEditPrescription();
        else if (ConsultationDetail.IsOpen) ConsultationDetail.Close();
        // Mientras se envía no se cierra (la consulta quedaría en solo lectura sin explicación)
        else if (Handoff.IsSendConsultationSheetOpen) Handoff.CloseSendConsultationSheet();
        else if (Consultation.IsCie10DropdownOpen) Consultation.IsCie10DropdownOpen = false;
        else if (Labs.HasSuggestions) Labs.StudySearchText = string.Empty;
        // Esc en "Terminar consulta" = volver a la consulta (no cobra ni la cierra)
        else if (Consultation.IsFinishConsultationOpen) Consultation.CloseFinishConsultation();
        // Dentro de la receta (2026-10-14), Esc cierra primero la lista abierta (indicaciones frecuentes o sugerencias de
        // medicamentos) y la corrección de una indicación; la hoja se cierra con la siguiente Esc
        else if (Prescription.IsPrescriptionModalOpen && Prescription.CaptureIndications.IsOpen) Prescription.CaptureIndications.Close();
        else if (Prescription.IsPrescriptionModalOpen && Prescription.SheetIndication.Picker.IsOpen) Prescription.SheetIndication.Picker.Close();
        else if (Prescription.IsPrescriptionModalOpen && Prescription.SheetIndication.IsEditing) Prescription.SheetIndication.Cancel();
        else if (Prescription.IsPrescriptionModalOpen && Prescription.IsPrescriptionDropdownOpen) Prescription.ClosePrescriptionSuggestions();
        else if (Prescription.IsPrescriptionModalOpen) Prescription.ClosePrescriptionModal();
        else if (Supplies.IsSuppliesModalOpen) Supplies.CloseSuppliesModal();
        else if (ClinicalServices.IsServicesModalOpen) ClinicalServices.CloseServicesModal();
        else if (Agenda.IsMovingAppointment) Agenda.CancelMovingAppointment();
        else if (Agenda.IsAgendaPanelOpen) Agenda.CloseAgendaPanel();
        // "Cambiar indicación" en la sección 5 de la consulta: primero la lista, luego la corrección
        else if (Navigation.IsConsultationPage && Prescription.PlanIndication.Picker.IsOpen) Prescription.PlanIndication.Picker.Close();
        else if (Navigation.IsConsultationPage && Prescription.PlanIndication.IsEditing) Prescription.PlanIndication.Cancel();
        else if (Navigation.IsHomePage && Home.HasHomeQuery) Home.HomeSearchText = string.Empty;
        else if (Navigation.IsChartPage) Navigation.GoToPatientsHome();
        else return false;
        return true;
    }
}
