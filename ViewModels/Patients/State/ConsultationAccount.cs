using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;

namespace TPVC.ViewModels.Patients;

/// <summary>
/// Cuenta de la consulta (fase 6, paso 14): el honorario y lo que se carga durante la consulta (receta
/// que se surte aquí, insumos y servicios); todo se cobra al terminar. Los cargos van al servicio de
/// consultas y después le pide al coordinador que sincronice la consulta en curso, como siempre.
/// </summary>
public partial class ConsultationAccount : ObservableObject
{
    private readonly ConsultationService? _consultationService;
    private readonly ActiveConsultationSession _active;
    private readonly CatalogStore _catalog;
    private readonly InfoDialogState _info;
    private readonly IConsultationGate _gate;
    private readonly IPatientsWorkflow _workflow;

    public ConsultationAccount(
        ConsultationService? consultationService,
        ActiveConsultationSession active,
        CatalogStore catalog,
        InfoDialogState info,
        IConsultationGate gate,
        IPatientsWorkflow workflow)
    {
        _consultationService = consultationService;
        _active = active;
        _catalog = catalog;
        _info = info;
        _gate = gate;
        _workflow = workflow;
    }

    /// <summary>Honorario que usaba el equipo que envió la consulta (Id local).</summary>
    public int? FeeServiceOverrideId { get; set; }

    /// <summary>Servicio de "consulta" del catálogo que se cobra como honorario al terminar.</summary>
    public ClinicalService? ConsultationFeeService =>
        // Consulta recibida de otro equipo: el honorario que ya usaba aquel
        (FeeServiceOverrideId is int feeId ? _catalog.Services.FirstOrDefault(s => s.IsActive && s.Id == feeId) : null)
        ?? _catalog.Services.FirstOrDefault(s => s.IsActive && s.Code.Equals("SERV-001", StringComparison.OrdinalIgnoreCase))
        ?? _catalog.Services.FirstOrDefault(s => s.IsActive && s.Category.Equals("Consulta", StringComparison.OrdinalIgnoreCase))
        ?? _catalog.Services.FirstOrDefault(s => s.IsActive && s.Name.Contains("Consulta", StringComparison.OrdinalIgnoreCase));

    public string ConsultationFeeName => ConsultationFeeService?.Name ?? "Consulta Médica General";

    public decimal ConsultationFeeAmount => ConsultationFeeService?.Price ?? DefaultConsultationFee;

    private const decimal DefaultConsultationFee = 300.00m;

    /// <summary>Honorario + todo lo cargado durante la consulta (lo que se cobrará al terminar).</summary>
    public decimal ConsultationAccountTotal => ConsultationFeeAmount + _active.ActiveConsultationTotalAmount;

    /// <summary>Honorario y total de la cuenta (NotifyAccountChanged del coordinador, en este orden).</summary>
    public void RaiseAccountChanged()
    {
        OnPropertyChanged(nameof(ConsultationFeeService));
        OnPropertyChanged(nameof(ConsultationFeeName));
        OnPropertyChanged(nameof(ConsultationFeeAmount));
        OnPropertyChanged(nameof(ConsultationAccountTotal));
    }

    /// <summary>Id negativo estable para un cargo libre (servicio o insumo fuera de catálogo): se acumula si se repite.</summary>
    internal static int FreeChargeId(string name)
    {
        int hash = 17;
        foreach (var c in TextSearch.Normalize(name)) hash = unchecked(hash * 31 + c);
        return -(1_000_000 + (int)((uint)hash % 1_000_000));
    }

    private bool ChargeProduct(Product product, int quantity, string? displayName = null)
    {
        if (_consultationService?.CurrentConsultation == null || quantity <= 0) return false;
        _consultationService.AddCartItemToActiveConsultation(
            product.Id, product.Code, displayName ?? product.Name, product.GenericName, product.Presentation,
            product.Unit, product.Price, quantity);
        _workflow.SyncActiveConsultation();
        return true;
    }

    private bool ChargeFreeLine(string code, string name, string? category, string unit, decimal price, int quantity)
    {
        if (_consultationService?.CurrentConsultation == null || quantity <= 0 || price <= 0) return false;
        _consultationService.AddCartItemToActiveConsultation(
            FreeChargeId(name), string.IsNullOrWhiteSpace(code) ? "CARGO" : code, name, category, null, unit, price, quantity);
        _workflow.SyncActiveConsultation();
        return true;
    }

    /// <summary>Quita (o reduce) un cargo de la cuenta de la consulta.</summary>
    private void Uncharge(int productId, int quantity)
    {
        var consultation = _consultationService?.CurrentConsultation;
        var item = consultation?.Items.FirstOrDefault(i => i.ProductId == productId);
        if (consultation == null || item == null) return;

        if (item.Quantity > quantity) item.Quantity -= quantity;
        else consultation.Items.Remove(item);

        consultation.NotifyItemsChanged();
        _workflow.SyncActiveConsultation();
    }

    /// <summary>
    /// "Se surte aquí": un medicamento recetado con existencias se carga a la cuenta en automático.
    /// Devuelve false si no está en inventario o no alcanza (se receta, pero no se cobra).
    /// </summary>
    public bool ChargePrescription(PatientPrescriptionItem item, Product? knownProduct = null)
    {
        var product = knownProduct != null && CatalogStore.ProductMatchesName(knownProduct, item.MedicineName)
            ? knownProduct
            : _catalog.FindCatalogProduct(item.MedicineName);
        var qty = Math.Max(1, item.RecommendedQuantity);
        if (product == null || product.CurrentStock < qty) return false;
        return ChargeProduct(product, qty, item.MedicineName);
    }

