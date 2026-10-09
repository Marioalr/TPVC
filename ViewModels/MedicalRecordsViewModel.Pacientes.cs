using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.ViewModels.Patients;

namespace TPVC.ViewModels;

/// <summary>
/// Apartado Pacientes (fase 2 del rediseño): inicio del consultorio, ficha, alta en pantalla y
/// agenda como panel. Un camino por tarea: el buscador abre la ficha, la ficha inicia la consulta.
/// </summary>
public partial class MedicalRecordsViewModel
{
    // Las páginas son de Navigation; los nombres siguen aquí para quien ya los usa
    public const string PageHome = PatientsNavigation.PageHome;
    public const string PageChart = PatientsNavigation.PageChart;
    public const string PageForm = PatientsNavigation.PageForm;
    public const string PageConsultation = PatientsNavigation.PageConsultation;

    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    private Action? _onAppointmentsChangedHandler;

    private void InitializePacientes()
    {
        if (_inventoryService != null)
        {
            _onAppointmentsChangedHandler = () =>
            {
                try
                {
                    if (_ui.IsOffUiThread)
                    {
                        _ui.InvokeAsync(async () => await RefreshAppointmentViewsAsync());
                        return;
                    }
                }
                catch
                {
                    // Sin WPF (pruebas en Linux)
                }
                _ = RefreshAppointmentViewsAsync();
            };
            _inventoryService.OnAppointmentsChanged += _onAppointmentsChangedHandler;
        }
    }

    private void DisposePacientes()
    {
        if (_inventoryService != null && _onAppointmentsChangedHandler != null)
        {
            _inventoryService.OnAppointmentsChanged -= _onAppointmentsChangedHandler;
        }
    }

    // ================================================================== páginas

    /// <summary>Aviso "Consulta en curso · Volver" cuando se está fuera de la consulta.</summary>
    public bool ShowsConsultationBanner => Active.HasActiveConsultation && !Navigation.IsConsultationPage && !Navigation.IsFormPage;

    public string ConsultationBannerText => !Active.HasActiveConsultation
        ? string.Empty
        : Handoff.IsConsultationHandedOff
            ? $"Consulta de {Active.ActiveConsultationPatientName} enviada a {Handoff.HandoffTargetName}: esperando que la acepte"
            : $"Consulta en curso: {Active.ActiveConsultationPatientName} · {Active.ActiveConsultationElapsedTime}";

    private void NotifyPatientsPageState()
    {
        Navigation.RaisePageFlags();
        OnPropertyChanged(nameof(ShowsConsultationBanner));
        OnPropertyChanged(nameof(ConsultationBannerText));
        Chart.RaiseConsultationLink();
        Handoff.RaiseIncomingNoticeState();
    }

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
            PatientFormViewModel.ParseFullName(typed, out var first, out var last, out var maternal);
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
}
