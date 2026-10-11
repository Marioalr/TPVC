using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// La copia de la consulta que viaja entre equipos (paso 8.11; antes dentro de ConsultationHandoffViewModel): al
/// enviar, cada cargo pasa de su Id local a su clase (<c>producto</c>, <c>servicio</c> o <c>libre</c>) y su SyncId; al
/// recibir, se traduce al Id de este equipo, y lo que no existe aquí se cobra como cargo libre. Sin WPF ni base: el
/// contrato de <c>docs/TURSO_SYNC_ARCHITECTURE.md</c> §4.5 que repite la app de Android.
/// </summary>
public static class HandoffPayloadCodec
{
    public const string LineProduct = "producto";
    public const string LineService = "servicio";
    public const string LineFree = "libre";

    /// <summary>Este equipo entiende la copia: ni el renglón ni el borrador son de una versión más nueva.</summary>
    public static bool CanRead(int payloadVersion, ConsultationDraft? payload) =>
        payload != null && payloadVersion <= ConsultationHandoffService.ProtocolVersion && payload.Version <= ConsultationDraft.CurrentVersion;

    /// <summary>
    /// Deja el borrador listo para viajar: sin los Id locales ni las marcas del envío, con los identificadores de
    /// sincronización del expediente, la nota, la cita y el honorario, y cada cargo con su clase y su SyncId.
    /// </summary>
    public static void PrepareForSending(ConsultationDraft draft, IReadOnlyList<CatalogRef> refs, Guid patientSyncId, DateTime? patientUpdatedAtUtc,
        Guid? progressNoteSyncId, Guid? appointmentSyncId, int? feeServiceId, DateTime savedAt)
    {
        draft.Handoff = null;
        draft.ReceivedHandoffId = null;
        draft.ReceivedHandoffToken = null;
        draft.PatientId = 0;
        draft.AttendingAppointmentId = null;
        draft.SavedAt = savedAt;
        draft.PatientSyncId = patientSyncId;
        draft.PatientUpdatedAtUtc = patientUpdatedAtUtc;
        draft.ProgressNoteSyncId = progressNoteSyncId;
        draft.AttendingAppointmentSyncId = appointmentSyncId;
        draft.FeeServiceSyncId = feeServiceId is > 0 ? refs.FirstOrDefault(r => r.Kind == CatalogRefKind.Service && r.Id == feeServiceId)?.SyncId : null;

        foreach (var item in draft.Items)
        {
            CatalogRef? match = item.ProductId switch
            {
                > 0 => refs.FirstOrDefault(r => r.Kind == CatalogRefKind.Product && r.Id == item.ProductId),
                < 0 and > -1_000_000 => refs.FirstOrDefault(r => r.Kind == CatalogRefKind.Service && r.Id == -item.ProductId),
                _ => null
            };
            item.LineKind = match?.Kind switch
            {
                CatalogRefKind.Product => LineProduct,
                CatalogRefKind.Service => LineService,
                _ => LineFree
            };
            item.CatalogSyncId = match?.SyncId;
            item.ProductId = 0;
        }
    }

    /// <summary>
    /// Traduce cada cargo recibido al Id de este equipo: los productos con Id positivo, los servicios con Id negativo y lo
    /// que aquí no existe (o llegó como libre) con <see cref="FreeChargeId"/>. Devuelve los productos y servicios que no
    /// se encontraron (se cobran como cargo libre, sin descontar existencias).
    /// </summary>
    public static List<string> MapReceivedItems(ConsultationDraft payload, IReadOnlyList<CatalogRef> refs)
    {
        var freeLines = new List<string>();
        foreach (var item in payload.Items)
        {
            var kind = item.LineKind switch
            {
                LineProduct => (CatalogRefKind?)CatalogRefKind.Product,
                LineService => CatalogRefKind.Service,
                _ => null
            };
            var match = kind == null ? null : FindCatalogRef(refs, kind.Value, item);
            if (match == null)
            {
                if (kind != null) freeLines.Add(item.Name);
                item.ProductId = FreeChargeId(item.Name);
            }
            else
            {
                item.ProductId = match.Kind == CatalogRefKind.Product ? match.Id : -match.Id;
            }
        }
        return freeLines;
    }

    /// <summary>Por identificador; si no llegó, el mismo código Y nombre (activo).</summary>
    public static CatalogRef? FindCatalogRef(IReadOnlyList<CatalogRef> refs, CatalogRefKind kind, ConsultationProductItem item)
    {
        if (item.CatalogSyncId is Guid syncId)
        {
            var bySync = refs.FirstOrDefault(r => r.Kind == kind && r.SyncId == syncId && r.IsActive);
            if (bySync != null) return bySync;
        }
        var name = TextSearch.Normalize(item.Name);
        return refs.FirstOrDefault(r => r.Kind == kind && r.IsActive
                                        && string.Equals(r.Code, item.Code, StringComparison.OrdinalIgnoreCase)
                                        && TextSearch.Normalize(r.Name) == name);
    }

    /// <summary>
    /// Id local de un cargo libre, calculado del nombre (sin acentos ni mayúsculas): -1 000 000 a -1 999 999. Solo vale en
    /// el equipo que lo calcula; entre equipos viajan la clase y el nombre.
    /// </summary>
    public static int FreeChargeId(string name)
    {
        int hash = 17;
        foreach (var c in TextSearch.Normalize(name)) hash = unchecked(hash * 31 + c);
        return -(1_000_000 + (int)((uint)hash % 1_000_000));
    }
}
