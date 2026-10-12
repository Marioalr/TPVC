using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

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
    internal static int FreeChargeId(string name) => TPVC.Services.Rules.HandoffPayloadCodec.FreeChargeId(name);

    private bool ChargeProduct(Product product, int quantity, string? displayName = null)
    {
        if (_consultationService?.CurrentConsultation == null || quantity <= 0) return false;
        _consultationService.AddCartItemToActiveConsultation(
            product.Id, product.Code, displayName ?? product.Name, product.GenericName, product.Presentation,
            product.Unit, product.Price, quantity);
        _workflow.SyncActiveConsultation();
        return true;
    }

    /// <param name="lineId">El renglón libre de la cuenta que ya tiene el insumo (paso 8.18: puede llevar otro nombre); sin él, el de su nombre.</param>
    private bool ChargeFreeLine(string code, string name, string? category, string unit, decimal price, int quantity, int? lineId = null)
    {
        if (_consultationService?.CurrentConsultation == null || quantity <= 0 || price <= 0) return false;
        _consultationService.AddCartItemToActiveConsultation(
            lineId ?? FreeChargeId(name), string.IsNullOrWhiteSpace(code) ? "CARGO" : code, name, category, null, unit, price, quantity);
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

    /// <summary>
    /// Carga el insumo recién agregado (todas sus piezas) y anota en el renglón con qué renglón de la
    /// cuenta se cargó: la clase y el SyncId del producto, que valen en cualquier equipo (paso 8.18), y el Id
    /// local, que leen las PC anteriores; false si no se pudo (sin consulta o sin precio).
    /// </summary>
    public bool ChargeSupply(PatientSupplyItem item, Product? knownProduct)
    {
        var product = knownProduct ?? _catalog.FindCatalogProduct(item.SupplyName);
        bool charged = ChargeSupplyUnits(item, product, Math.Max(1, item.Quantity));
        if (charged)
        {
            PatientSheetRows.SetSupplyLine(item, product != null
                ? PatientSheetRows.SupplyLine.ForProduct(product)
                : PatientSheetRows.SupplyLine.Free(item.SupplyName));
        }
        if (!charged)
        {
            _info.InfoModalMessage = $"'{item.SupplyName}' no está en el inventario ni tiene precio: se registró en el expediente, pero no se cargó a la cuenta.";
            _info.IsInfoModalVisible = true;
        }
        return charged;
    }

    /// <summary>
    /// Carga el servicio recién agregado (todas sus unidades, cada una con sus insumos por defecto, o su
    /// cargo libre); false si no se pudo (sin consulta, bloqueada o sin precio).
    /// </summary>
    public bool ChargeService(PatientServiceItem item)
    {
        var service = FindService(item);
        if (service != null)
        {
            for (int i = 0; i < Math.Max(1, item.Quantity); i++)
            {
                // Lo que impide cargar una unidad (sin consulta, en otro equipo) impide todas: se ve en la primera
                if (!TryAddServiceToActiveConsultation(service)) return false;
            }
            return true;
        }

        if (!ChargeFreeLine(item.ServiceCode, item.ServiceName, $"Servicio Clínico • {item.Category}", "Servicio", item.Price, Math.Max(1, item.Quantity)))
        {
            _info.InfoModalMessage = $"'{item.ServiceName}' no tiene precio: se registró en el expediente, pero no se cargó a la cuenta.";
            _info.IsInfoModalVisible = true;
            return false;
        }
        return true;
    }

    /// <summary>
    /// El servicio del catálogo de este equipo de un renglón de la hoja (paso 8.18): por su SyncId, fuera del catálogo
    /// si no tiene ni SyncId ni Id, por el Id viejo si el nombre coincide, o por nombre. null = cargo libre.
    /// </summary>
    private ClinicalService? FindService(PatientServiceItem item) => PatientSheetRows.ResolveService(item, _catalog.Services);

    /// <summary>
    /// Carga piezas de un insumo: como el producto dado o, sin él, como cargo libre a su precio (en
    /// <paramref name="freeLineId"/> si el renglón ya tiene uno, aunque se llame distinto).
    /// Devuelve false si no hay consulta o el cargo libre no tiene precio.
    /// </summary>
    public bool ChargeSupplyUnits(PatientSupplyItem item, Product? product, int units, int? freeLineId = null)
    {
        return product != null
            ? ChargeProduct(product, units, item.SupplyName)
            : ChargeFreeLine("INSUMO", item.SupplyName, "Material clínico", "Piezas", item.UnitPrice, units, freeLineId);
    }

    /// <summary>
    /// Renglón de la cuenta de un insumo en este equipo (paso 8.18): por el SyncId del producto con que se cargó, el
    /// cargo libre de su nombre, el Id anotado solo si ese producto tiene el mismo nombre (un renglón que cargó otro
    /// equipo trae los Id de aquel), o por nombre (renglones anteriores a v1.18.7). Antes, un renglón anterior al paso
    /// que cargó este mismo equipo (su Id tiene en esta cuenta un renglón con su nombre) es ese renglón, aunque el
    /// producto se haya renombrado o desactivado después.
    /// </summary>
    public PatientSheetRows.SupplyLine SupplyLine(PatientSupplyItem item)
    {
        var account = _consultationService?.CurrentConsultation?.Items;
        if (account != null && PatientSheetRows.AccountLegacySupplyLine(item, account, _catalog.ProductRefs) is { } own) return own;
        return PatientSheetRows.ResolveSupplyLine(item, _catalog.AvailableProducts, _catalog.ProductRefs);
    }

    /// <summary>El Id local del renglón de la cuenta de un insumo (<see cref="SupplyLine"/>).</summary>
    public int SupplyLineId(PatientSupplyItem item) => SupplyLine(item).LineId;

    /// <summary>
    /// Recibir una consulta de otro equipo también traduce las hojas (paso 8.18): los renglones de insumos y servicios
    /// que cargó esa consulta quedan con los Id de este equipo, los mismos de su cuenta ya traducida. Se guardan con la
    /// siguiente escritura del expediente.
    /// </summary>
    public int TranslateReceivedSheets(PatientRecord patient, ConsultationDraft received) =>
        PatientSheetRows.TranslateReceivedRows(patient, received.Folio, received.Items, _catalog.AvailableProducts, _catalog.ProductRefs, _catalog.Services);

    /// <summary>Descuenta piezas de un insumo de la cuenta, del mismo renglón con que se cargaron (producto o cargo libre).</summary>
    public void UnchargeSupplyUnits(PatientSupplyItem item, int units) => Uncharge(SupplyLineId(item), units);

    /// <summary>Piezas de un producto que ya están en la cuenta de la consulta (de cualquier hoja o servicio).</summary>
    public int ChargedProductUnits(int productId) =>
        _consultationService?.CurrentConsultation?.Items.FirstOrDefault(i => i.ProductId == productId)?.Quantity ?? 0;

    /// <summary>
    /// Descuenta unidades de un servicio de la cuenta tal como se cargaron: por cada una, su renglón y
    /// sus insumos por defecto (AddServiceToActiveConsultation), o su cargo libre.
    /// </summary>
    public void UnchargeServiceUnits(PatientServiceItem item, int units)
    {
        // Un renglón anterior al paso 8.18 que cargó este equipo: su Id tiene en la cuenta un renglón con su nombre,
        // aunque el servicio ya se llame distinto o esté inactivo (con sus insumos por defecto de ese servicio)
        var account = _consultationService?.CurrentConsultation?.Items;
        if (account != null && PatientSheetRows.AccountLegacyServiceId(item, account) is int ownId)
        {
            Uncharge(-ownId, units);
            var own = _catalog.AllServices.FirstOrDefault(s => s.Id == ownId) ?? _catalog.Services.FirstOrDefault(s => s.Id == ownId);
            foreach (var dp in own?.DefaultProducts ?? Enumerable.Empty<ServiceDefaultProduct>())
            {
                if (dp.Product != null) Uncharge(dp.Product.Id, Math.Max(1, dp.Quantity) * units);
            }
            return;
        }

        var service = FindService(item);
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
    public void AddServiceToActiveConsultation(ClinicalService? service) => TryAddServiceToActiveConsultation(service);

    /// <summary>Carga una unidad del servicio con sus insumos por defecto; false si no se pudo (bloqueada o sin consulta).</summary>
    private bool TryAddServiceToActiveConsultation(ClinicalService? service)
    {
        if (service == null || _gate.BlockIfConsultationReadOnly() || _gate.BlockIfReceivingHandoff()) return false;

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
            return true;
        }
        return false;
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
