using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

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
/// <item><c>"#piso"</c> (<see cref="FloorKey"/>, no puede ser una columna): el piso. La marca más nueva de una escritura de la
/// fila completa sin marcas que esta versión ya incluye (una PC v1.19–v1.38, la fila de antes de actualizar, «Volver a
/// aplicar»): todo lo que la fila tenía entonces lo tenía desde ese momento. Una llave conocida vale al menos el piso, así
/// que una llave que no lo rebasa no se guarda; un renglón que la fila no tiene y del que no sabe nada más cuenta como
/// quitado en el piso.</item>
/// <item><c>"UpdatedAtUtc"</c>: el <c>UpdatedAtUtc</c> de la fila con que se mantuvieron estas marcas. Si la fila tiene un
/// <c>UpdatedAtUtc</c> mayor, la escribió alguien que no conoce las marcas (una PC v1.19–v1.38 sube la fila sin tocar esta
/// columna): es una escritura sin marcas (<see cref="IsStampless"/>) y su piso es ese <c>UpdatedAtUtc</c>.</item>
/// <item>Cualquier otra llave es de una versión más nueva: se conserva tal cual, y al combinar gana la marca mayor (el piso
/// no la toca).</item>
/// </list>
/// Una marca nunca vale más que el <c>UpdatedAtUtc</c> de su fila: si la marca de la fila se reparó porque estaba en el
/// futuro (paso 8.7), las del campo se reparan con ella. Los renglones quitados se purgan 90 días después de la marca más
/// nueva del expediente (<see cref="Purge"/>). La forma en que se compara y se guarda es el estado normalizado
/// (<see cref="State"/>).
/// </summary>
public sealed class PatientFieldStamps
{
    /// <summary>La llave con el UpdatedAtUtc de la fila con que se mantuvieron las marcas.</summary>
    public const string RowStampKey = "UpdatedAtUtc";

