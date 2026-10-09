using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Páginas de Pacientes (fase 6, paso 5): inicio, ficha, alta y consulta. Al cambiar de página avisa
/// al coordinador, que actualiza lo que depende de ella (aviso de consulta en curso, consulta recibida...).
/// </summary>
public partial class PatientsNavigation : ObservableObject
{
    public const string PageHome = "Home";
    public const string PageChart = "Chart";
    public const string PageForm = "Form";
    public const string PageConsultation = "Consultation";

    private readonly IPatientsHubs _hubs;

    public PatientsNavigation(IPatientsHubs hubs)
    {
        _hubs = hubs;
    }

    /// <summary>Página desde la que se abrió el alta o la edición (al cerrarla se vuelve a ella).</summary>
    public string PageBeforeForm { get; set; } = PageHome;

    /// <summary>La vista enfoca el buscador (al volver al inicio o al terminar una consulta).</summary>
    public event Action? FocusSearchRequested;

    [ObservableProperty]
    private string _patientsPage = PageHome;

    partial void OnPatientsPageChanged(string value) => _hubs.OnPatientsPageChanged();

    public bool IsHomePage => PatientsPage == PageHome;
    public bool IsChartPage => PatientsPage == PageChart;
    public bool IsFormPage => PatientsPage == PageForm;
    public bool IsConsultationPage => PatientsPage == PageConsultation;

    /// <summary>Barra con buscador, agenda y nuevo paciente (inicio y ficha).</summary>
    public bool ShowsPatientsTopBar => IsHomePage || IsChartPage;

    /// <summary>Avisa qué página se ve (lo primero que avisa el coordinador al cambiar de página).</summary>
    public void RaisePageFlags()
    {
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsChartPage));
        OnPropertyChanged(nameof(IsFormPage));
        OnPropertyChanged(nameof(IsConsultationPage));
        OnPropertyChanged(nameof(ShowsPatientsTopBar));
    }

    [RelayCommand]
    public void GoToPatientsHome()
    {
        PatientsPage = PageHome;
        FocusSearchRequested?.Invoke();
    }

    /// <summary>Pide el foco en el buscador sin cambiar de página (la X que borra la búsqueda).</summary>
    public void RequestSearchFocus() => FocusSearchRequested?.Invoke();
}
