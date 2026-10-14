using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Lo que el Kardex le muestra al médico (paso 8.19). Desde el Kardex completo una operación deja un movimiento por lote
/// (una venta de 5 piezas que sale de dos lotes y de lo sin lote son tres renglones del libro), pero en pantalla sigue
/// siendo un renglón por operación, como antes: la cantidad total y la existencia de antes y de después. El libro no
/// cambia; solo se agrupa lo que se lee.
/// </summary>
public static class KardexRows
{
    /// <summary>
    /// Dos movimientos son la misma operación si los dos son del Kardex completo (Delta distinto de 0; una fila anterior
    /// siempre es su propia operación) y comparten producto, tipo, fecha y hora exactas, renglón del ticket, motivo y
    /// responsable: todos los renglones de una operación se escriben juntos con la misma hora.
    /// </summary>
    public static bool SameOperation(InventoryMovement a, InventoryMovement b) =>
        a.Delta != 0 && b.Delta != 0
        && a.ProductId == b.ProductId
        && a.MovementType == b.MovementType
        && a.Date == b.Date
        && a.SaleItemId == b.SaleItemId
        && string.Equals(a.Reason, b.Reason, StringComparison.Ordinal)
        && string.Equals(a.Responsible, b.Responsible, StringComparison.Ordinal);

    /// <summary>
    /// Une las partes de cada operación (la lista viene en el orden del Kardex, del más nuevo al más viejo). Las partes de
    /// una operación tienen la misma hora, pero dentro de esa hora pueden venir intercaladas con las de otra (un ticket de
    /// dos renglones, o filas que llegaron de otro equipo en otro orden): se juntan todas las de cada hora, en el orden en
    /// que aparece la primera. Un grupo de uno se regresa tal cual; uno de varios, como un renglón nuevo con lo que movió
    /// en total, la existencia antes de la primera parte y después de la última, y sin lote.
    /// </summary>
    public static List<InventoryMovement> Group(IReadOnlyList<InventoryMovement> movements)
    {
        var rows = new List<InventoryMovement>();
        int i = 0;
        while (i < movements.Count)
        {
            int end = i + 1;
            while (end < movements.Count && movements[end].Date == movements[i].Date) end++;

            var operations = new List<List<InventoryMovement>>();
            for (int k = i; k < end; k++)
            {
                var operation = operations.FirstOrDefault(o => SameOperation(o[0], movements[k]));
                if (operation != null) operation.Add(movements[k]);
                else operations.Add(new List<InventoryMovement> { movements[k] });
            }
            rows.AddRange(operations.Select(o => o.Count == 1 ? o[0] : Merge(o)));
            i = end;
        }
        return rows;
    }

    private static InventoryMovement Merge(List<InventoryMovement> parts)
    {
        var first = parts[0];
        int delta = parts.Sum(p => p.Delta);
        // Cada parte deja la existencia del producto donde la encontró la siguiente: la de antes es la de la parte con que
        // empieza la cadena (su existencia de antes no es la de después de ninguna otra), sin depender del orden en que
        // llegaron; la de después, esa más lo que movió la operación
        var afters = parts.Select(p => p.StockAfter).ToHashSet();
        int before = (parts.FirstOrDefault(p => !afters.Contains(p.StockBefore)) ?? parts.MinBy(p => p.Id)!).StockBefore;
        return new InventoryMovement
        {
            Id = first.Id,
            SyncId = first.SyncId,
            UpdatedAtUtc = parts.Max(p => p.UpdatedAtUtc),
            ProductId = first.ProductId,
            Product = first.Product,
            SaleItemId = first.SaleItemId,
            MovementType = first.MovementType,
            Quantity = Math.Abs(delta),
            Delta = delta,
            StockBefore = before,
            StockAfter = before + delta,
            Date = first.Date,
            Reason = first.Reason,
            Responsible = first.Responsible
        };
    }
}
