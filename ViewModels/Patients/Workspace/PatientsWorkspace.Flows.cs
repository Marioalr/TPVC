using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Flujos que cruzan a varios dueños (fase 6, plan §3): cada uno es un solo método con el mismo orden de
/// siempre, y cada paso escribe en su dueño. Apartado Pacientes (fase 2 del rediseño): inicio del
/// consultorio, ficha, alta en pantalla y agenda como panel. Un camino por tarea: el buscador abre la
/// ficha, la ficha inicia la consulta. La consulta en curso tiene secciones en el orden de la entrevista
/// y una "cuenta de la consulta" que se llena sola (receta que se surte aquí, insumos y servicios); todo
/// se cobra al terminar, sin salir al Punto de Venta.
/// </summary>
public partial class PatientsWorkspace
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    // ================================================================== carga y consulta en curso

    public async Task LoadInitialDataAsync()
    {
        await LoadPatientsAsync();
        await LoadServicesAsync();
        await Templates.LoadPrescriptionTemplatesAsync();
        await Home.LoadTodayAppointmentsAsync();
        Autosave.TryRestoreConsultationDraft();
        await PatientForm.TryRestorePatientFormDraftAsync();
    }

    public async Task LoadPatientsAsync()
    {
        if (_inventoryService != null)
        {
            // Listado ligero: las notas de consulta solo se cargan para el expediente abierto
            var dbPatients = await _inventoryService.GetPatientSummariesAsync();
            var prevSelected = Session.SelectedPatient;
            var prevSelectedId = prevSelected?.Id ?? 0;

            // Se conserva la misma instancia del paciente seleccionado (la UI y los formularios
            // abiertos trabajan sobre ella); solo se toma de la base su historial de consultas.
            PatientRecord? keptSelected = null;
            bool replacedSelected = false;
            if (prevSelected != null && prevSelectedId > 0)
            {
                var index = dbPatients.FindIndex(p => p.Id == prevSelectedId);
                if (index >= 0)
                {
                    // Si otro equipo cambió el expediente (llegó con la sincronización), se toma la
                    // versión de la base: guardar después la instancia vieja borraría esos cambios.
                    // Se decide después de leer: mientras tanto pudo cambiar la selección o iniciarse
                    // una consulta con ese paciente.
                    var fresh = dbPatients[index];
                    var consultations = await _inventoryService.GetPatientConsultationsAsync(prevSelectedId);
                    bool forced = Session.ForceFreshPatientIds.Contains(prevSelectedId);
                    if (ReferenceEquals(Session.SelectedPatient, prevSelected)
                        && (forced || fresh.UpdatedAtUtc > prevSelected.UpdatedAtUtc)
                        && CanReplaceSelectedPatientInstance(prevSelected))
                    {
                        Session.ForceFreshPatientIds.Remove(prevSelectedId);
                        fresh.Consultations = consultations;
                        keptSelected = fresh;
                        replacedSelected = true;
                    }
                    else
                    {
                        prevSelected.Consultations = consultations;
                        dbPatients[index] = prevSelected;
                        keptSelected = prevSelected;
                    }
                }
            }

            Session.Patients = new ObservableCollection<PatientRecord>(dbPatients);

            _consultationService?.LoadPatientsFromDatabase(dbPatients);

            if (prevSelectedId > 0)
            {
                if (!ReferenceEquals(Session.SelectedPatient, prevSelected))
                {
                    // Se eligió otro paciente mientras se leía la base: no se toca su ficha
                }
                else if (keptSelected == null)
                {
                    Session.SelectedPatient = null; // El expediente fue dado de baja
                }
                else if (replacedSelected)
                {
                    Chart.ReplaceSelectedPatientKeepingView(keptSelected);
                }
                else
                {
                    Session.RefreshConsultationsHistory(keptSelected);
                }
            }
            else if (_consultationService != null && _consultationService.HasActiveConsultation)
            {
                var activeName = _consultationService.CurrentConsultation?.PatientName;
                Session.SelectedPatient = Session.Patients.FirstOrDefault(p => p.FullName == activeName);
            }
        }
    }

    /// <summary>
    /// La instancia abierta se puede cambiar por la de la base salvo que alguien trabaje sobre ella
    /// (alta o edición abierta, o es el paciente de la consulta en curso).
    /// </summary>
    private bool CanReplaceSelectedPatientInstance(PatientRecord patient) =>
        !PatientForm.IsPatientModalOpen
        && (Record.Patient == null || Record.Patient.Id != patient.Id)
        && !(Active.HasActiveConsultation && Active.ActiveConsultation?.PatientId == patient.Id);

    private bool NeedsConsultationsLoad(PatientRecord patient) =>
        _inventoryService != null && patient.Id > 0 && patient.Consultations.Count == 0 && patient.ConsultationCount > 0;

    /// <summary>Carga bajo demanda el historial de un expediente del listado ligero.</summary>
    private async Task LoadConsultationsForAsync(PatientRecord patient, bool refreshSelectedHistory)
    {
        if (_inventoryService == null) return;
        try
        {
            patient.Consultations = await _inventoryService.GetPatientConsultationsAsync(patient.Id);
            if (refreshSelectedHistory && ReferenceEquals(Session.SelectedPatient, patient))
            {
                Session.RefreshConsultationsHistory(patient);
                RefreshVitalSignsEvolution(patient);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"No se pudo cargar el historial del expediente {patient.RecordNumber}", ex);
        }
    }

    public void SyncActiveConsultation()
    {
        Active.RaiseConsultationChanged();
        NotifyAccountChanged();

        if (Active.HasActiveConsultation)
        {
            Active.StartConsultationTimer();
        }
        else
        {
            Active.StopConsultationTimer();
            Account.FeeServiceOverrideId = null;
            Handoff.ReceivedHandoffId = null;
            Handoff.ReceivedHandoffToken = null;
            // Cualquier otro cierre (p. ej. cobrada desde el Punto de Venta) también cierra su receta
            if (Record.Patient != null) _ = Record.EndConsultationOnRecordAsync(discarded: false);
        }

        Autosave.OnActiveConsultationStateChangedForAutosave();
        NotifyPatientsPageState();
        NotifyPrescriptionStateChanged();
        Handoff.NotifyHandoffState();
    }

    public async Task LoadServicesAsync()
    {
        await Catalog.LoadAsync();

        Prescription.ApplyPrescriptionSuggestions(Prescription.PrescriptionSearchText ?? string.Empty);
        Supplies.ApplySupplySuggestions(Supplies.SupplySearchText ?? string.Empty);
        ClinicalServices.ApplyServiceModalSuggestions(ClinicalServices.ServiceModalSearchText ?? string.Empty);
        CatalogBrowser.ApplyCatalogBrowserFilter();
    }

    /// <summary>Se cobró la consulta (o se terminó sin cargos): se cierra y se vuelve al inicio.</summary>
    private async Task OnConsultationChargedAsync(SaleReceipt? receipt)
    {
        await HandleSaleCompletedFromConsultationAsync();
        if (receipt != null) Feedback.ShowSuccess($"Consulta cobrada · {receipt.Folio}");
    }

    public async Task HandleSaleCompletedFromConsultationAsync()
    {
        try
        {
            // 0. Si por alguna razón no se guardó la nota de consulta en BD, asegurarse de persistirla
            if (!Consultation.IsCurrentConsultationSaved && Session.SelectedPatient != null)
            {
                try
                {
                    await Consultation.SaveActiveConsultationToDbAsync(DateTime.Now);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("No se pudo guardar la nota de consulta al cobrar", ex);
                }
            }
            Consultation.IsCurrentConsultationSaved = false;
            await Record.EndConsultationOnRecordAsync(discarded: false);

            // 1. Cerrar "Terminar consulta" y cualquier otra hoja secundaria
            Consultation.CloseFinishConsultation();
        Prescription.IsPrescriptionModalOpen = false;
        Supplies.IsSuppliesModalOpen = false;
        ClinicalServices.IsServicesModalOpen = false;
        PatientForm.IsPatientModalOpen = false;
        CatalogBrowser.IsCatalogBrowserModalOpen = false;
        Info.DismissInfoModal();

        // 2. Finalizar y cerrar la consulta médica en el servicio y detener cronómetro
        _consultationService?.CloseActiveConsultation();
        SyncActiveConsultation();
        Active.StopConsultationTimer();

        // 3. Limpiar los campos de la consulta
        Consultation.ClearConsultationFields();

        // 4. Salir de todo y volver a la vista inicial (sin paciente seleccionado)
        Session.SelectedPatient = null;
        Session.ConsultationsHistory.Clear();

        // 6. La cita que se atendía queda como atendida y se vuelve al inicio, listo para el siguiente
        await CompleteAttendedAppointmentAsync();
        Navigation.GoToPatientsHome();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al finalizar consulta tras venta completada", ex);
        }
    }

    /// <summary>Al cerrar el programa o apagar Windows: consulta y alta de paciente en curso.</summary>
    public void FlushConsultationDraft()
    {
        Autosave.SaveConsultationDraftNow();
        PatientForm.SavePatientFormDraftNow();
    }

    // Las hojas de la consulta viven en Prescription (receta, alerta de alergia, posología, receta
    // anterior y "Se surte aquí"), Supplies (insumos) y ClinicalServices (servicios).

    // El historial de citas del paciente es de Chart (el panel de agenda y el estado de las citas, de Agenda).

    // ==========================================
    // EVOLUCIÓN DE SIGNOS VITALES (HISTORIAL Y GRÁFICAS): VitalsChart
    // ==========================================

    /// <summary>Recalcula la gráfica de signos del paciente dado o, si no se da, del paciente abierto.</summary>
    public void RefreshVitalSignsEvolution(PatientRecord? patient = null) => VitalsChart.RefreshVitalSignsEvolution(patient ?? Session.SelectedPatient);

    // ================================================================== avisos que cruzan a varios dueños

    private void NotifyPatientsPageState()
    {
        Navigation.RaisePageFlags();
        OnPropertyChanged(nameof(ShowsConsultationBanner));
        OnPropertyChanged(nameof(ConsultationBannerText));
        Chart.RaiseConsultationLink();
        Handoff.RaiseIncomingNoticeState();
    }

    // La "última receta" de la ficha (ChartPrescription) es de Chart.

    // Las indicaciones de la receta (vista previa, impresión y PDF) son de Preview; el plan de la
    // consulta (Consultation) le avisa al cambiar.

    private void NotifyPrescriptionStateChanged()
    {
        Preview.RaisePrescriptionIndications();
        Prescription.RaiseRepeatState();
        Chart.RaisePrescriptionState();
    }

    // Las secciones de la nota, sus botones rápidos, la alerta de signos vitales, la consulta anterior
    // (PreviousConsultation) y el cobro (BuildCheckoutCart) viven en Consultation.

    // =========================================================================
    // CUENTA DE LA CONSULTA (el honorario y los cargos viven en Account)
    // =========================================================================

    private void NotifyAccountChanged()
    {
        Consultation.RaisePreviousConsultation();
        Account.RaiseAccountChanged();
        Active.RaiseTotalAmount();
    }

    // ================================================================== páginas

    /// <summary>Al entrar desde el inicio de la app: a la consulta si hay una en curso, si no al inicio.</summary>
    public void EnterPatientsSection()
    {
        Handoff.OnEnterPatientsSectionForHandoff();
        if (Active.HasActiveConsultation)
        {
            ShowConsultation();
        }
        else if (!Navigation.IsFormPage)
        {
            Navigation.GoToPatientsHome();
        }
    }

    [RelayCommand]
    public void OpenPatientChart(PatientRecord? patient)
    {
        if (patient == null) return;
        Home.HomeSearchText = string.Empty;
        Session.SelectPatient(patient);
        Navigation.PatientsPage = PageChart;
    }

    [RelayCommand]
    public void ShowConsultation()
    {
        if (!Active.HasActiveConsultation) return;
        var id = Active.ActiveConsultation!.PatientId;
        if (Session.SelectedPatient?.Id != id)
        {
            var patient = Session.Patients.FirstOrDefault(p => p.Id == id);
            if (patient != null) Session.SelectedPatient = patient;
        }
        Navigation.PatientsPage = PageConsultation;
    }

    /// <summary>Botón "Expediente" de la consulta: la ficha completa sin cerrar la consulta.</summary>
    [RelayCommand]
    public void ShowChartFromConsultation()
    {
        if (Session.SelectedPatient != null) Navigation.PatientsPage = PageChart;
    }

    [RelayCommand]
    public void ChartPrimaryAction()
    {
        if (Chart.IsConsultationForSelectedPatient)
        {
            ShowConsultation();
        }
        else if (Active.HasActiveConsultation)
        {
            Info.InfoModalMessage = Handoff.IsConsultationHandedOff
                ? $"La consulta de {Active.ActiveConsultationPatientName} está enviada a {Handoff.HandoffTargetName}. Recupérela o espere a que la acepte antes de iniciar otra."
                : $"Hay una consulta en curso con {Active.ActiveConsultationPatientName}. Termínela o descártela antes de iniciar otra.";
            Info.IsInfoModalVisible = true;
        }
        else
        {
            Consultation.StartActiveConsultation();
        }
    }

    // ================================================================== inicio: Home

    [RelayCommand]
    public async Task CreatePatientFromSearchAsync()
    {
        var typed = Home.HomeSearchText.Trim();
        await PatientForm.NewPatientAsync();
        // Si se escribió un nombre (no un folio o teléfono), el alta empieza con él
        if (typed.Length > 0 && !typed.Any(char.IsDigit))
        {
            PatientFormRules.ParseFullName(typed, out var first, out var last, out var maternal);
            PatientForm.FormFirstName = ToTitle(first);
            PatientForm.FormLastName = ToTitle(last);
            PatientForm.FormMaternalLastName = ToTitle(maternal);
        }
        Home.HomeSearchText = string.Empty;
    }

    private static string ToTitle(string text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : Mexican.TextInfo.ToTitleCase(text.Trim().ToLower(Mexican));

    private async Task RefreshAppointmentViewsAsync()
    {
        await Home.LoadTodayAppointmentsAsync();
        if (Agenda.IsAgendaPanelOpen) await Agenda.LoadAgendaPanelAsync();
        if (Session.SelectedPatient != null) await Chart.LoadPatientAppointmentsAsync(Session.SelectedPatient.Id);
    }

    /// <summary>"Atender" en una cita de hoy: abre la consulta con el motivo de la cita.</summary>
    [RelayCommand]
    public void AttendAppointment(PatientAppointment? appointment)
    {
        if (appointment == null) return;

        var patient = appointment.PatientRecordId > 0 ? Session.Patients.FirstOrDefault(p => p.Id == appointment.PatientRecordId) : null;
        if (patient == null)
        {
            // Cita sin expediente: se busca por nombre para abrirlo o crearlo
            Agenda.IsAgendaPanelOpen = false;
            Navigation.GoToPatientsHome();
            Home.HomeSearchText = appointment.PatientName;
            return;
        }

        if (Active.HasActiveConsultation)
        {
            if (Active.ActiveConsultation?.PatientId == patient.Id)
            {
                ShowConsultation();
            }
            else
            {
                Info.InfoModalMessage = $"Hay una consulta en curso con {Active.ActiveConsultationPatientName}. Termínela o descártela antes de atender otra cita.";
                Info.IsInfoModalVisible = true;
            }
            return;
        }

        Agenda.IsAgendaPanelOpen = false;
        Session.SelectPatient(patient);
        Consultation.StartActiveConsultation();
        if (!string.IsNullOrWhiteSpace(appointment.Reason)) Consultation.ConsultationChiefComplaint = appointment.Reason.Trim();
        Consultation.AttendingAppointmentId = appointment.Id;
        // La copia pendiente se tomó antes de saber la cita: si el programa se cierra antes de la
        // siguiente edición, la consulta recuperada debe seguir ligada a ella (y marcarla "Atendida")
        Autosave.ScheduleAutosave();
    }

    /// <summary>Al cobrar la consulta, la cita que se estaba atendiendo queda como atendida.</summary>
    private async Task CompleteAttendedAppointmentAsync()
    {
        if (Consultation.AttendingAppointmentId is not int id || _inventoryService == null) return;
        Consultation.AttendingAppointmentId = null;
        try
        {
            await _inventoryService.UpdateAppointmentStatusAsync(id, "Atendida");
            await RefreshAppointmentViewsAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo marcar la cita como atendida", ex);
        }
    }

    // ================================================================== ficha: Chart (aquí, el flujo de editar una sección)

    /// <summary>"Editar esta sección": el alta abierta justo en esa sección.</summary>
    [RelayCommand]
    public void EditAntecedent(AntecedentSection? section)
    {
        if (Handoff.BlockIfPatientHandedOff(Session.SelectedPatient)) return;
        PatientForm.EditPatient();
        if (section != null) PatientForm.FormOpenSection = section.Key;
    }

    // ================================================================== agenda (panel lateral): Agenda

    private async Task RefreshAgendaAfterChangeAsync(int patientId)
    {
        await Agenda.LoadAgendaPanelAsync();
        await Home.LoadTodayAppointmentsAsync();
        if (Session.SelectedPatient != null && Session.SelectedPatient.Id == patientId) await Chart.LoadPatientAppointmentsAsync(patientId);
    }

    // ================================================================== consulta en curso

    /// <summary>
    /// Descarta la consulta en curso (paciente equivocado, se retiró...): no guarda la nota ni cobra.
    /// Si ya se había guardado un avance, se elimina del expediente.
    /// </summary>
    [RelayCommand]
    public async Task DiscardActiveConsultationAsync()
    {
        if (!Active.HasActiveConsultation || Handoff.BlockIfConsultationReadOnly()) return;

        if (!await _dialogs.ConfirmAsync(
                "¿Descartar la consulta en curso?\n\nNo se guardará la nota médica ni se cobrará nada de la cuenta.",
                "Descartar consulta"))
        {
            return;
        }

        var folio = _consultationService?.CurrentConsultation?.Folio;
        if (_inventoryService != null && Session.SelectedPatient != null && !string.IsNullOrWhiteSpace(folio))
        {
            try
            {
                await _inventoryService.DeleteConsultationNoteAsync(Session.SelectedPatient.Id, folio);
            }
            catch (Exception ex)
            {
                AppLogger.Error("No se pudo eliminar el avance de la consulta descartada", ex);
            }
        }

        // El avance que se hubiera guardado deja de verse en la ficha (historial, signos, resumen)
        var patient = Session.SelectedPatient;
        if (patient != null && !string.IsNullOrWhiteSpace(folio))
        {
            foreach (var note in patient.Consultations.Where(c => c.Folio == folio).ToList()) patient.Consultations.Remove(note);
            foreach (var note in Session.ConsultationsHistory.Where(c => c.Folio == folio).ToList()) Session.ConsultationsHistory.Remove(note);
            RefreshVitalSignsEvolution(patient);
        }

        await Record.EndConsultationOnRecordAsync(discarded: true);
        _consultationService?.CloseActiveConsultation();
        Consultation.IsCurrentConsultationSaved = false;
        Consultation.AttendingAppointmentId = null;
        SyncActiveConsultation();
        Active.StopConsultationTimer();
        Consultation.ClearConsultationFields();
        Navigation.PatientsPage = Session.SelectedPatient != null ? PageChart : PageHome;
    }

    // ================================================================== consulta entre equipos: Handoff

    // Consulta entre equipos: vive en Handoff (ConsultationHandoffViewModel). Aquí queda cerrar las hojas de
    // la consulta, que cruza a varios dueños; Handoff lo pide por el flujo (IPatientsWorkflow).

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
