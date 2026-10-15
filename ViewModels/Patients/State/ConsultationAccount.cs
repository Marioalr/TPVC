using System;
using System.Collections.Generic;
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
        if (consultation == null || item == null || quantity <= 0) return;

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
    /// Carga el servicio recién agregado (todas sus unidades con sus insumos por defecto, o su cargo libre) y le pone al
    /// renglón su lista de insumos (revisión 13): los insumos por defecto por las unidades, o vacía en un cargo libre. La lista
    /// es lo que el renglón cargó; la configuración del servicio no cambia. false si no se pudo (sin consulta, bloqueada o sin
    /// precio).
    /// </summary>
    public bool ChargeService(PatientServiceItem item)
    {
        int units = Math.Max(1, item.Quantity);
        var service = FindService(item);
        if (service != null)
        {
            // Lo que impide cargar una unidad (sin consulta, en otro equipo) impide todas
            if (!ChargeServiceLine(service, units)) return false;
            var supplies = new List<PatientServiceSupply>();
            ChargeAddedPieces(supplies, ServiceRowSupplies.AddUnits(supplies, service.DefaultProducts, units));
            item.Supplies = supplies;
            _workflow.SyncActiveConsultation();
            return true;
        }

        if (!ChargeFreeLine(item.ServiceCode, item.ServiceName, $"Servicio Clínico • {item.Category}", "Servicio", item.Price, units))
        {
            _info.InfoModalMessage = $"'{item.ServiceName}' no tiene precio: se registró en el expediente, pero no se cargó a la cuenta.";
            _info.IsInfoModalVisible = true;
            return false;
        }
        item.Supplies = new List<PatientServiceSupply>();
        return true;
    }

    /// <summary>
    /// El renglón del servicio en la cuenta, por sus unidades (sin sus insumos): con los mismos candados que
    /// <see cref="TryAddServiceToActiveConsultation"/>. No sincroniza: lo hace quien llama al terminar.
    /// </summary>
    private bool ChargeServiceLine(ClinicalService service, int units)
    {
        if (units <= 0 || _gate.BlockIfConsultationReadOnly() || _gate.BlockIfReceivingHandoff()) return false;
        if (!_active.HasActiveConsultation) _workflow.StartActiveConsultation();
        if (_consultationService?.CurrentConsultation == null) return false;
        return _consultationService.AddCartItemToActiveConsultation(
            -service.Id, service.Code, service.Name, $"Servicio Clínico • {service.Category}",
            $"Duración: {service.EstimatedDurationMinutes} min", "Servicio", service.Price, units);
    }

    /// <summary>
    /// Carga piezas de un insumo de un servicio: como el producto (con su nombre, como los insumos por defecto) o, sin él, como
    /// el cargo libre que ya tiene el insumo (<paramref name="freeLineId"/>) o el de su nombre, a su precio. false sin consulta o
    /// si el cargo libre no tiene precio. No sincroniza.
    /// </summary>
    private bool ChargeServiceSupplyUnits(PatientServiceSupply entry, Product? product, int units, int? freeLineId = null)
    {
        if (_consultationService?.CurrentConsultation == null || units <= 0) return false;
        if (product != null) return ChargeServiceProductUnits(product, units);
        if (entry.UnitPrice <= 0) return false;
        _consultationService.AddCartItemToActiveConsultation(
            freeLineId ?? FreeChargeId(entry.SupplyName), "INSUMO", entry.SupplyName, "Material clínico", null, "Piezas", entry.UnitPrice, units);
        return true;
    }

    /// <summary>
    /// Carga lo que <see cref="ServiceRowSupplies.AddUnits"/> sumó a la lista, cada insumo en el renglón de la cuenta con que
    /// se cobra (<see cref="ChargeServiceSupplyOnItsLine"/>): en un equipo que recibió la consulta, un producto que aquí no está
    /// activo se cobra como el cargo libre al que se tradujo, no como el producto. Lo que no se pudo cargar sale de la lista.
    /// No sincroniza.
    /// </summary>
    private void ChargeAddedPieces(List<PatientServiceSupply> list, IEnumerable<ServiceRowSupplies.Pieces> added)
    {
        foreach (var pieces in added)
        {
            if (!ChargeServiceSupplyOnItsLine(pieces.Entry, pieces.Product, pieces.Count)) pieces.Entry.Quantity -= pieces.Count;
        }
        list.RemoveAll(e => e == null || e.Quantity <= 0);
    }

    /// <summary>
    /// Carga piezas de un insumo de la lista en su renglón de la cuenta, el mismo que encuentran + / − y la X
    /// (<see cref="ServiceSupplyLine"/>): un cargo libre (Id negativo) a su precio; un producto como ese producto (el que se
    /// pasa si es el del renglón, o el del inventario con ese Id); y si este equipo ya no lo tiene activo, en ese mismo
    /// renglón con el nombre y el precio del insumo. Anota el Id local en el insumo. No sincroniza.
    /// </summary>
    private bool ChargeServiceSupplyOnItsLine(PatientServiceSupply entry, Product? product, int units)
    {
        if (_consultationService?.CurrentConsultation == null || units <= 0) return false;
        var line = ServiceSupplyLine(entry);
        bool charged;
        if (line.LineId <= 0)
        {
            charged = ChargeServiceSupplyUnits(entry, null, units, line.LineId < 0 ? line.LineId : null);
        }
        else if ((product?.Id == line.LineId ? product : _catalog.AvailableProducts.FirstOrDefault(p => p.Id == line.LineId)) is { } lineProduct)
        {
            charged = ChargeServiceProductUnits(lineProduct, units);
        }
        else
        {
            charged = _consultationService.AddCartItemToActiveConsultation(
                line.LineId, "INSUMO", entry.SupplyName, null, null, "Piezas", entry.UnitPrice, units);
        }
        if (charged) PatientSheetRows.RefreshSupplyLine(entry, line);
        return charged;
    }

    /// <summary>Piezas de un insumo de un servicio cargadas como el producto, con su nombre (como siempre los insumos por defecto). No sincroniza.</summary>
    private bool ChargeServiceProductUnits(Product product, int units)
    {
        if (_consultationService?.CurrentConsultation == null || units <= 0) return false;
        _consultationService.AddCartItemToActiveConsultation(
            product.Id, product.Code, product.Name, product.GenericName, product.Presentation, product.Unit, product.Price, units);
        return true;
    }

    /// <summary>
    /// Renglón de la cuenta de un insumo de un servicio: las mismas reglas que un insumo de la hoja (por su SyncId, el cargo
    /// libre, el Id negativo, el Id viejo con el mismo nombre o por nombre).
    /// </summary>
    public PatientSheetRows.SupplyLine ServiceSupplyLine(PatientServiceSupply entry) =>
        PatientSheetRows.ResolveSupplyLine(entry, _catalog.AvailableProducts, _catalog.ProductRefs);

    /// <summary>
    /// Carga piezas más de un insumo de la lista de un renglón de servicio (+ o agregar): el producto del inventario con que
    /// se cargó, o su cargo libre. Lo anota en el insumo (sus piezas y el Id local de su renglón). false si no se pudo.
    /// </summary>
    public bool ChargeServiceSupply(PatientServiceSupply entry, Product? product, PatientSheetRows.SupplyLine line, int units)
    {
        if (!ChargeServiceSupplyUnits(entry, product, units, line.LineId < 0 ? line.LineId : null)) return false;
        entry.Quantity += units;
        PatientSheetRows.RefreshSupplyLine(entry, line);
        _workflow.SyncActiveConsultation();
        return true;
    }

    /// <summary>Descuenta piezas de un insumo de la lista de un renglón de servicio, del mismo renglón con que se cargaron.</summary>
    public void UnchargeServiceSupply(PatientServiceSupply entry, int units)
    {
        units = Math.Min(units, entry.Quantity);
        if (units <= 0) return;
        var line = ServiceSupplyLine(entry);
        Uncharge(line.LineId, units);
        entry.Quantity -= units;
        PatientSheetRows.RefreshSupplyLine(entry, line);
    }

    /// <summary>
    /// El servicio con que se descuenta un renglón: el de su Id si la cuenta abierta tiene su renglón con su nombre (un renglón
    /// anterior al paso 8.18 que cargó este equipo, aunque el servicio ya se llame distinto o esté inactivo), o el que
    /// encuentran las reglas del paso 8.18; null = cargo libre. <paramref name="lineId"/> es el renglón del servicio en la cuenta.
    /// </summary>
    private ClinicalService? ServiceForRow(PatientServiceItem item, out int lineId)
    {
        var account = _consultationService?.CurrentConsultation?.Items;
        if (account != null && PatientSheetRows.AccountLegacyServiceId(item, account) is int ownId)
        {
            lineId = -ownId;
            return _catalog.AllServices.FirstOrDefault(s => s.Id == ownId) ?? _catalog.Services.FirstOrDefault(s => s.Id == ownId);
        }
        var service = FindService(item);
        lineId = service != null ? -service.Id : FreeChargeId(item.ServiceName);
        return service;
    }

    /// <summary>
    /// La lista de un renglón de esta consulta que no la tiene (de una versión anterior o de otro equipo que no la conoce): lo
    /// que descontaría hoy quitarlo, los insumos por defecto del servicio por sus unidades. No carga nada ni toca el renglón.
    /// </summary>
    public List<PatientServiceSupply> LegacyServiceSupplies(PatientServiceItem item)
    {
        var service = ServiceForRow(item, out _);
        return ServiceRowSupplies.LegacySupplies(service, Math.Max(0, item.ChargedUnits));
    }

    /// <summary>
    /// Cambia las unidades cargadas de un renglón de servicio (+ o −, <paramref name="delta"/> distinto de 0). Con lista: el
    /// servicio y los insumos por defecto de esas unidades (<see cref="ServiceRowSupplies.AddUnits"/> y
    /// <see cref="ServiceRowSupplies.RemoveUnits"/>: lo ajustado a mano se queda). Sin lista, como antes: el servicio con sus
    /// insumos por defecto de hoy por cada unidad. false si no se pudo cargar.
    /// </summary>
    public bool ChangeServiceUnits(PatientServiceItem item, int delta)
    {
        if (delta == 0) return true;
        var service = ServiceForRow(item, out int lineId);
        if (delta > 0)
        {
            if (service == null)
            {
                return ChargeFreeLine(item.ServiceCode, item.ServiceName, $"Servicio Clínico • {item.Category}", "Servicio", item.Price, delta);
            }
            if (!ChargeServiceLine(service, delta)) return false;
            if (item.Supplies != null)
            {
                // Sobre una copia, y el renglón queda con ella: WPF no vuelve a dibujar una lista que se le asigna otra vez.
                // Cada insumo en su renglón de la cuenta: el mismo que descuentan una unidad menos, la X y quitar el renglón
                var added = new List<PatientServiceSupply>(item.Supplies);
                ChargeAddedPieces(added, ServiceRowSupplies.AddUnits(added, service.DefaultProducts, delta));
                item.Supplies = added;
            }
            else
            {
                foreach (var dp in service.DefaultProducts.Where(dp => dp.Product != null))
                {
                    ChargeServiceProductUnits(dp.Product!, ServiceRowSupplies.PiecesPerUnit(dp) * delta);
                }
            }
            _workflow.SyncActiveConsultation();
            return true;
        }

        int units = -delta;
        if (item.Supplies == null)
        {
            UnchargeServiceUnits(item, units);
            return true;
        }
        Uncharge(lineId, units);
        // Sobre una copia (un insumo en 0 sale de ella), y el renglón queda con ella para que la pantalla se vuelva a dibujar
        var list = new List<PatientServiceSupply>(item.Supplies);
        foreach (var pieces in ServiceRowSupplies.RemoveUnits(list, service?.DefaultProducts, units))
        {
            // Quitar las piezas de la lista ya lo hizo RemoveUnits: aquí solo se descuentan de su renglón de la cuenta
            Uncharge(ServiceSupplyLine(pieces.Entry).LineId, pieces.Count);
        }
        item.Supplies = list;
        return true;
    }

    /// <summary>
    /// Quitar el renglón: descuenta lo que cargó en esta consulta. Con lista, el servicio por sus unidades y cada insumo de la
    /// lista con sus piezas (lo ajustado a mano incluido); sin lista, como antes (<see cref="UnchargeServiceUnits"/>).
    /// </summary>
    public void UnchargeServiceRow(PatientServiceItem item, int units)
    {
        if (item.Supplies == null)
        {
            UnchargeServiceUnits(item, units);
            return;
        }
        ServiceForRow(item, out int lineId);
        Uncharge(lineId, units);
        foreach (var pieces in ServiceRowSupplies.UnchargeAll(item.Supplies))
        {
            Uncharge(ServiceSupplyLine(pieces.Entry).LineId, pieces.Count);
        }
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
    /// Descuenta unidades de un servicio sin lista de insumos (un renglón de una versión anterior o de otro equipo que no la
    /// conoce), como siempre: por cada una, su renglón y los insumos por defecto que el servicio tiene hoy
    /// (AddServiceToActiveConsultation), o su cargo libre.
    /// </summary>
    public void UnchargeServiceUnits(PatientServiceItem item, int units)
    {
        // Un renglón anterior al paso 8.18 que cargó este equipo: su Id tiene en la cuenta un renglón con su nombre,
        // aunque el servicio ya se llame distinto o esté inactivo (con sus insumos por defecto de ese servicio)
        var service = ServiceForRow(item, out int lineId);
        Uncharge(lineId, units);
        foreach (var dp in service?.DefaultProducts ?? Enumerable.Empty<ServiceDefaultProduct>())
        {
            if (dp.Product != null) Uncharge(dp.Product.Id, ServiceRowSupplies.PiecesPerUnit(dp) * units);
        }
    }

    /// <summary>
    /// Piezas de un producto que todavía caben en la cuenta (<see cref="ServiceRowSupplies.PiecesThatFit"/>): sus existencias
    /// menos lo que ya está cargado de él.
    /// </summary>
    public int PiecesThatFit(Product product, int requested) =>
        ServiceRowSupplies.PiecesThatFit(requested, product.CurrentStock, ChargedProductUnits(product.Id));

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
