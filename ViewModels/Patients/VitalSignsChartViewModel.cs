using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Evolución de signos vitales de la ficha (fase 6, paso 3): últimos valores, gráfica y tabla. El
/// coordinador la recalcula al cambiar de paciente y al guardar o descartar una consulta; cuando
/// cambia la serie de puntos le avisa, porque "Lo esencial" de la ficha se arma con ella.
/// </summary>
public partial class VitalSignsChartViewModel : ObservableObject
{
    private readonly IPatientsHubs _hubs;

    public VitalSignsChartViewModel(IPatientsHubs hubs)
    {
        _hubs = hubs;
    }

    [ObservableProperty]
    private ObservableCollection<VitalSignsEvolutionPoint> _vitalSignsEvolutionPoints = new();

    /// <summary>"Lo esencial" de la ficha se arma con estos puntos: el coordinador lo rehace aquí, antes del aviso, como siempre.</summary>
    partial void OnVitalSignsEvolutionPointsChanged(ObservableCollection<VitalSignsEvolutionPoint> value) => _hubs.OnVitalSignsEvolutionPointsChanged(value);

    [ObservableProperty]
    private string _vitalSignsFilter = "Todos"; // "Todos", "Peso", "Presion", "Glucosa"

    [ObservableProperty]
    private string _weightPolylinePoints = string.Empty;

    [ObservableProperty]
    private string _systolicPolylinePoints = string.Empty;

    [ObservableProperty]
    private string _diastolicPolylinePoints = string.Empty;

    [ObservableProperty]
    private string _glucosePolylinePoints = string.Empty;

    [ObservableProperty]
    private ObservableCollection<VitalSignsChartPoint> _chartPoints = new();

    [ObservableProperty]
    private bool _hasEnoughPointsForChart;

    [ObservableProperty]
    private string _latestWeightFormatted = "--";

    [ObservableProperty]
    private string _latestBPFormatted = "--";

    [ObservableProperty]
    private string _latestGlucoseFormatted = "--";

    [ObservableProperty]
    private string _latestIMCFormatted = "--";

    [RelayCommand]
    public void SetVitalSignsFilter(string filter)
    {
        VitalSignsFilter = filter;
        RecalculateChartPoints();
    }

    /// <summary>Filtros de la gráfica como chips (con acentos para leerse bien).</summary>
    public IReadOnlyList<string> VitalSignsFilterOptions { get; } = new[] { "Todos", "Peso", "Presión", "Glucosa" };

