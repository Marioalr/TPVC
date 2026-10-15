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
                var other = TextSearch.Normalize($"{p.RecordNumber} {p.OriginalFolio} {p.LastDiagnosis}");
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
            var today = list.Where(a => !a.IsCancelled).OrderBy(a => AgendaPanelViewModel.ParseAppointmentTime(a.Time)).ToList();
            // Paso 8.23: las que se empalman llevan la marca "Empalme", como en la agenda
            AgendaPanelViewModel.MarkOverlaps(today);
            TodayAppointments = new ObservableCollection<PatientAppointment>(today);
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudieron cargar las citas de hoy", ex);
        }
    }

    // ================================================================== posibles duplicados (paso 8.23)

    /// <summary>Los pares que bajaron de otro equipo y todavía no se cierran con "Entendido", el más reciente primero.</summary>
    private readonly List<DuplicatePatientPair> _duplicatePairs = new();

    /// <summary>Cuántos pares se muestran en el banner; los demás se cuentan.</summary>
    public const int MaxDuplicatePairsShown = 3;

    [ObservableProperty]
    private string _duplicatePatientsNoticeText = string.Empty;

    public bool HasDuplicatePatientsNotice => DuplicatePatientsNoticeText.Length > 0;

    partial void OnDuplicatePatientsNoticeTextChanged(string value) => OnPropertyChanged(nameof(HasDuplicatePatientsNotice));

    /// <summary>Los pares de la última revisión, para las pruebas y para quien quiera abrirlos.</summary>
    public IReadOnlyList<DuplicatePatientPair> DuplicatePairs => _duplicatePairs;

    /// <summary>
    /// Después de bajar: suma al banner los pares de posibles duplicados que llegaron de otro equipo (uno que ya está no se
    /// repite; si vuelve a llegar, sube al principio).
    /// </summary>
    public void AddDuplicatePairs(IEnumerable<DuplicatePatientPair> pairs)
    {
        foreach (var pair in pairs)
        {
            _duplicatePairs.RemoveAll(p => p.Key == pair.Key);
            _duplicatePairs.Insert(0, pair);
        }
        RefreshDuplicatePatientsNotice();
    }

    /// <summary>"Entendido": el banner se cierra; un par que vuelva a llegar (otro equipo lo edita) vuelve a avisar.</summary>
    [RelayCommand]
    public void DismissDuplicatePatientsNotice()
    {
        _duplicatePairs.Clear();
        RefreshDuplicatePatientsNotice();
    }

    private void RefreshDuplicatePatientsNotice()
    {
        int count = _duplicatePairs.Count;
        DuplicatePatientsNoticeText = count == 0
            ? string.Empty
            : (count == 1 ? "Posible expediente duplicado" : "Posibles expedientes duplicados") + " con lo que llegó de otro equipo: " +
              string.Join("; ", _duplicatePairs.Take(MaxDuplicatePairsShown).Select(p => p.Text)) +
              (count > MaxDuplicatePairsShown ? $" y {count - MaxDuplicatePairsShown} más" : string.Empty) +
              ". Revise las dos fichas: si son la misma persona, siga con una. TPVC no los une solo.";
        OnPropertyChanged(nameof(DuplicatePairs));
    }
}