    /// <summary>La llave del piso: lleva '#', que ninguna columna lleva.</summary>
    public const string FloorKey = "#piso";

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
    /// Lee el texto de la columna. Vacío, nulo o dañado = sin marcas (como una fila de antes del paso): también un objeto
    /// con una llave repetida (kotlinx se quedaría con la última; aquí cuenta como dañado, igual en los dos). Un valor que no
    /// es texto se ignora.
    /// </summary>
    public static PatientFieldStamps Parse(string? json)
    {
        var stamps = new PatientFieldStamps();
        if (string.IsNullOrWhiteSpace(json)) return stamps;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return stamps;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // Una llave repetida: dañado, sin marcas
                if (!seen.Add(property.Name)) return new PatientFieldStamps();
                if (property.Value.ValueKind == JsonValueKind.String) stamps._entries[property.Name] = property.Value.GetString()!;
            }
            return stamps;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // Dañado: sin marcas
            return new PatientFieldStamps();
        }
    }

    /// <summary>El texto canónico que se guarda (llaves ordenadas, sin espacios); "" sin ninguna marca.</summary>
    public string ToJson()
    {
        if (_entries.Count == 0) return string.Empty;
        var obj = new System.Text.Json.Nodes.JsonObject();
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

    /// <summary>El piso guardado (<see cref="FloorKey"/>); null si no hay.</summary>
    public DateTime? Floor
    {
        get => ParseStamp(this[FloorKey]);
        set => this[FloorKey] = value is { } v ? Format(v) : null;
    }

    /// <summary>
    /// Una escritura sin marcas: la fila tiene un UpdatedAtUtc mayor que el de sus marcas (o no tiene marcas). La escribió
    /// una PC anterior al paso o un camino que no pasó por el sello: lo que cambió no se sabe campo por campo.
    /// </summary>
    public bool IsStampless(DateTime rowUpdatedAtUtc) => RowStamp is not { } stamp || rowUpdatedAtUtc > stamp;

    /// <summary>La marca de una llave tal como la guarda esta fila: nunca más que su UpdatedAtUtc; null si no hay.</summary>
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

    /// <summary>
    /// Una llave que esta versión conoce (y a la que el piso le aplica): un campo de <paramref name="fieldColumns"/>, una de
    /// las tres hojas o un renglón de una hoja. Las demás son de una versión más nueva.
    /// </summary>
    public static bool IsKnownKey(string key, IReadOnlyCollection<string> fieldColumns)
    {
        if (fieldColumns.Contains(key)) return true;
        foreach (var sheet in PatientSheetCanonical.Columns)
        {
            if (key == sheet || TryParseRowKey(key, sheet, out _, out _)) return true;
        }
        return false;
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

    /// <summary>La marca más nueva de cualquier llave salvo <see cref="RowStampKey"/> (el piso cuenta); null sin marcas.</summary>
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

    // ================================================================== el estado normalizado

    /// <summary>
    /// El estado normalizado de una versión de la fila (con su <paramref name="rowUpdatedAtUtc"/>), sin la llave de la fila:
    /// <list type="bullet">
    /// <item>cada marca, nunca mayor que el UpdatedAtUtc de la fila;</item>
    /// <item>el piso es el mayor entre el guardado y, en una escritura sin marcas, su UpdatedAtUtc (sus llaves son todas
    /// anteriores);</item>
    /// <item>una llave conocida (<see cref="IsKnownKey"/>) que no rebasa el piso no se guarda: vale el piso.</item>
    /// </list>
    /// Dos versiones con el mismo estado y los mismos valores dicen lo mismo.
    /// </summary>
    public PatientFieldStamps State(DateTime rowUpdatedAtUtc, IReadOnlyCollection<string> fieldColumns, bool stampless) =>
        Normalize(_entries.Where(kv => kv.Key != RowStampKey).Select(kv => KeyValuePair.Create(kv.Key, Effective(kv.Key, rowUpdatedAtUtc)!)),
            stampless ? Format(rowUpdatedAtUtc) : null, fieldColumns);

    /// <inheritdoc cref="State(DateTime, IReadOnlyCollection{string}, bool)"/>
    public PatientFieldStamps State(DateTime rowUpdatedAtUtc, IReadOnlyCollection<string> fieldColumns) =>
        State(rowUpdatedAtUtc, fieldColumns, IsStampless(rowUpdatedAtUtc));

    private static PatientFieldStamps Normalize(IEnumerable<KeyValuePair<string, string>> entries, string? extraFloor, IReadOnlyCollection<string> fieldColumns)
    {
        var state = new PatientFieldStamps(entries);
        state[FloorKey] = Max(state[FloorKey], extraFloor);
        state.DropBelowFloor(fieldColumns);
        return state;
    }

    /// <summary>Quita las llaves conocidas que no rebasan el piso (valen el piso).</summary>
    public void DropBelowFloor(IReadOnlyCollection<string> fieldColumns)
    {
        if (this[FloorKey] is not { } floor) return;
        foreach (var key in _entries.Keys.ToList())
        {
            if (key == FloorKey || key == RowStampKey || !IsKnownKey(key, fieldColumns)) continue;
            if (string.CompareOrdinal(_entries[key], floor) <= 0) _entries.Remove(key);
        }
    }

    /// <summary>
    /// Purga los renglones quitados (los de cualquier hoja) cuya marca de quitado es más de 90 días anterior a la marca más
    /// nueva del expediente (el piso cuenta): su llave de quitado y la del renglón. Depende solo de las marcas, así dos
    /// equipos purgan lo mismo y la purga sola nunca se vuelve una diferencia que suba.
    /// </summary>
    public void Purge(IEnumerable<string> sheetColumns)
    {
        if (Newest() is { } newest) PurgeBefore(sheetColumns, newest - RemovedRowRetention);
    }

    /// <summary>
    /// Purga los renglones quitados antes de <paramref name="horizon"/>: los que sigan quitados (su marca no rebasa la de
    /// quitado) pierden sus dos llaves. La revisión de una consulta recibida la usa con el horizonte de este equipo.
    /// </summary>
    public void PurgeBefore(IEnumerable<string> sheetColumns, DateTime horizon)
    {
        var limit = Format(horizon);
        foreach (var column in sheetColumns)
        {
            foreach (var key in _entries.Keys.ToList())
            {
                if (!TryParseRowKey(key, column, out var rowId, out var removed) || !removed) continue;
                if (!_entries.TryGetValue(key, out var removedAt) || string.CompareOrdinal(removedAt, limit) >= 0) continue;
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
    /// Kotlin). Parte del estado de la fila antes de guardar (<see cref="State"/> con su <paramref name="previousUpdatedAtUtc"/>;
    /// en un alta, null: sin piso por escritura sin marcas) y:
    /// <list type="bullet">
    /// <item>cada campo que cambió por su significado (<paramref name="changedFields"/>) toma la marca nueva;</item>
    /// <item>en una hoja que cambió (<paramref name="changedSheets"/>, el texto de antes y el de después), cada renglón agregado
    /// o cuya forma canónica cambió toma la marca en su llave, cada renglón que ya no está la toma en su llave de quitado, y
    /// si cambió la lista de RowId, la hoja la toma en su propia llave;</item>
    /// <item>una escritura de la fila completa (<paramref name="wholeRow"/>: «Volver a aplicar») sube el piso a la marca
    /// nueva en lugar de marcar campo por campo;</item>
    /// <item>al final, la llave de la fila = la marca nueva, y se purgan los renglones quitados viejos.</item>
    /// </list>
    /// Una fila que era una escritura sin marcas conserva así su UpdatedAtUtc anterior como piso: lo que tenía, lo tenía desde
    /// entonces, aunque ahora solo se marque lo editado.
    /// </summary>
    public static PatientFieldStamps ApplyLocalWrite(string? before, DateTime? previousUpdatedAtUtc, DateTime stamp, IEnumerable<string> changedFields,
        IEnumerable<(string Column, string? Before, string? After)> changedSheets, Guid patientSyncId, IReadOnlyCollection<string> fieldColumns,
        bool wholeRow = false)
    {
        var parsed = Parse(before);
        var stamps = previousUpdatedAtUtc is { } previous
            ? parsed.State(previous, fieldColumns)
            : parsed.State(stamp, fieldColumns, stampless: false);
        var text = Format(stamp);
        if (wholeRow)
        {
            stamps[FloorKey] = text;
        }
        else
        {
            foreach (var field in changedFields) stamps[field] = text;
            foreach (var (column, sheetBefore, sheetAfter) in changedSheets)
            {
                var old = PatientSheetCanonical.ReadRows(column, sheetBefore, patientSyncId);
                var current = PatientSheetCanonical.ReadRows(column, sheetAfter, patientSyncId);
                var oldById = old.ToDictionary(r => r.RowId);
                var currentIds = current.Select(r => r.RowId).ToHashSet();
                foreach (var row in current)
                {
                    // Por su texto canónico: dos JsonObject nunca son iguales como objetos
                    if (!oldById.TryGetValue(row.RowId, out var previousRow) || previousRow.Text != row.Text)
                    {
                        stamps[RowKey(column, row.RowId)] = text;
                    }
                }
                foreach (var row in old.Where(r => !currentIds.Contains(r.RowId)))
                {
                    stamps[RemovedKey(column, row.RowId)] = text;
                }
                if (!old.Select(r => r.RowId).SequenceEqual(current.Select(r => r.RowId)))
                {
                    stamps[column] = text;
                }
            }
        }
        stamps.DropBelowFloor(fieldColumns);
        stamps.Purge(PatientSheetCanonical.Columns);
        stamps.RowStamp = stamp;
        return stamps;
    }
}
