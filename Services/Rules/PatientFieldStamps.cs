using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TPVC.Services.Rules;

/// <summary>
/// Paso 8.22 (H1b): las marcas por campo del expediente, en <c>Patients.FieldStampsJson</c>. Sin WPF ni base: la app de
/// Kotlin las mantiene igual (<c>contracts/golden/field-stamps.json</c>). Es un objeto JSON plano {llave: marca}, con las
/// marcas en el formato del protocolo (<c>yyyy-MM-dd HH:mm:ss.fffffff</c>, 27 caracteres, UTC) y las llaves ordenadas
/// (forma canónica, <see cref="CanonicalJson"/>):
/// <list type="bullet">
/// <item><c>"&lt;columna&gt;"</c>: la última vez que este expediente cambió ese campo (la columna remota, como <c>Allergies</c>).
/// En una hoja (<c>Active*Json</c>) es la marca de su orden: la última vez que cambió qué renglones tiene o en qué orden.</item>
/// <item><c>"&lt;hoja&gt;/&lt;RowId&gt;"</c>: la última vez que se agregó o cambió ese renglón (RowId en minúsculas).</item>
/// <item><c>"&lt;hoja&gt;/&lt;RowId&gt;/quitado"</c>: cuándo se quitó. El renglón vive si su marca es mayor que la de quitado; con
/// marcas iguales, gana quitado. Los arreglos de las hojas solo llevan renglones vivos: las PC v1.36–v1.38 y cualquier
/// lector viejo ven exactamente lo que hay.</item>
/// <item><c>"UpdatedAtUtc"</c>: el <c>UpdatedAtUtc</c> de la fila con que se mantuvieron estas marcas. Si la fila tiene un
/// <c>UpdatedAtUtc</c> mayor, la escribió alguien que no conoce las marcas (una PC v1.19–v1.38 sube la fila sin tocar esta
/// columna): es una escritura sin marcas (<see cref="IsStampless"/>).</item>
/// <item>Cualquier otra llave es de una versión más nueva: se conserva, y al combinar gana la marca mayor.</item>
/// </list>
/// Una marca nunca vale más que el <c>UpdatedAtUtc</c> de su fila (<see cref="Effective"/>): si la marca de la fila se
/// reparó porque estaba en el futuro (paso 8.7), las del campo se reparan con ella. Los renglones quitados se purgan 90
/// días después de la marca más nueva del expediente (<see cref="Purge"/>).
/// </summary>
public sealed class PatientFieldStamps
{
    /// <summary>La llave con el UpdatedAtUtc de la fila con que se mantuvieron las marcas.</summary>
    public const string RowStampKey = "UpdatedAtUtc";

    /// <summary>Lo que se agrega a la llave de un renglón quitado.</summary>
    public const string RemovedSuffix = "/quitado";

    /// <summary>Cuánto se guarda un renglón quitado, contado desde la marca más nueva del expediente.</summary>
    public static readonly TimeSpan RemovedRowRetention = TimeSpan.FromDays(90);

    private readonly SortedDictionary<string, string> _entries = new(StringComparer.Ordinal);

    public PatientFieldStamps()
    {
    }

    private PatientFieldStamps(IEnumerable<KeyValuePair<string, string>> entries)
    {
        foreach (var (key, value) in entries) _entries[key] = value;
    }

