using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.ViewModels;

/// <summary>Un renglón de «Por sección» con la marca del tipo elegido (un clic filtra el resto del panel).</summary>
public sealed class BusinessTypeRowItem(BusinessTypeRow row, bool isSelected)
{
    public BusinessTypeRow Row { get; } = row;
    public bool IsSelected { get; } = isSelected;
}

/// <summary>Una barra de «En el tiempo» con sus alturas ya calculadas.</summary>
public sealed class BusinessChartBar
{
    public required BusinessTimePoint Point { get; init; }
    public double SalesHeight { get; init; }
    public double ProfitHeight { get; init; }
    public bool ProfitIsNegative { get; init; }
    public bool ShowLabel { get; init; }
    public string ToolTip { get; init; } = string.Empty;
}

/// <summary>
/// Panel «Negocio» (pedido del médico del 2026-10-22): ventas, costo, utilidad y margen por periodo y por tipo, los más
/// vendidos, la gráfica en el tiempo, las compras, el consumo de insumos y el valor del inventario, con exportación a CSV. Lee
/// con <see cref="IBusinessReportQueries"/> (asíncrono: la pantalla no se bloquea) y todas las cuentas son las reglas puras
/// de <see cref="BusinessReportRules"/> y <see cref="BusinessStockRules"/>.
/// </summary>
public partial class BusinessViewModel : ObservableObject
{
    public const string AllTypes = "Todo";
    public const string AllProducts = "Todos";
    public const string ByQuantity = "Por cantidad";
    public const string ByProfit = "Por utilidad";

    /// <summary>Alto de la gráfica en píxeles (la barra más alta).</summary>
    public const double ChartHeight = 140;

    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    private readonly IBusinessReportQueries _queries;
    private readonly ClinicProfileService? _profile;
    private BusinessReportData _data = new();
    private int _loadVersion;

    public BusinessViewModel(IBusinessReportQueries queries, ClinicProfileService? profile = null)
    {
        _queries = queries;
        _profile = profile;
    }

