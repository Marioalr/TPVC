using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels;

public partial class MainHubViewModel : ObservableObject
{
    private readonly Action<string> _navigate;
    private readonly InventoryService? _inventoryService;

    public Action? OnOpenAgendaRequested { get; set; }

    // ==========================================
    // ALERTAS DE INICIO DEL DÍA
    // ==========================================
    [ObservableProperty]
    private int _todayAppointmentsCount;

    [ObservableProperty]
    private int _lowStockCount;

    [ObservableProperty]
    private int _expiredBatchesCount;

    [ObservableProperty]
    private int _nearExpiryBatchesCount;

    /// <summary>Revisión 20: productos en negativo (se cobraron sin que alcanzaran): "revisar inventario".</summary>
    [ObservableProperty]
    private int _inventoryReviewCount;

    /// <summary>Un renglón por producto: "Paracetamol 500 mg: −3 · revisar inventario".</summary>
    [ObservableProperty]
    private string _inventoryReviewText = string.Empty;

    [ObservableProperty]
    private bool _hasAlerts;

    [ObservableProperty]
    private string _alertNotificationMessage = string.Empty;

    // ==========================================
    // LISTA DE COMPRAS SUGERIDA
    // ==========================================
    [ObservableProperty]
    private bool _isSuggestedPurchasesOpen;

    [ObservableProperty]
    private ObservableCollection<SuggestedPurchaseItem> _suggestedPurchases = new();

    [ObservableProperty]
    private decimal _totalSuggestedCost;

    [ObservableProperty]
    private int _totalSuggestedQuantity;

    [ObservableProperty]
    private string? _suggestedPurchasesStatusMessage;

    /// <summary>"≈ 3 sin costo: se estiman con el 65 % del precio de venta" (vacío si todos tienen costo).</summary>
    [ObservableProperty]
    private string _estimatedCostNote = string.Empty;

    private static readonly System.Globalization.CultureInfo Mexican = System.Globalization.CultureInfo.GetCultureInfo("es-MX");

    public MainHubViewModel(
        Action<string> navigate, 
        InventoryService? inventoryService = null)
    {
        _navigate = navigate;
        _inventoryService = inventoryService;

        if (_inventoryService != null)
        {
            _inventoryService.OnInventoryChanged += () => _ = RefreshAlertsAsync();
            _inventoryService.OnAppointmentsChanged += () => _ = RefreshAlertsAsync();
            _ = RefreshAlertsAsync();
        }
    }

    [RelayCommand]
    public void OpenNewSale()
    {
        _navigate("Pos");
    }

    [RelayCommand]
    public void OpenNewConsultation()
    {
        _navigate("Records");
    }

    [RelayCommand]
    public void OpenConfiguration()
    {
        _navigate("Config");
    }

    [RelayCommand]
    public async Task RefreshAlertsAsync()
    {
        if (_inventoryService == null) return;

        try
        {
            var summary = await _inventoryService.GetSystemAlertsSummaryAsync();
            TodayAppointmentsCount = summary.TodayAppointmentsCount;
            LowStockCount = summary.LowStockCount;
            ExpiredBatchesCount = summary.ExpiredBatchesCount;
            NearExpiryBatchesCount = summary.NearExpiryBatchesCount;
            InventoryReviewCount = summary.InventoryReviewCount;
            InventoryReviewText = string.Join(Environment.NewLine, summary.InventoryReviewLines);
            HasAlerts = summary.HasAlerts;

            var alerts = new StringBuilder();
            if (TodayAppointmentsCount > 0)
                alerts.Append($"📅 {TodayAppointmentsCount} cita{(TodayAppointmentsCount > 1 ? "s" : "")} programada{(TodayAppointmentsCount > 1 ? "s" : "")} hoy. ");
            if (LowStockCount > 0)
                alerts.Append($"⚠️ {LowStockCount} producto{(LowStockCount > 1 ? "s" : "")} con stock bajo. ");
            if (ExpiredBatchesCount > 0)
                alerts.Append($"⛔ {ExpiredBatchesCount} lote{(ExpiredBatchesCount > 1 ? "s" : "")} caducado{(ExpiredBatchesCount > 1 ? "s" : "")}. ");
            else if (NearExpiryBatchesCount > 0)
                alerts.Append($"⏳ {NearExpiryBatchesCount} lote{(NearExpiryBatchesCount > 1 ? "s" : "")} por caducar (próximos 30 días). ");
            if (InventoryReviewCount > 0)
                alerts.Append($"📦 {InventoryReviewCount} producto{(InventoryReviewCount > 1 ? "s" : "")} en negativo: revisar inventario. ");

            AlertNotificationMessage = alerts.ToString().Trim();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al refrescar alertas del sistema en MainHub", ex);
        }
    }

    [RelayCommand]
    public void NavigateToAppointments()
    {
        _navigate("Records");
        OnOpenAgendaRequested?.Invoke();
    }

    [RelayCommand]
    public void NavigateToLowStock()
    {
        _ = OpenSuggestedPurchasesAsync();
    }

    [RelayCommand]
    public void NavigateToBatches()
    {
        _navigate("Config");
    }