    public void UnchargePrescription(PatientPrescriptionItem item)
    {
        var product = _catalog.FindCatalogProduct(item.MedicineName);
        if (product != null) Uncharge(product.Id, Math.Max(1, item.RecommendedQuantity));
    }

    public void ChargeSupply(PatientSupplyItem item, Product? knownProduct)
    {
        bool charged = ChargeSupplyUnits(item, knownProduct, Math.Max(1, item.Quantity));
        if (!charged)
        {
            _info.InfoModalMessage = $"'{item.SupplyName}' no está en el inventario ni tiene precio: se registró en el expediente, pero no se cargó a la cuenta.";
            _info.IsInfoModalVisible = true;
        }
    }

    public void ChargeService(PatientServiceItem item)
    {
        var service = _catalog.Services.FirstOrDefault(s => (item.ServiceId > 0 && s.Id == item.ServiceId) || s.Name.Equals(item.ServiceName, StringComparison.OrdinalIgnoreCase));
        if (service != null)
        {
            for (int i = 0; i < Math.Max(1, item.Quantity); i++) AddServiceToActiveConsultation(service);
            return;
        }

        if (!ChargeFreeLine(item.ServiceCode, item.ServiceName, $"Servicio Clínico • {item.Category}", "Servicio", item.Price, Math.Max(1, item.Quantity)))
        {
            _info.InfoModalMessage = $"'{item.ServiceName}' no tiene precio: se registró en el expediente, pero no se cargó a la cuenta.";
            _info.IsInfoModalVisible = true;
        }
    }

    /// <summary>
    /// Carga piezas de un insumo: como producto del inventario o, fuera de él, como cargo libre a su
    /// precio. Devuelve false si no hay consulta o el cargo libre no tiene precio.
    /// </summary>
    public bool ChargeSupplyUnits(PatientSupplyItem item, Product? knownProduct, int units)
    {
        var product = knownProduct ?? _catalog.FindCatalogProduct(item.SupplyName);
        return product != null
            ? ChargeProduct(product, units, item.SupplyName)
            : ChargeFreeLine("INSUMO", item.SupplyName, "Material clínico", "Piezas", item.UnitPrice, units);
    }

    /// <summary>Quitar el insumo de la hoja lo quita de la cuenta tal como se cargó (producto o cargo libre).</summary>
    public void UnchargeSupply(PatientSupplyItem item) => UnchargeSupplyUnits(item, Math.Max(1, item.Quantity));

    /// <summary>Descuenta piezas de un insumo de la cuenta, del mismo renglón con que se cargaron.</summary>
    public void UnchargeSupplyUnits(PatientSupplyItem item, int units)
    {
        var product = _catalog.FindCatalogProduct(item.SupplyName);
        Uncharge(product?.Id ?? FreeChargeId(item.SupplyName), units);
    }

    /// <summary>
    /// Quitar el servicio de la hoja lo quita de la cuenta tal como se cargó: por cada unidad, su
    /// renglón y sus insumos por defecto (AddServiceToActiveConsultation), o su cargo libre.
    /// </summary>
    public void UnchargeService(PatientServiceItem item)
    {
        var units = Math.Max(1, item.Quantity);
        var service = _catalog.Services.FirstOrDefault(s => (item.ServiceId > 0 && s.Id == item.ServiceId) || s.Name.Equals(item.ServiceName, StringComparison.OrdinalIgnoreCase));
        if (service == null)
        {
            Uncharge(FreeChargeId(item.ServiceName), units);
            return;
        }

        Uncharge(-service.Id, units);
        foreach (var dp in service.DefaultProducts)
        {
            if (dp.Product != null) Uncharge(dp.Product.Id, Math.Max(1, dp.Quantity) * units);
        }
    }

    [RelayCommand]
    public void AddServiceToActiveConsultation(ClinicalService? service)
    {
        if (service == null || _gate.BlockIfConsultationReadOnly() || _gate.BlockIfReceivingHandoff()) return;

        if (!_active.HasActiveConsultation)
        {
            _workflow.StartActiveConsultation();
        }

        if (_consultationService?.CurrentConsultation != null)
        {
            _consultationService.AddCartItemToActiveConsultation(
                -service.Id,
                service.Code,
                service.Name,
                $"Servicio Clínico • {service.Category}",
                $"Duración: {service.EstimatedDurationMinutes} min",
                "Servicio",
                service.Price,
                1
            );

            // Agregar también los insumos por defecto del servicio si existen
            foreach (var dp in service.DefaultProducts)
            {
                if (dp.Product != null)
                {
                    _consultationService.AddCartItemToActiveConsultation(
                        dp.Product.Id,
                        dp.Product.Code,
                        dp.Product.Name,
                        dp.Product.GenericName,
                        dp.Product.Presentation,
                        dp.Product.Unit,
                        dp.Product.Price,
                        Math.Max(1, dp.Quantity)
                    );
                }
            }

            _workflow.SyncActiveConsultation();
            // El cargo aparece al instante en "Cuenta de la consulta": sin ventana que interrumpa
        }
    }

    [RelayCommand]
    public void RemoveItemFromActiveConsultation(ConsultationProductItem? item)
    {
        if (_consultationService?.CurrentConsultation == null || item == null || _gate.BlockIfConsultationReadOnly()) return;

        _consultationService.CurrentConsultation.Items.Remove(item);
        _consultationService.CurrentConsultation.NotifyItemsChanged();
        _workflow.SyncActiveConsultation();
    }
}