    private static string DateText(DateTime date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>El panel se muestra en este equipo (<see cref="DevicePermissions.CanViewBusinessReports"/>; la PC siempre).</summary>
    public static bool IsAllowedOn(DeviceRole role) => DevicePermissions.CanViewBusinessReports(role);

    // ================================================================== filtros

    public IReadOnlyList<string> PeriodOptions { get; } = Enum.GetValues<BusinessPeriodKind>().Select(BusinessReportRules.PeriodName).ToList();

    public IReadOnlyList<string> TypeOptions { get; } = new[] { AllTypes }.Concat(Enum.GetValues<BusinessLineType>().Select(BusinessReportRules.TypeName)).ToList();

    public IReadOnlyList<string> TopProductsOptions { get; } = new[]
    {
        BusinessReportRules.TypeName(BusinessLineType.Medicamentos), BusinessReportRules.TypeName(BusinessLineType.Insumos), AllProducts
    };

    public IReadOnlyList<string> RankingOptions { get; } = new[] { ByQuantity, ByProfit };

    /// <summary>Este mes al abrir.</summary>
    [ObservableProperty]
    private string _periodName = BusinessReportRules.PeriodName(BusinessPeriodKind.EsteMes);

    [ObservableProperty]
    private string _typeName = AllTypes;

    /// <summary>Más vendidos: medicamentos por omisión, como lo pidió el médico.</summary>
    [ObservableProperty]
    private string _topProductsName = BusinessReportRules.TypeName(BusinessLineType.Medicamentos);

    [ObservableProperty]
    private string _rankingName = ByQuantity;

    /// <summary>El rango personalizado empieza con este mes: del día 1 a hoy.</summary>
    [ObservableProperty]
    private string _customFromText = DateText(new DateTime(ClinicClock.Today.Year, ClinicClock.Today.Month, 1));

    [ObservableProperty]
    private string _customToText = DateText(ClinicClock.Today);

    [ObservableProperty]
    private string? _customRangeError;

    public BusinessPeriodKind PeriodKind =>
        Enum.GetValues<BusinessPeriodKind>().FirstOrDefault(k => BusinessReportRules.PeriodName(k) == PeriodName);

    public bool IsCustomPeriod => PeriodKind == BusinessPeriodKind.Personalizado;

    public BusinessLineType? TypeFilter => BusinessReportRules.TypeFromName(TypeName);

    partial void OnPeriodNameChanged(string value)
    {
        OnPropertyChanged(nameof(PeriodKind));
        OnPropertyChanged(nameof(IsCustomPeriod));
        if (!IsCustomPeriod) _ = LoadAsync();
    }

    partial void OnTypeNameChanged(string value)
    {
        OnPropertyChanged(nameof(TypeFilter));
        Recompute();
    }

    partial void OnTopProductsNameChanged(string value) => Recompute();

    partial void OnRankingNameChanged(string value) => Recompute();

    /// <summary>Un clic en un renglón de «Por sección» filtra el resto por ese tipo; otro clic en el mismo regresa a «Todo».</summary>
    [RelayCommand]
    public void SelectType(string? name) => TypeName = string.IsNullOrEmpty(name) || name == TypeName ? AllTypes : name;

    /// <summary>«Aplicar» del rango personalizado: las dos fechas dd/mm/aaaa (al revés se voltean).</summary>
    [RelayCommand]
    public async Task ApplyCustomRangeAsync()
    {
        var from = BusinessReportRules.ParseDate(CustomFromText);
        var to = BusinessReportRules.ParseDate(CustomToText);
        if (from == null || to == null)
        {
            CustomRangeError = "Escriba las dos fechas completas (dd/mm/aaaa).";
            return;
        }
        CustomRangeError = null;
        await LoadAsync();
    }

    // ================================================================== estado

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private BusinessPeriod _period = new(DateTime.Today, DateTime.Today.AddDays(1));

    [ObservableProperty]
    private BusinessPeriod _previousPeriod = new(DateTime.Today.AddDays(-1), DateTime.Today);

    public string PeriodText => BusinessReportRules.PeriodText(Period);

    public string ComparisonText => $"Comparado con {BusinessReportRules.PeriodText(PreviousPeriod)}";

    partial void OnPeriodChanged(BusinessPeriod value) => OnPropertyChanged(nameof(PeriodText));

    partial void OnPreviousPeriodChanged(BusinessPeriod value) => OnPropertyChanged(nameof(ComparisonText));

    // ================================================================== resultados

    [ObservableProperty]
    private BusinessSummary _summary = BusinessSummary.Empty;

    [ObservableProperty]
    private BusinessComparison _comparison = new(null, null, null);

    [ObservableProperty]
    private ObservableCollection<BusinessTypeRowItem> _byType = new();

    [ObservableProperty]
    private ObservableCollection<BusinessTopProduct> _topProducts = new();

    [ObservableProperty]
    private ObservableCollection<BusinessTopService> _topServices = new();

    [ObservableProperty]
    private ObservableCollection<BusinessChartBar> _chartBars = new();

    [ObservableProperty]
    private BusinessPurchases _purchases = BusinessPurchases.Empty;

    [ObservableProperty]
    private BusinessConsumption _consumption = BusinessConsumption.Empty;

    [ObservableProperty]
    private decimal _inventoryValue;

    [ObservableProperty]
    private string _inventoryValueDetail = InventoryValueRules.Detail(0);

    [ObservableProperty]
    private string _chartScaleText = string.Empty;

    [ObservableProperty]
    private string _topProductsTitle = string.Empty;

    public string SalesChangeText => BusinessReportRules.ChangeText(Comparison.SalesChangePercent);
    public string ProfitChangeText => BusinessReportRules.ChangeText(Comparison.ProfitChangePercent);
    public string TicketsChangeText => BusinessReportRules.ChangeText(Comparison.TicketsChangePercent);

    public string CostNote => BusinessReportRules.EstimatedCostNote(Summary.EstimatedCostLines, Summary.MissingCostLines);
    public bool HasCostNote => CostNote.Length > 0;

    public string PurchasesCostNote => BusinessReportRules.EstimatedCostNote(Purchases.EstimatedCostLines, Purchases.MissingCostLines);
    public string ConsumptionCostNote => BusinessReportRules.EstimatedCostNote(Consumption.EstimatedCostLines, Consumption.MissingCostLines);

    public bool HasNoSales => Summary.Tickets == 0;
    public bool HasNoTopProducts => TopProducts.Count == 0;
    public bool HasNoTopServices => TopServices.Count == 0;
    public bool HasNoPurchases => Purchases.Entries == 0;
    public bool HasNoConsumption => Consumption.Rows.Count == 0;

    /// <summary>Con un tipo de producto en el filtro, «Más vendidos» ya es de ese tipo y no ofrece cambiarlo.</summary>
    public bool CanChooseTopProducts => TypeFilter is not (BusinessLineType.Medicamentos or BusinessLineType.Insumos or BusinessLineType.Otros);

    partial void OnSummaryChanged(BusinessSummary value)
    {
        OnPropertyChanged(nameof(CostNote));
        OnPropertyChanged(nameof(HasCostNote));
        OnPropertyChanged(nameof(HasNoSales));
    }

    partial void OnComparisonChanged(BusinessComparison value)
    {
        OnPropertyChanged(nameof(SalesChangeText));
        OnPropertyChanged(nameof(ProfitChangeText));
        OnPropertyChanged(nameof(TicketsChangeText));
    }

    partial void OnPurchasesChanged(BusinessPurchases value)
    {
        OnPropertyChanged(nameof(PurchasesCostNote));
        OnPropertyChanged(nameof(HasNoPurchases));
    }

    partial void OnConsumptionChanged(BusinessConsumption value)
    {
        OnPropertyChanged(nameof(ConsumptionCostNote));
        OnPropertyChanged(nameof(HasNoConsumption));
    }

    // ================================================================== carga

    /// <summary>
    /// Lee el periodo elegido y el anterior igual (una sola lectura de la base, asíncrona) y recalcula todo. Si mientras tanto se
    /// pidió otra carga, el resultado viejo se descarta.
    /// </summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        int version = ++_loadVersion;
        var today = ClinicClock.Today;
        var kind = PeriodKind;
        var period = BusinessReportRules.PeriodFor(kind, today, BusinessReportRules.ParseDate(CustomFromText), BusinessReportRules.ParseDate(CustomToText));
        var previous = BusinessReportRules.PreviousPeriod(kind, period);

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var from = previous.From < period.From ? previous.From : period.From;
            var to = previous.To > period.To ? previous.To : period.To;
            var data = await _queries.LoadBusinessReportAsync(from, to);
            if (version != _loadVersion) return;
            _data = data;
            Period = period;
            PreviousPeriod = previous;
            Recompute();
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            AppLogger.Error("No se pudo cargar el panel del negocio", ex);
            ErrorMessage = $"No se pudo leer la información del negocio: {ex.Message}";
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    /// <summary>Recalcula con lo ya leído (cambiar el tipo o los más vendidos no vuelve a leer la base).</summary>
    public void Recompute()
    {
        var type = TypeFilter;
        var allLines = BusinessReportRules.Lines(_data.SaleLines, _data.Products, _data.Services, Period);
        var lines = type == null ? allLines : allLines.Where(l => l.Type == type).ToList();
        var previousLines = BusinessReportRules.Lines(_data.SaleLines, _data.Products, _data.Services, PreviousPeriod, type);

        Summary = BusinessReportRules.Summarize(lines);
        Comparison = BusinessReportRules.Compare(Summary, BusinessReportRules.Summarize(previousLines));
        ByType = new ObservableCollection<BusinessTypeRowItem>(BusinessReportRules.ByType(allLines).Select(r => new BusinessTypeRowItem(r, r.Type == type)));

        var productType = TopProductsType();
        var ranking = RankingName == ByProfit ? BusinessRanking.Profit : BusinessRanking.Quantity;
        TopProducts = new ObservableCollection<BusinessTopProduct>(BusinessReportRules.TopProducts(lines, productType, ranking));
        TopProductsTitle = (productType is { } t ? BusinessReportRules.TypeName(t) : "Todos los productos") + (ranking == BusinessRanking.Profit ? " · por utilidad" : " · por cantidad");
        TopServices = new ObservableCollection<BusinessTopService>(BusinessReportRules.TopServices(lines));

        BuildChart(BusinessReportRules.Timeline(lines, Period));

        Purchases = BusinessStockRules.Purchases(_data.Movements, _data.Products, Period, type);
        Consumption = BusinessStockRules.SupplyConsumption(_data.Movements, allLines, _data.Products, Period, type);

        var (value, withoutCost) = InventoryValueRules.Summarize(_data.Products);
        InventoryValue = value;
        InventoryValueDetail = InventoryValueRules.Detail(withoutCost);
        _stockedWithoutCost = withoutCost;

        OnPropertyChanged(nameof(HasNoTopProducts));
        OnPropertyChanged(nameof(HasNoTopServices));
        OnPropertyChanged(nameof(CanChooseTopProducts));
    }

    private int _stockedWithoutCost;

    /// <summary>El tipo de «Más vendidos»: el del filtro si es de productos; si no, el elegido (Medicamentos, Insumos o Todos).</summary>
    private BusinessLineType? TopProductsType() =>
        TypeFilter is BusinessLineType.Medicamentos or BusinessLineType.Insumos or BusinessLineType.Otros
            ? TypeFilter
            : BusinessReportRules.TypeFromName(TopProductsName);

    private void BuildChart(IReadOnlyList<BusinessTimePoint> points)
    {
        decimal max = points.Count == 0 ? 0m : points.Max(p => Math.Max(p.Sales, Math.Abs(p.Profit)));
        int step = Math.Max(1, (int)Math.Ceiling(points.Count / 12.0));
        double Height(decimal amount) => max <= 0 ? 0 : (double)(Math.Abs(amount) / max) * ChartHeight;
        ChartBars = new ObservableCollection<BusinessChartBar>(points.Select((p, i) => new BusinessChartBar
        {
            Point = p,
            SalesHeight = Height(p.Sales),
            ProfitHeight = Height(p.Profit),
            ProfitIsNegative = p.Profit < 0,
            ShowLabel = i % step == 0,
            ToolTip = $"{p.Label}: ventas {p.Sales.ToString("C2", Mexican)} · utilidad {p.Profit.ToString("C2", Mexican)}"
        }));
        ChartScaleText = max > 0 ? $"La barra más alta: {max.ToString("C0", Mexican)}" : "Sin ventas en el periodo";
    }

    // ================================================================== exportar

    /// <summary>Carpeta del CSV: el escritorio (o la carpeta temporal si no hay); las pruebas ponen otra.</summary>
    public string? ExportFolder { get; set; }

    /// <summary>
    /// «Exportar CSV»: lo que se ve (periodo, filtro, resumen, secciones, más vendidos, la gráfica, compras, consumo y valor del
    /// inventario) en Negocio_aaaaMMdd_HHmmss.csv, UTF-8 con BOM para Excel, como la compra sugerida.
    /// </summary>
    [RelayCommand]
    public async Task ExportCsvAsync()
    {
        try
        {
            var folder = ExportFolder;
            if (string.IsNullOrEmpty(folder))
            {
                folder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = Path.GetTempPath();
            }
            var now = ClinicClock.Now;
            var filename = $"Negocio_{now:yyyyMMdd_HHmmss}.csv";
            var path = Path.Combine(folder, filename);
            await File.WriteAllTextAsync(path, BuildCsv(now), Encoding.UTF8);
            StatusMessage = $"Exportado en {path}";
        }
        catch (Exception ex)
        {
            AppLogger.Error("No se pudo exportar el panel del negocio", ex);
            StatusMessage = $"No se pudo exportar el CSV: {ex.Message}";
        }
    }

    /// <summary>El CSV de la vista actual (<see cref="BusinessReportRules.ToCsv"/>).</summary>
    public string BuildCsv(DateTime generatedAt)
    {
        var clinic = _profile?.CurrentProfile.ClinicName;
        return BusinessReportRules.ToCsv(new BusinessCsvReport(
            string.IsNullOrWhiteSpace(clinic) ? "Consultorio Médico" : clinic,
            PeriodName,
            Period,
            TypeFilter,
            generatedAt,
            Summary,
            Comparison,
            ByType.Select(r => r.Row).ToList(),
            TopProductsTitle,
            TopProducts.ToList(),
            TopServices.ToList(),
            ChartBars.Select(b => b.Point).ToList(),
            Purchases,
            Consumption,
            InventoryValue,
            _stockedWithoutCost));
    }

    /// <summary>El resumen de una línea del menú de Configuración.</summary>
    public string MenuSummary => Summary.Tickets == 0
        ? "Ventas, utilidad y compras"
        : $"{PeriodName}: utilidad {Summary.Profit.ToString("C0", Mexican)}";
}
