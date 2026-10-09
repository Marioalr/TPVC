namespace TPVC.ViewModels;

/// <summary>
/// Esc por capas y "escribir para buscar" de Pacientes (fase 6, paso 2d). Antes vivían en
/// MedicalRecordsView.xaml.cs; la vista solo atiende las teclas y el foco, y pregunta aquí qué hacer.
/// </summary>
public partial class MedicalRecordsViewModel
{
    /// <summary>Hay una hoja, diálogo o aviso abierto encima de la pantalla.</summary>
    public bool IsAnySheetOpen =>
        IsInfoModalVisible || IsAllergyAlertOpen || IsCatalogBrowserModalOpen
        || IsPrescriptionPdfModalOpen || IsFinishConsultationOpen || IsPrescriptionModalOpen || IsSuppliesModalOpen
        || IsServicesModalOpen || IsSendConsultationSheetOpen;

    /// <summary>En el inicio o la ficha, sin agenda ni hojas encima, lo que se teclea va al buscador.</summary>
    public bool CanTypeToSearch => (IsHomePage || IsChartPage) && !IsAgendaPanelOpen && !IsAnySheetOpen;

    /// <summary>Lo tecleado con el foco fuera de un campo se agrega al buscador.</summary>
    public void AppendTypedSearchText(string text) => HomeSearchText += text;

    /// <summary>
    /// Esc: cierra lo de más arriba (aviso, hojas, agenda, búsqueda) o vuelve de la ficha al inicio.
    /// false si no había nada que cerrar (la tecla sigue su camino).
    /// </summary>
    public bool TryCloseTopmost()
    {
        if (IsInfoModalVisible) DismissInfoModal();
        else if (IsAllergyAlertOpen) CancelAllergyPrescription();
        else if (IsCatalogBrowserModalOpen) CloseCatalogBrowser();
        else if (IsPrescriptionPdfModalOpen) ClosePrescriptionPdfPreview();
        // Mientras se envía no se cierra (la consulta quedaría en solo lectura sin explicación)
        else if (IsSendConsultationSheetOpen) CloseSendConsultationSheet();
        else if (IsCie10DropdownOpen) IsCie10DropdownOpen = false;
        // Esc en "Terminar consulta" = volver a la consulta (no cobra ni la cierra)
        else if (IsFinishConsultationOpen) CloseFinishConsultation();
        else if (IsPrescriptionModalOpen) ClosePrescriptionModal();
        else if (IsSuppliesModalOpen) CloseSuppliesModal();
        else if (IsServicesModalOpen) CloseServicesModal();
        else if (IsMovingAppointment) CancelMovingAppointment();
        else if (IsAgendaPanelOpen) CloseAgendaPanel();
        else if (IsHomePage && HasHomeQuery) HomeSearchText = string.Empty;
        else if (IsChartPage) GoToPatientsHome();
        else return false;
        return true;
    }
}
