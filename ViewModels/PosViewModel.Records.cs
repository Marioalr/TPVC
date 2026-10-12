using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;

namespace TPVC.ViewModels;

/// <summary>Corte de caja del día e historial de tickets (reimprimir y cancelar con devolución).</summary>
public partial class PosViewModel
{
    // ================================================================== corte de caja

    [ObservableProperty]
    private bool _isCashCutModalOpen;

    [ObservableProperty]
    private DailyCashCut? _currentCashCut;

    [ObservableProperty]
    private DateTime _cashCutDate = DateTime.Today;

    [ObservableProperty]
    private string? _cashCutStatusMessage;

    [RelayCommand]
    public async Task OpenCashCutModalAsync()
    {
        CashCutDate = DateTime.Today;
        CashCutStatusMessage = null;
        await LoadCashCutForDateAsync();
        IsCashCutModalOpen = true;
    }

    [RelayCommand]
    public void CloseCashCutModal()
    {
        IsCashCutModalOpen = false;
        CurrentCashCut = null;
        CashCutStatusMessage = null;
        FocusSearchRequested?.Invoke();
    }

    public async Task LoadCashCutForDateAsync()
    {
        CurrentCashCut = await _inventoryService.GetDailyCashCutAsync(CashCutDate, _clinicProfileService?.CurrentProfile);
        CashCutStatusMessage = null;
    }

    [RelayCommand]
    public Task PreviousCashCutDayAsync() => ShowCashCutDayAsync(CashCutDate.AddDays(-1));

    [RelayCommand]
    public Task NextCashCutDayAsync() => ShowCashCutDayAsync(CashCutDate.AddDays(1));

    [RelayCommand]
    public Task TodayCashCutAsync() => ShowCashCutDayAsync(DateTime.Today);

    private async Task ShowCashCutDayAsync(DateTime date)
    {
        if (date.Date > DateTime.Today) return;
        CashCutDate = date.Date;
        await LoadCashCutForDateAsync();
    }

    public bool CanGoToNextCashCutDay => CashCutDate.Date < DateTime.Today;
    public bool IsCashCutToday => CashCutDate.Date == DateTime.Today;

    partial void OnCashCutDateChanged(DateTime value)
    {
        OnPropertyChanged(nameof(CanGoToNextCashCutDay));
        OnPropertyChanged(nameof(IsCashCutToday));
    }

    [RelayCommand]
    public void PrintCashCut()
    {
        if (CurrentCashCut == null) return;
        if (_printingService != null)
        {
            bool printed = _printingService.PrintCashCut(CurrentCashCut, _clinicProfileService?.CurrentProfile ?? new ClinicProfile());
            CashCutStatusMessage = printed ? "Corte enviado a la impresora." : "Impresión cancelada.";
        }
        else
        {
            CashCutStatusMessage = "Servicio de impresión no disponible en este equipo.";
        }
    }

