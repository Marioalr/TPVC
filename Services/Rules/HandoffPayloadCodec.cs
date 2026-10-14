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
    /// <param name="patientFieldStampsJson">Paso 8.22: el <c>FieldStampsJson</c> del expediente en este equipo; viajan sus marcas
    /// (sin la de la fila) para que quien recibe espere a tenerlas.</param>
    /// <param name="progressNoteUpdatedAtUtc">Paso 8.22: la marca del avance de la nota.</param>
    public static void PrepareForSending(ConsultationDraft draft, IReadOnlyList<CatalogRef> refs, Guid patientSyncId, DateTime? patientUpdatedAtUtc,
        Guid? progressNoteSyncId, Guid? appointmentSyncId, int? feeServiceId, DateTime savedAt,
        string? patientFieldStampsJson = null, DateTime? progressNoteUpdatedAtUtc = null)
    {
        draft.Handoff = null;
        draft.ReceivedHandoffId = null;
        draft.ReceivedHandoffToken = null;
        draft.PatientId = 0;
        draft.AttendingAppointmentId = null;
        draft.SavedAt = savedAt;
        draft.PatientSyncId = patientSyncId;
        draft.PatientUpdatedAtUtc = patientUpdatedAtUtc;
        draft.PatientFieldStamps = patientFieldStampsJson != null && patientUpdatedAtUtc is { } version
            ? FieldStampsForPayload(patientFieldStampsJson, version)
            : null;
        draft.ProgressNoteUpdatedAtUtc = progressNoteSyncId != null ? progressNoteUpdatedAtUtc : null;
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
    /// Paso 8.22: las marcas por campo que viajan en la copia: el estado normalizado de <c>FieldStampsJson</c>
    /// (<see cref="PatientFieldStamps.State"/>: el piso <c>#piso</c> y cada marca, nunca más que su fila, sin la llave de la
    /// fila), en orden ordinal de sus llaves. Sin marcas, ninguna (la copia lleva el objeto vacío: quien recibe sabe que el que
    /// envía ya las conoce).
    /// </summary>
    public static Dictionary<string, DateTime> FieldStampsForPayload(string? fieldStampsJson, DateTime patientUpdatedAtUtc)
    {
        var stamps = PatientFieldStamps.Parse(fieldStampsJson).State(patientUpdatedAtUtc, Array.Empty<string>());
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var key in stamps.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (PatientFieldStamps.ParseStamp(stamps[key]) is { } stamp) result[key] = stamp;
        }
        return result;
    }

    /// <summary>Qué le falta a este equipo para tomar una consulta recibida (paso 8.22: también las marcas por campo).</summary>
    public enum Arrival
    {
        /// <summary>Ya tiene todo: se puede tomar.</summary>
        Ready,

        /// <summary>Todavía no llega el expediente.</summary>
        PatientMissing,

        /// <summary>Llegó, pero no con los últimos cambios del equipo que la envió.</summary>
        PatientBehind,

        /// <summary>Todavía no llega el avance de la nota.</summary>
        NoteMissing,

        /// <summary>Llegó la nota, pero no su última versión.</summary>
        NoteBehind
    }

    /// <summary>
    /// Antes de tomar una consulta recibida, ya con la sincronización hecha: el expediente debe existir aquí con al menos la
    /// versión de la copia (<c>PatientUpdatedAtUtc</c>) y, desde el paso 8.22, cada marca por campo de la copia
    /// (<c>PatientFieldStamps</c>) debe ser menor o igual a la de aquí: comparar solo la marca de la fila dejaba tomar la
    /// consulta en un equipo cuya fila era "más nueva" por otro campo sin los últimos cargos del que la envió. Lo de aquí se
    /// toma normalizado (<see cref="PatientFieldStamps.State"/>): cada marca cuenta al menos como el piso de aquí (en una
    /// escritura sin marcas, su <c>UpdatedAtUtc</c>), y el piso de la copia (<c>#piso</c>) se compara con el de aquí. Un
    /// renglón quitado que aquí ya se purgaría (su marca de quitado, 90 días antes de la marca más nueva de aquí, y la del
    /// renglón si no la rebasa) no se exige: al combinar se volvería a purgar. Con el avance de la nota:
    /// debe existir y, si la copia trae su marca (<c>ProgressNoteUpdatedAtUtc</c>), tenerla o una más nueva. Una copia de antes
    /// del paso (sin esos campos) se revisa como antes. Si falta algo: "Intente de nuevo en unos segundos".
    /// </summary>
    public static Arrival CheckArrival(ConsultationDraft payload, bool patientExists, DateTime? patientUpdatedAtUtc, string? patientFieldStampsJson,
        bool noteExists, DateTime? noteUpdatedAtUtc)
    {
        if (!patientExists) return Arrival.PatientMissing;
        var localUpdated = patientUpdatedAtUtc ?? DateTime.MinValue;
        if (payload.PatientUpdatedAtUtc is { } version && localUpdated < version) return Arrival.PatientBehind;

        if (payload.PatientFieldStamps is { Count: > 0 } wanted)
        {
            // Lo de aquí, normalizado: cada marca cuenta al menos como el piso (en una escritura sin marcas, su UpdatedAtUtc)
            var local = PatientFieldStamps.Parse(patientFieldStampsJson).State(localUpdated, Array.Empty<string>());
            var floor = local[PatientFieldStamps.FloorKey];
            // Lo que aquí ya se purgaría (un renglón quitado más de 90 días antes de la marca más nueva de aquí: su llave de
            // quitado y la del renglón) no se exige: al combinar se volvería a purgar
            var theirs = new PatientFieldStamps();
            foreach (var (key, stamp) in wanted)
            {
                if (key != PatientFieldStamps.RowStampKey) theirs[key] = PatientFieldStamps.Format(stamp);
            }
            if (local.Newest() is { } newest) theirs.PurgeBefore(PatientSheetCanonical.Columns, newest - PatientFieldStamps.RemovedRowRetention);
            foreach (var (key, stamp) in theirs.Entries)
            {
                var mine = PatientFieldStamps.Max(floor, key == PatientFieldStamps.FloorKey ? null : local[key]);
                if (PatientFieldStamps.Compare(mine, stamp) < 0) return Arrival.PatientBehind;
            }
        }

        if (payload.ProgressNoteSyncId != null)
        {
            if (!noteExists) return Arrival.NoteMissing;
            if (payload.ProgressNoteUpdatedAtUtc is { } note && (noteUpdatedAtUtc ?? DateTime.MinValue) < note) return Arrival.NoteBehind;
        }
        return Arrival.Ready;
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