    /// <summary>
    /// Lee el texto de la columna. Vacío, nulo o dañado = sin marcas (como una fila de antes del paso). Un valor que no es
    /// texto se ignora.
    /// </summary>
    public static PatientFieldStamps Parse(string? json)
    {
        var stamps = new PatientFieldStamps();
        if (string.IsNullOrWhiteSpace(json)) return stamps;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return stamps;
            foreach (var (key, value) in obj)
            {
                if (CanonicalJson.AsString(value) is { } text) stamps._entries[key] = text;
            }
        }
        catch (JsonException)
        {
            // Dañado: sin marcas
        }
        return stamps;
    }

    /// <summary>El texto canónico que se guarda (llaves ordenadas, sin espacios); "" sin ninguna marca.</summary>
    public string ToJson()
    {
        if (_entries.Count == 0) return string.Empty;
        var obj = new JsonObject();
        foreach (var (key, value) in _entries) obj[key] = value;
        return CanonicalJson.Write(obj);
    }

    public PatientFieldStamps Clone() => new(_entries);

    public IReadOnlyDictionary<string, string> Entries => _entries;

    public int Count => _entries.Count;

    public string? this[string key]
    {
        get => _entries.TryGetValue(key, out var value) ? value : null;
        set
        {
            if (value == null) _entries.Remove(key);
            else _entries[key] = value;
        }
    }

    public bool Remove(string key) => _entries.Remove(key);

    /// <summary>La marca de la fila con que se mantuvieron estas marcas; null si nunca se mantuvieron.</summary>
    public DateTime? RowStamp
    {
        get => ParseStamp(this[RowStampKey]);
        set => this[RowStampKey] = value is { } v ? Format(v) : null;
    }

    /// <summary>
    /// Una escritura sin marcas: la fila tiene un UpdatedAtUtc mayor que el de sus marcas (o no tiene marcas). La escribió
    /// una PC anterior al paso o un camino que no pasó por el sello: lo que cambió no se sabe campo por campo.
    /// </summary>
    public bool IsStampless(DateTime rowUpdatedAtUtc) => RowStamp is not { } stamp || rowUpdatedAtUtc > stamp;

    /// <summary>La marca de una llave tal como cuenta en esta fila: nunca más que su UpdatedAtUtc; null si no hay.</summary>
    public string? Effective(string key, DateTime rowUpdatedAtUtc)
    {
        var value = this[key];
        if (value == null || ParseStamp(value) is not { } stamp) return value;
        return stamp > rowUpdatedAtUtc ? Format(rowUpdatedAtUtc) : value;
    }

    // ================================================================== llaves

    public static string RowKey(string column, Guid rowId) => $"{column}/{SyncIds.Format(rowId)}";

    public static string RemovedKey(string column, Guid rowId) => RowKey(column, rowId) + RemovedSuffix;

    /// <summary>Si la llave es de un renglón de <paramref name="column"/>: su RowId y si es la de quitado.</summary>
    public static bool TryParseRowKey(string key, string column, out Guid rowId, out bool removed)
    {
        rowId = Guid.Empty;
        removed = false;
        var prefix = column + "/";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = key[prefix.Length..];
        if (rest.EndsWith(RemovedSuffix, StringComparison.Ordinal))
        {
            removed = true;
            rest = rest[..^RemovedSuffix.Length];
        }
        return rest.Length == 36 && Guid.TryParseExact(rest, "D", out rowId) && SyncIds.Format(rowId) == rest;
    }

    // ================================================================== marcas

    /// <summary>Una marca en el formato del protocolo (27 caracteres).</summary>
    public static string Format(DateTime stamp) => stamp.ToString(TursoSyncFormats.DateTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>Una marca del protocolo; null si el texto no lo es.</summary>
    public static DateTime? ParseStamp(string? text) =>
        text != null && text.Length == TursoSyncFormats.DateTimeFormat.Length
                     && DateTime.TryParseExact(text, TursoSyncFormats.DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)
            ? stamp
            : null;

    /// <summary>La mayor de dos marcas (null cuenta como la mínima). Dos marcas del protocolo se comparan como texto.</summary>
    public static string? Max(string? a, string? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return string.CompareOrdinal(a, b) >= 0 ? a : b;
    }

    /// <summary>Compara dos marcas (null cuenta como la mínima).</summary>
    public static int Compare(string? a, string? b)
    {
        if (a == null) return b == null ? 0 : -1;
        if (b == null) return 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    /// <summary>La marca más nueva de cualquier llave salvo <see cref="RowStampKey"/>; null sin marcas.</summary>
    public DateTime? Newest()
    {
        DateTime? newest = null;
        foreach (var (key, value) in _entries)
        {
            if (key == RowStampKey || ParseStamp(value) is not { } stamp) continue;
            if (newest == null || stamp > newest) newest = stamp;
        }
        return newest;
    }

    /// <summary>
    /// Purga los renglones quitados (los de cualquier hoja) cuya marca de quitado es más de 90 días anterior a la marca más
    /// nueva del expediente: su llave de quitado y la del renglón. Depende solo de las marcas, así dos equipos purgan lo
    /// mismo y la purga sola nunca se vuelve una diferencia que suba.
    /// </summary>
    public void Purge(IEnumerable<string> sheetColumns)
    {
        if (Newest() is not { } newest) return;
        var horizon = Format(newest - RemovedRowRetention);
        foreach (var column in sheetColumns)
        {
            foreach (var key in _entries.Keys.ToList())
            {
                if (!TryParseRowKey(key, column, out var rowId, out var removed) || !removed) continue;
                var removedAt = _entries[key];
                if (string.CompareOrdinal(removedAt, horizon) >= 0) continue;
                var rowKey = RowKey(column, rowId);
                // Solo si sigue quitado (una edición posterior lo revive: no se purga)
                if (_entries.TryGetValue(rowKey, out var edited) && string.CompareOrdinal(edited, removedAt) > 0) continue;
                _entries.Remove(key);
                _entries.Remove(rowKey);
            }
        }
    }

    // ================================================================== escritura local

    /// <summary>
    /// Lo que un guardado local deja en las marcas (lo que hace el sello de la PC, <c>StampSyncMetadata</c>, y lo que hará
    /// Kotlin): cada campo que cambió por su significado (<paramref name="changedFields"/>) toma la marca nueva; en una hoja
    /// que cambió (<paramref name="changedSheets"/>, el texto de antes y el de después), cada renglón agregado o cambiado
    /// toma la marca nueva en su llave, cada renglón que ya no está la toma en su llave de quitado, y si cambió qué renglones
    /// hay o su orden, la hoja la toma en su propia llave. Al final, <see cref="RowStampKey"/> = la marca nueva y se purgan
    /// los renglones quitados viejos.
    /// </summary>
    public void ApplyLocalWrite(DateTime stamp, IEnumerable<string> changedFields,
        IEnumerable<(string Column, string? Before, string? After)> changedSheets, Guid patientSyncId)
    {
        var text = Format(stamp);
        foreach (var field in changedFields) _entries[field] = text;
        foreach (var (column, before, after) in changedSheets)
        {
            var old = PatientSheetCanonical.ReadRows(column, before, patientSyncId);
            var current = PatientSheetCanonical.ReadRows(column, after, patientSyncId);
            var oldById = old.ToDictionary(r => r.RowId);
            var currentIds = current.Select(r => r.RowId).ToHashSet();
            foreach (var row in current)
            {
                if (!oldById.TryGetValue(row.RowId, out var previous) || previous.Canonical != row.Canonical)
                {
                    _entries[RowKey(column, row.RowId)] = text;
                }
            }
            foreach (var row in old.Where(r => !currentIds.Contains(r.RowId)))
            {
                _entries[RemovedKey(column, row.RowId)] = text;
            }
            if (!old.Select(r => r.RowId).SequenceEqual(current.Select(r => r.RowId)))
            {
                _entries[column] = text;
            }
        }
        RowStamp = stamp;
        Purge(PatientSheetCanonical.Columns);
    }
}