    [RelayCommand]
    public async Task ExportCashCutCsvAsync()
    {
        if (CurrentCashCut == null) return;
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) desktop = Path.GetTempPath();
            string filePath = Path.Combine(desktop, $"Corte_Caja_{CurrentCashCut.Date:yyyyMMdd_HHmmss}.csv");
            await File.WriteAllTextAsync(filePath, _inventoryService.ExportDailyCashCutToCsv(CurrentCashCut), System.Text.Encoding.UTF8);
            CashCutStatusMessage = $"Corte exportado en: {filePath}";
        }
        catch (Exception ex)
        {
            CashCutStatusMessage = $"No se pudo exportar el corte: {ex.Message}";
        }
    }

    // ================================================================== historial de tickets

    [ObservableProperty]
    private bool _isTicketHistoryModalOpen;

    [ObservableProperty]
    private string _ticketSearchFolio = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Sale> _recentSales = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancelSelectedSale))]
    private Sale? _selectedSaleForDetail;

    public bool CanCancelSelectedSale => SelectedSaleForDetail != null && !SelectedSaleForDetail.IsCancelled;

    [ObservableProperty]
    private bool _isCancelConfirmModalOpen;

    [ObservableProperty]
    private string _cancellationReason = "Devolución de mercancía";

    [ObservableProperty]
    private string? _ticketHistoryStatusMessage;

    [RelayCommand]
    public async Task OpenTicketHistoryModalAsync()
    {
        TicketSearchFolio = string.Empty;
        TicketHistoryStatusMessage = null;
        IsCancelConfirmModalOpen = false;
        await LoadSalesHistoryAsync();
        IsTicketHistoryModalOpen = true;
    }

    [RelayCommand]
    public void CloseTicketHistoryModal()
    {
        IsTicketHistoryModalOpen = false;
        IsCancelConfirmModalOpen = false;
        TicketHistoryStatusMessage = null;
        SelectedSaleForDetail = null;
        FocusSearchRequested?.Invoke();
    }

    public async Task LoadSalesHistoryAsync()
    {
        RecentSales = new ObservableCollection<Sale>(await _inventoryService.GetSalesHistoryAsync(limit: 50));
        SelectedSaleForDetail = RecentSales.FirstOrDefault();
    }

    [RelayCommand]
    public async Task SearchTicketByFolioAsync()
    {
        if (string.IsNullOrWhiteSpace(TicketSearchFolio))
        {
            TicketHistoryStatusMessage = null;
            await LoadSalesHistoryAsync();
            return;
        }

        var sale = await _inventoryService.GetSaleByFolioAsync(TicketSearchFolio.Trim());
        if (sale != null)
        {
            RecentSales = new ObservableCollection<Sale> { sale };
            SelectedSaleForDetail = sale;
            TicketHistoryStatusMessage = null;
        }
        else
        {
            TicketHistoryStatusMessage = $"No hay ningún ticket con el folio '{TicketSearchFolio.Trim()}'.";
        }
    }

    [RelayCommand]
    public void ReprintSelectedTicket()
    {
        if (SelectedSaleForDetail == null) return;

        CurrentReceipt = new SaleReceipt
        {
            Folio = SelectedSaleForDetail.Folio,
            OriginalFolio = SelectedSaleForDetail.OriginalFolio,
            Date = SelectedSaleForDetail.Date,
            PatientName = SelectedSaleForDetail.PatientName,
            PaymentMethod = SelectedSaleForDetail.PaymentMethod,
            Cashier = SelectedSaleForDetail.Cashier,
            Items = SelectedSaleForDetail.Items.Select(i => new SaleReceiptItem
            {
                Name = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };
        IsFreshSaleTicket = false;
        IsTicketModalOpen = true;
        TicketStatusMessage = null;
    }

    [RelayCommand]
    public void RequestCancelSale(Sale? sale = null)
    {
        if (sale != null) SelectedSaleForDetail = sale;
        if (SelectedSaleForDetail == null) return;
        if (SelectedSaleForDetail.IsCancelled)
        {
            TicketHistoryStatusMessage = "Este ticket ya está cancelado.";
            return;
        }

        CancellationReason = "Devolución de producto solicitada por el paciente";
        IsCancelConfirmModalOpen = true;
    }

    [RelayCommand]
    public void CloseCancelConfirmModal() => IsCancelConfirmModalOpen = false;

    [RelayCommand]
    public async Task ConfirmCancelSaleAsync()
    {
        if (SelectedSaleForDetail == null) return;
        var folio = SelectedSaleForDetail.Folio;
        try
        {
            if (await _inventoryService.CancelSaleAsync(SelectedSaleForDetail.Id, CancellationReason))
            {
                IsCancelConfirmModalOpen = false;
                TicketHistoryStatusMessage = null;
                await LoadSalesHistoryAsync();
                SelectedSaleForDetail = RecentSales.FirstOrDefault(s => s.Folio == folio) ?? SelectedSaleForDetail;
                await LoadCatalogAsync();
                ShowAlert($"Ticket {folio} cancelado: la mercancía volvió al inventario", true, toast: true);
            }
            else
            {
                TicketHistoryStatusMessage = "No se pudo cancelar el ticket.";
            }
        }
        catch (Exception ex)
        {
            TicketHistoryStatusMessage = $"No se pudo cancelar el ticket: {ex.Message}";
        }
    }
}
