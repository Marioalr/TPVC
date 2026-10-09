using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Inicio del consultorio (fase 6, paso 11): el buscador de pacientes con sus resultados, los
/// pacientes recientes y las citas de hoy. Abrir un resultado (la ficha) lo corre el coordinador.
/// </summary>
public partial class PatientsHomeViewModel : ObservableObject
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    private readonly InventoryService? _inventoryService;
    private readonly PatientSession _session;
    private readonly PatientsNavigation _nav;
    private readonly IPatientsWorkflow _workflow;

    public PatientsHomeViewModel(InventoryService? inventoryService, PatientSession session, PatientsNavigation navigation, IPatientsWorkflow workflow)
    {
        _inventoryService = inventoryService;
        _session = session;
        _nav = navigation;
        _workflow = workflow;
    }

    [ObservableProperty]
    private string _homeSearchText = string.Empty;

    partial void OnHomeSearchTextChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && _nav.IsChartPage) _nav.PatientsPage = PatientsNavigation.PageHome;
        RefreshHomeResults();
    }

    [ObservableProperty]
    private ObservableCollection<PatientRecord> _homeResults = new();

    [ObservableProperty]
    private ObservableCollection<PatientRecord> _recentPatients = new();

    public bool HasHomeQuery => !string.IsNullOrWhiteSpace(HomeSearchText);
    public bool HasNoHomeResults => HasHomeQuery && HomeResults.Count == 0;
    public string HomeResultsSummary => HomeResults.Count == 1 ? "1 paciente" : $"{HomeResults.Count} pacientes";
    public string CreateFromSearchText => $"Crear paciente \"{HomeSearchText.Trim()}\"";

    /// <summary>
    /// Pacientes que coinciden con lo escrito (sin acentos): primero los que empiezan así, luego
    /// los que tienen una palabra que empieza así y al final los que lo contienen en nombre, folio,
    /// teléfono o diagnóstico.
    /// </summary>
    public static List<PatientRecord> RankPatients(IEnumerable<PatientRecord> patients, string? query, int limit = 30)
    {
        var q = TextSearch.Normalize(query);
        if (q.Length == 0) return new List<PatientRecord>();
        var digits = new string(q.Where(char.IsDigit).ToArray());

        return patients
            .Select(p =>
            {
                var name = TextSearch.Normalize(p.FullName);
                var other = TextSearch.Normalize($"{p.RecordNumber} {p.LastDiagnosis}");
                int rank;
                if (name.StartsWith(q, StringComparison.Ordinal)) rank = 0;
                else if ((" " + name).Contains(" " + q, StringComparison.Ordinal)) rank = 1;
                else if (name.Contains(q, StringComparison.Ordinal) || other.Contains(q, StringComparison.Ordinal)) rank = 2;
                else if (digits.Length >= 4 && new string((p.Phone ?? string.Empty).Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal)) rank = 2;
                else rank = -1;
                return (Patient: p, Rank: rank);
            })
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Patient.FullName, StringComparer.CurrentCulture)
            .Take(limit)
            .Select(x => x.Patient)
            .ToList();
    }

    public void RefreshHomeResults()
    {
        HomeResults = new ObservableCollection<PatientRecord>(RankPatients(_session.Patients, HomeSearchText));
        OnPropertyChanged(nameof(HasHomeQuery));
        OnPropertyChanged(nameof(HasNoHomeResults));
        OnPropertyChanged(nameof(HomeResultsSummary));
        OnPropertyChanged(nameof(CreateFromSearchText));
    }

    public void RefreshRecentPatients()
    {
        RecentPatients = new ObservableCollection<PatientRecord>(
            _session.Patients.OrderByDescending(p => p.LastVisit).ThenBy(p => p.FullName, StringComparer.CurrentCulture).Take(12));
    }

    [RelayCommand]
    public void OpenFirstHomeResult() => _workflow.OpenPatientChart(HomeResults.FirstOrDefault());

    /// <summary>La X del buscador (fase 7, 7.5): borra toda la búsqueda y deja el cursor en el cuadro, sin cambiar de página.</summary>
    [RelayCommand]
    public void ClearHomeSearch()
    {
        HomeSearchText = string.Empty;
        _nav.RequestSearchFocus();
    }

    [ObservableProperty]
    private ObservableCollection<PatientAppointment> _todayAppointments = new();

    public bool HasTodayAppointments => TodayAppointments.Count > 0;

    public string TodayTitle
    {
        get
        {
            var text = DateTime.Today.ToString("dddd d 'de' MMMM", Mexican);
            return "Hoy · " + text;
        }
    }

    partial void OnTodayAppointmentsChanged(ObservableCollection<PatientAppointment> value) =>
        OnPropertyChanged(nameof(HasTodayAppointments));

    public async Task LoadTodayAppointmentsAsync()
    {
        if (_inventoryService == null) return;
        try
        {
            var list = await _inventoryService.GetAppointmentsByDayAsync(DateTime.Today);
            TodayAppointments = new ObservableCollection<PatientAppointment>(
                list.Where(a => !a.IsCancelled).OrderBy(a => AgendaPanelViewModel.ParseAppointmentTime(a.Time)));
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudieron cargar las citas de hoy", ex);
        }
    }
}