    /// <summary>Revisión 20: al inventario, donde un conteo o una entrada corrigen lo que quedó en negativo.</summary>
    [RelayCommand]
    public void NavigateToInventoryReview()
    {
        _navigate("Config");
    }

    [RelayCommand]
    public async Task OpenSuggestedPurchasesAsync()
    {
        if (_inventoryService == null) return;

        try
        {
            var items = await _inventoryService.GetSuggestedPurchaseListAsync();
            SuggestedPurchases = new ObservableCollection<SuggestedPurchaseItem>(items);
            TotalSuggestedCost = items.Sum(i => i.EstimatedTotalCost);
            TotalSuggestedQuantity = items.Sum(i => i.SuggestedQuantity);
            int estimated = items.Count(i => i.IsCostEstimated);
            EstimatedCostNote = estimated == 0
                ? string.Empty
                : $"≈ {estimated} sin costo capturado: se estiman con el {InventoryService.EstimatedCostShareOfPrice.ToString("P0", Mexican)} del precio de venta.";
            SuggestedPurchasesStatusMessage = null;
            IsSuggestedPurchasesOpen = true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error al cargar lista sugerida de compras", ex);
        }
    }

    [RelayCommand]
    public void CloseSuggestedPurchases()
    {
        IsSuggestedPurchasesOpen = false;
        SuggestedPurchasesStatusMessage = null;
    }

    [RelayCommand]
    public async Task ExportSuggestedPurchasesCsvAsync()
    {
        if (SuggestedPurchases.Count == 0)
        {
            SuggestedPurchasesStatusMessage = "No hay productos en la lista para exportar.";
            return;
        }

        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
            {
                desktop = Path.GetTempPath();
            }

            string filename = $"Lista_Compras_Sugerida_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string filePath = Path.Combine(desktop, filename);

            var sb = new StringBuilder();
            sb.AppendLine("Código,Producto,Genérico,Presentación,Categoría,Stock Actual,Stock Mínimo,Cantidad Sugerida,Costo Unitario,Total,Costo Estimado,Motivo");

            foreach (var item in SuggestedPurchases)
            {
                sb.AppendLine($"\"{item.ProductCode}\",\"{item.ProductName}\",\"{item.GenericName}\",\"{item.Presentation}\",\"{item.CategoryName}\",{item.CurrentStock},{item.MinStock},{item.SuggestedQuantity},{item.EstimatedUnitCost:F2},{item.EstimatedTotalCost:F2},{(item.IsCostEstimated ? "Sí" : "No")},\"{item.Reason}\"");
            }

            await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);
            SuggestedPurchasesStatusMessage = $"¡Exportado con éxito en su escritorio:\n{filename}";
        }
        catch (Exception ex)
        {
            SuggestedPurchasesStatusMessage = $"Error al exportar CSV: {ex.Message}";
        }
    }

    [RelayCommand]
    public void PrintSuggestedPurchases()
    {
        if (SuggestedPurchases.Count == 0) return;

        try
        {
            // Generar archivo de texto formateado para impresión o informe
            string tempFile = Path.Combine(Path.GetTempPath(), $"Lista_Compras_{DateTime.Now:yyyyMMdd}.txt");
            var sb = new StringBuilder();
            sb.AppendLine("==========================================================================");
            sb.AppendLine("                   LISTA SUGERIDA DE COMPRAS Y RESURTIDO                  ");
            sb.AppendLine("==========================================================================");
            sb.AppendLine($"Fecha: {DateTime.Now:dd/MM/yyyy HH:mm} | Total Productos a Comprar: {SuggestedPurchases.Count}");
            sb.AppendLine($"Inversión (al costo): ${TotalSuggestedCost:N2} MXN | Unidades Totales: {TotalSuggestedQuantity}");
            sb.AppendLine("--------------------------------------------------------------------------");
            sb.AppendLine(string.Format("{0,-12} {1,-26} {2,8} {3,8} {4,10} {5,12}", "Código", "Producto", "Actual", "Sugerido", "Costo U.", "Total"));
            sb.AppendLine("--------------------------------------------------------------------------");

            foreach (var p in SuggestedPurchases)
            {
                string name = p.ProductName.Length > 25 ? p.ProductName.Substring(0, 25) : p.ProductName;
                sb.AppendLine(string.Format("{0,-12} {1,-26} {2,8} {3,8} {4,10} {5,12}",
                    p.ProductCode, name, p.CurrentStock, p.SuggestedQuantity, p.UnitCostText, p.EstimatedTotalCost.ToString("C2", Mexican)));
            }

            if (!string.IsNullOrEmpty(EstimatedCostNote)) sb.AppendLine(EstimatedCostNote);
            sb.AppendLine("==========================================================================");
            File.WriteAllText(tempFile, sb.ToString(), Encoding.UTF8);

            SuggestedPurchasesStatusMessage = $"Informe listo para imprimir:\n{tempFile}";
        }
        catch (Exception ex)
        {
            SuggestedPurchasesStatusMessage = $"Error al imprimir: {ex.Message}";
        }
    }
}
