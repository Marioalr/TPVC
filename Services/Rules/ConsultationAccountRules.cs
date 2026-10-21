using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// La cuenta de la consulta (paso 8.26; antes dentro de <c>ConsultationAccount</c> y <c>ConsultationService</c>, sin cambios):
/// el honorario, el total y cómo se suman y se quitan los renglones. Lo que cada hoja carga a la cuenta (receta que se surte
/// aquí, insumos y servicios con sus insumos) lo fijan los vectores de <c>sheet-charges.json</c>, que corren la cuenta real.
/// Sin WPF ni base: vectores en <c>contracts/golden/consultation-account.json</c>.
/// </summary>
public static class ConsultationAccountRules
{
    /// <summary>El código del servicio que es el honorario si existe.</summary>
    public const string FeeServiceCode = "SERV-001";

    /// <summary>El nombre del honorario sin un servicio de consulta en el catálogo.</summary>
    public const string DefaultFeeName = "Consulta Médica General";

    /// <summary>El honorario sin un servicio de consulta en el catálogo.</summary>
    public const decimal DefaultFee = 300.00m;

    /// <summary>El código de un cargo libre que no trae uno.</summary>
    public const string FreeLineCode = "CARGO";

    /// <summary>
    /// El servicio que se cobra como honorario, entre los activos: el que usaba el equipo que envió la consulta
    /// (<paramref name="overrideId"/>, su Id local aquí), el de código SERV-001, el primero de categoría "Consulta" o el primero
    /// cuyo nombre contiene "Consulta" (sin distinguir mayúsculas; los acentos cuentan); null si ninguno.
    /// </summary>
    public static ClinicalService? FeeService(IEnumerable<ClinicalService> services, int? overrideId)
    {
        var list = services as IReadOnlyCollection<ClinicalService> ?? services.ToList();
        return (overrideId is int feeId ? list.FirstOrDefault(s => s.IsActive && s.Id == feeId) : null)
            ?? list.FirstOrDefault(s => s.IsActive && s.Code.Equals(FeeServiceCode, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(s => s.IsActive && s.Category.Equals("Consulta", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(s => s.IsActive && s.Name.Contains("Consulta", StringComparison.OrdinalIgnoreCase));
    }

    public static string FeeName(ClinicalService? feeService) => feeService?.Name ?? DefaultFeeName;

    public static decimal FeeAmount(ClinicalService? feeService) => feeService?.Price ?? DefaultFee;

    /// <summary>Lo que se cobrará al terminar: el honorario más cada renglón (cantidad × precio).</summary>
    public static decimal Total(decimal feeAmount, IEnumerable<ConsultationProductItem> items) => feeAmount + items.Sum(i => i.Subtotal);

    /// <summary>El Id del renglón de un servicio del catálogo en la cuenta: su Id con signo negativo.</summary>
    public static int ServiceLineId(ClinicalService service) => -service.Id;

    /// <summary>La "categoría" (genérico) que lleva el renglón de un servicio: "Servicio Clínico • Curaciones".</summary>
    public static string ServiceLineGeneric(string? category) => $"Servicio Clínico • {category}";

    /// <summary>La "presentación" que lleva el renglón de un servicio del catálogo: "Duración: 20 min".</summary>
    public static string ServiceLinePresentation(int minutes) => $"Duración: {minutes} min";

    /// <summary>El código de un cargo libre: el que trae o "CARGO".</summary>
    public static string FreeChargeCode(string? code) => string.IsNullOrWhiteSpace(code) ? FreeLineCode : code;

    /// <summary>
    /// "Se surte aquí": cuántas piezas carga un medicamento recetado (su cantidad recomendada, 1 como mínimo), o null si no se
    /// carga porque no hay producto o sus existencias no alcanzan (se receta igual).
    /// </summary>
    public static int? PrescriptionChargeQuantity(PatientPrescriptionItem item, Product? product)
    {
        var quantity = Math.Max(1, item.RecommendedQuantity);
        return product == null || product.CurrentStock < quantity ? null : quantity;
    }

    /// <summary>
    /// Suma <paramref name="quantity"/> piezas a la cuenta: al renglón que ya tiene ese Id (solo cambia su cantidad; el nombre y
    /// el precio se quedan como estaban) o en uno nuevo al final. false con cantidad 0 o negativa. Las piezas sueltas de un paquete
    /// (<paramref name="isIndividualUnits"/>) van en su propio renglón, aparte de los paquetes completos del mismo producto.
    /// </summary>
    public static bool AddLine(IList<ConsultationProductItem> items, int productId, string code, string name, string? genericName,
        string? presentation, string unit, decimal unitPrice, int quantity, DateTime addedAt, bool isIndividualUnits = false)
    {
        if (quantity <= 0) return false;
        var existing = items.FirstOrDefault(i => i.ProductId == productId && i.IsIndividualUnits == isIndividualUnits);
        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            items.Add(new ConsultationProductItem
            {
                ProductId = productId,
                Code = code,
                Name = name,
                GenericName = genericName,
                Presentation = presentation,
                Unit = unit,
                UnitPrice = unitPrice,
                Quantity = quantity,
                IsIndividualUnits = isIndividualUnits,
                AddedAt = addedAt
            });
        }
        return true;
    }

    /// <summary>
    /// Quita <paramref name="quantity"/> piezas del renglón con ese Id: si tiene más se reduce y si no sale de la cuenta.
    /// false si no hay renglón o la cantidad no es positiva (nada cambia). Con <paramref name="isIndividualUnits"/> se prefiere el
    /// renglón de ese modo (piezas sueltas o paquetes) y, si no hay, cualquiera con ese Id.
    /// </summary>
    public static bool RemoveLine(IList<ConsultationProductItem> items, int productId, int quantity, bool? isIndividualUnits = null)
    {
        var item = items.FirstOrDefault(i => i.ProductId == productId && (isIndividualUnits == null || i.IsIndividualUnits == isIndividualUnits.Value))
                   ?? items.FirstOrDefault(i => i.ProductId == productId);
        if (item == null || quantity <= 0) return false;
        if (item.Quantity > quantity) item.Quantity -= quantity;
        else items.Remove(item);
        return true;
    }
}