    public string VitalSignsFilterLabel
    {
        get => VitalSignsFilter == "Presion" ? "Presión" : VitalSignsFilter;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            SetVitalSignsFilter(value == "Presión" ? "Presion" : value);
        }
    }

    partial void OnVitalSignsFilterChanged(string value) => OnPropertyChanged(nameof(VitalSignsFilterLabel));

    public void RefreshVitalSignsEvolution(PatientRecord? patient)
    {
        var pat = patient;
        if (pat == null)
        {
            // Colección nueva (no Clear): así "Lo esencial" (LatestVitals) también queda vacío
            VitalSignsEvolutionPoints = new ObservableCollection<VitalSignsEvolutionPoint>();
            ChartPoints.Clear();
            HasEnoughPointsForChart = false;
            LatestWeightFormatted = "--";
            LatestBPFormatted = "--";
            LatestGlucoseFormatted = "--";
            LatestIMCFormatted = "--";
            WeightPolylinePoints = string.Empty;
            SystolicPolylinePoints = string.Empty;
            DiastolicPolylinePoints = string.Empty;
            GlucosePolylinePoints = string.Empty;
            return;
        }

        var points = pat.GetVitalSignsEvolution();
        VitalSignsEvolutionPoints = new ObservableCollection<VitalSignsEvolutionPoint>(points);

        var latest = points.LastOrDefault();
        if (latest != null)
        {
            LatestWeightFormatted = latest.Weight.HasValue ? $"{latest.Weight.Value:F1} kg" : "--";
            LatestBPFormatted = (latest.Systolic.HasValue && latest.Diastolic.HasValue) ? $"{latest.Systolic.Value}/{latest.Diastolic.Value} mmHg" : "--";
            LatestGlucoseFormatted = latest.Glucose.HasValue ? $"{latest.Glucose.Value:F0} mg/dL" : "--";
            LatestIMCFormatted = latest.BMI.HasValue ? $"{latest.BMI.Value:F1}" : "--";
        }
        else
        {
            LatestWeightFormatted = "--";
            LatestBPFormatted = "--";
            LatestGlucoseFormatted = "--";
            LatestIMCFormatted = "--";
        }

        RecalculateChartPoints();
    }

    private const int MaxChartMarkersPerSeries = 60;

    private void RecalculateChartPoints()
    {
        var points = VitalSignsEvolutionPoints.ToList();
        if (points.Count == 0)
        {
            HasEnoughPointsForChart = false;
            WeightPolylinePoints = string.Empty;
            SystolicPolylinePoints = string.Empty;
            DiastolicPolylinePoints = string.Empty;
            GlucosePolylinePoints = string.Empty;
            ChartPoints.Clear();
            return;
        }

        HasEnoughPointsForChart = true;

        double width = 640;
        double height = 200;
        double padLeft = 40;
        double padRight = 30;
        double padTop = 20;
        double padBottom = 30;

        double plotW = width - padLeft - padRight;
        double plotH = height - padTop - padBottom;

        int n = points.Count;
        double stepX = n > 1 ? plotW / (n - 1) : plotW / 2;

        var chartPts = new ObservableCollection<VitalSignsChartPoint>();
        // Con años de consultas los puntos quedan encimados (y cada uno es un elemento en pantalla):
        // las líneas usan todas las mediciones, pero se marcan a lo más ~60 por serie, repartidas
        int markerStep = Math.Max(1, (int)Math.Ceiling(n / (double)MaxChartMarkersPerSeries));

        var weightPoly = new StringBuilder();
        var sysPoly = new StringBuilder();
        var diaPoly = new StringBuilder();
        var gluPoly = new StringBuilder();

        for (int i = 0; i < n; i++)
        {
            var p = points[i];
            double x = n > 1 ? padLeft + (i * stepX) : padLeft + stepX;
            bool marker = i % markerStep == 0 || i == n - 1;

            // Peso: escala 30..150 kg
            if (p.Weight.HasValue)
            {
                double normW = (double)Math.Clamp(p.Weight.Value, 30m, 150m);
                double yW = padTop + plotH - ((normW - 30.0) / 120.0 * plotH);
                weightPoly.Append(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", x, yW));
                if (marker && (VitalSignsFilter == "Todos" || VitalSignsFilter == "Peso"))
                {
                    chartPts.Add(new VitalSignsChartPoint
                    {
                        X = x,
                        Y = yW,
                        ValueLabel = $"{p.Weight.Value:F1} kg",
                        SeriesName = "Peso"
                    });
                }
            }

            // Presión sistólica: escala 60..220 mmHg
            if (p.Systolic.HasValue)
            {
                double normSys = (double)Math.Clamp(p.Systolic.Value, 60m, 220m);
                double ySys = padTop + plotH - ((normSys - 60.0) / 160.0 * plotH);
                sysPoly.Append(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", x, ySys));
                if (marker && (VitalSignsFilter == "Todos" || VitalSignsFilter == "Presion"))
                {
                    chartPts.Add(new VitalSignsChartPoint
                    {
                        X = x,
                        Y = ySys,
                        ValueLabel = $"{p.Systolic.Value} Sis",
                        SeriesName = "Presión Sistólica"
                    });
                }
            }

            // Presión diastólica: escala 40..130 mmHg
            if (p.Diastolic.HasValue)
            {
                double normDia = (double)Math.Clamp(p.Diastolic.Value, 40m, 130m);
                double yDia = padTop + plotH - ((normDia - 40.0) / 90.0 * plotH);
                diaPoly.Append(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", x, yDia));
                if (marker && (VitalSignsFilter == "Todos" || VitalSignsFilter == "Presion"))
                {
                    chartPts.Add(new VitalSignsChartPoint
                    {
                        X = x,
                        Y = yDia,
                        ValueLabel = $"{p.Diastolic.Value} Dia",
                        SeriesName = "Presión Diastólica"
                    });
                }
            }

            // Glucosa: escala 50..300 mg/dL
            if (p.Glucose.HasValue)
            {
                double normGlu = (double)Math.Clamp(p.Glucose.Value, 50m, 300m);
                double yGlu = padTop + plotH - ((normGlu - 50.0) / 250.0 * plotH);
                gluPoly.Append(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", x, yGlu));
                if (marker && (VitalSignsFilter == "Todos" || VitalSignsFilter == "Glucosa"))
                {
                    chartPts.Add(new VitalSignsChartPoint
                    {
                        X = x,
                        Y = yGlu,
                        ValueLabel = $"{p.Glucose.Value:F0} mg/dL",
                        SeriesName = "Glucosa"
                    });
                }
            }
        }

        WeightPolylinePoints = weightPoly.ToString().Trim();
        SystolicPolylinePoints = sysPoly.ToString().Trim();
        DiastolicPolylinePoints = diaPoly.ToString().Trim();
        GlucosePolylinePoints = gluPoly.ToString().Trim();
        ChartPoints = chartPts;
    }
}
