using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TPVC.Services.Rules;

/// <summary>
/// Paso 8.22 (H1b): la combinación por campo de un expediente al bajarlo. Pura (sin base ni WPF): la PC la usa al aplicar
/// una fila de <c>Patients</c> y Kotlin la repite igual (<c>contracts/golden/patient-merge.json</c>). La nube sigue con LWW
/// por fila; lo que la nube rechaza se repara en la siguiente combinación.
/// <list type="bullet">
/// <item><b>Cada versión se normaliza</b> (<see cref="PatientFieldStamps.State"/>): sus marcas, nunca mayores que su fila, y
/// su piso. Una escritura sin marcas (una PC v1.19–v1.38, o la fila de antes de actualizar) tiene de piso su
/// <c>UpdatedAtUtc</c>: escribió la fila completa en ese momento.</item>
/// <item><b>Por campo gana la marca mayor</b>, y la marca de un campo vale al menos el piso de su versión: lo que una fila sin
/// marcas tenía, lo tenía desde su <c>UpdatedAtUtc</c> (equivale al LWW de siempre, no a una moneda al aire). Sin piso ni
/// marca, la mínima.</item>
/// <item><b>Marcas iguales y valores distintos:</b> gana el valor con el texto canónico mayor en orden ordinal, así los dos
/// lados eligen lo mismo.</item>
/// <item><b>Hojas por renglón</b> (por <c>RowId</c>): unión de los renglones; cada uno con su marca y su marca de quitado
/// (vive si la suya es mayor; con marcas iguales gana quitado). Un renglón que una versión tiene vale al menos su piso; uno
/// que no tiene cuenta como quitado en el mayor entre su piso y su propia marca de quitado. Su contenido, el del lado con la marca mayor (con marcas
/// iguales, el texto canónico mayor). El orden es el del lado con la marca de orden mayor (también al menos el piso; con
/// marcas iguales, la lista de RowId de sus renglones vivos con el texto canónico mayor), y los renglones vivos que solo tiene
/// el otro lado van al final en su orden.</item>
/// <item>El piso del resultado es el mayor de los dos, y una marca que no lo rebasa no se guarda. Las llaves que esta versión
/// no conoce se conservan; gana la marca mayor.</item>
/// <item><b>¿Cambió algo?</b> Se compara por significado (<see cref="CanonicalVersion"/>: valores canónicos, hojas
/// canónicas y el estado normalizado, sin la llave de la fila). Si el resultado es otro que la versión remota, la marca de la
/// fila queda en <c>max(L, R) + 1 ms</c> y la fila se registra para subir; si no, se queda la versión remota tal cual. Dos
/// filas sin marcas iguales (la primera sincronización después de actualizar) no suben nada.</item>
/// </list>
/// Es conmutativa e idempotente y absorbe (combinar con una versión que ya incluye el resultado da ese resultado: nadie vuelve
/// a subir); lo que se sabe (valores, estado y qué renglones viven) es además asociativo y solo crece; el orden de los
/// renglones depende de qué versión ganó el orden, y cuando todos saben lo mismo gana la lista de texto canónico mayor. Por
/// eso los equipos convergen sin subidas repetidas, aunque la nube rechace subidas a la mitad (<c>PatientMergeTests</c>).
/// </summary>
public static class PatientMerge
{
    /// <summary>Lo que se suma a la marca mayor de las dos cuando el resultado sube.</summary>
    public static readonly TimeSpan MergeStep = SyncRules.StampStep;

    /// <summary>De dónde sale el valor combinado de una columna.</summary>
    public enum Source
    {
        /// <summary>Se queda el de aquí (también si los dos dicen lo mismo).</summary>
        Local,

        /// <summary>El de la nube.</summary>
        Remote,

        /// <summary>Una hoja con renglones de los dos lados: un texto nuevo.</summary>
        Merged
    }

    /// <summary>Una versión del expediente: la de aquí o la de la nube.</summary>
    public sealed class Version
    {
        public required Guid SyncId { get; init; }
        public required DateTime UpdatedAtUtc { get; init; }

        /// <summary>El texto de <c>FieldStampsJson</c> (vacío o nulo = sin marcas).</summary>
        public string? FieldStampsJson { get; init; }

        /// <summary>
        /// Columna remota → valor canónico (<see cref="CanonicalJson.FromValue"/>). En una hoja, el texto guardado como
        /// valor de texto (o null). Una columna que falta en la versión remota cuenta como igual a la de aquí.
        /// </summary>
        public required IReadOnlyDictionary<string, JsonNode?> Values { get; init; }
    }

    /// <summary>El resultado de combinar.</summary>
    public sealed class Result
    {
        /// <summary>Los valores combinados (en una hoja, el texto que se guarda).</summary>
        public required Dictionary<string, JsonNode?> Values { get; init; }

        public required Dictionary<string, Source> Sources { get; init; }

        /// <summary>El estado combinado (el piso y las marcas que lo rebasan), sin la llave de la fila.</summary>
        public required PatientFieldStamps Stamps { get; init; }

        /// <summary>El resultado dice otra cosa que la versión remota: se guarda con marca nueva y sube.</summary>
        public required bool DiffersFromRemote { get; init; }

        /// <summary>El resultado dice otra cosa que la versión de aquí.</summary>
        public required bool DiffersFromLocal { get; init; }

        /// <summary>La marca de la fila: la remota si no difiere de ella; si no, <c>max(L, R) + 1 ms</c>.</summary>
        public required DateTime UpdatedAtUtc { get; init; }

        /// <summary>El texto de <c>FieldStampsJson</c> que se guarda: el remoto tal cual, o las marcas combinadas con la llave de la fila.</summary>
        public required string FieldStampsJson { get; init; }
    }

    /// <summary>Combina la versión de aquí con la de la nube, en las columnas <paramref name="columns"/>.</summary>
    public static Result Merge(Version local, Version remote, IReadOnlyCollection<string> columns)
    {
        var fieldColumns = FieldColumns(columns);
        var sl = PatientFieldStamps.Parse(local.FieldStampsJson).State(local.UpdatedAtUtc, fieldColumns);
        var sr = PatientFieldStamps.Parse(remote.FieldStampsJson).State(remote.UpdatedAtUtc, fieldColumns);
        var floorL = sl[PatientFieldStamps.FloorKey];
        var floorR = sr[PatientFieldStamps.FloorKey];
        var syncId = local.SyncId;

        // Las llaves que esta versión no conoce: la marca mayor, tal cual (el piso no las toca)
        var merged = new PatientFieldStamps();
        foreach (var key in sl.Entries.Keys.Union(sr.Entries.Keys, StringComparer.Ordinal))
        {
            if (key == PatientFieldStamps.FloorKey || PatientFieldStamps.IsKnownKey(key, fieldColumns)) continue;
            merged[key] = PatientFieldStamps.Max(sl[key], sr[key]);
        }
        merged[PatientFieldStamps.FloorKey] = PatientFieldStamps.Max(floorL, floorR);

        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var sources = new Dictionary<string, Source>(StringComparer.Ordinal);
        var remoteValues = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            var mine = local.Values.GetValueOrDefault(column);
            var theirs = remote.Values.TryGetValue(column, out var r) ? r : mine;
            remoteValues[column] = theirs;

            if (PatientSheetCanonical.IsSheet(column))
            {
                var (value, source) = MergeSheet(column, CanonicalJson.AsString(mine), CanonicalJson.AsString(theirs), sl, sr, floorL, floorR, syncId, merged);
                values[column] = value == null ? null : JsonValue.Create(value);
                sources[column] = source;
                continue;
            }

            // La marca de un campo vale al menos el piso de su versión
            var el = PatientFieldStamps.Max(floorL, sl[column]);
            var er = PatientFieldStamps.Max(floorR, sr[column]);
            merged[column] = PatientFieldStamps.Max(el, er);
            var a = CanonicalJson.Write(mine);
            var b = CanonicalJson.Write(theirs);
            if (a == b)
            {
                values[column] = CanonicalJson.Clone(mine);
                sources[column] = Source.Local;
                continue;
            }

            int cmp = PatientFieldStamps.Compare(el, er);
            if (cmp == 0) cmp = CanonicalJson.CompareOrdinal(a, b);
            values[column] = CanonicalJson.Clone(cmp > 0 ? mine : theirs);
            sources[column] = cmp > 0 ? Source.Local : Source.Remote;
        }

        merged.DropBelowFloor(fieldColumns);
        merged.Purge(PatientSheetCanonical.Columns);

        var canonicalMerged = CanonicalVersion(values, merged, columns, syncId);
        bool differsFromRemote = canonicalMerged != CanonicalVersion(remoteValues, Purged(sr), columns, syncId);
        bool differsFromLocal = canonicalMerged != CanonicalVersion(local.Values, Purged(sl), columns, syncId);

        DateTime stamp;
        string stampsJson;
        if (differsFromRemote)
        {
            stamp = (local.UpdatedAtUtc > remote.UpdatedAtUtc ? local.UpdatedAtUtc : remote.UpdatedAtUtc) + MergeStep;
            var withRow = merged.Clone();
            withRow.RowStamp = stamp;
            stampsJson = withRow.ToJson();
        }
        else
        {
            stamp = remote.UpdatedAtUtc;
            stampsJson = remote.FieldStampsJson ?? string.Empty;
        }

        return new Result
        {
            Values = values,
            Sources = sources,
            Stamps = merged,
            DiffersFromRemote = differsFromRemote,
            DiffersFromLocal = differsFromLocal,
            UpdatedAtUtc = stamp,
            FieldStampsJson = stampsJson
        };
    }

    /// <summary>Las columnas de campos (sin las hojas): las llaves que el piso domina además de las de las hojas.</summary>
    public static IReadOnlyCollection<string> FieldColumns(IEnumerable<string> columns) =>
        columns.Where(c => !PatientSheetCanonical.IsSheet(c)).ToHashSet(StringComparer.Ordinal);

    private static PatientFieldStamps Purged(PatientFieldStamps state)
    {
        var copy = state.Clone();
        copy.Purge(PatientSheetCanonical.Columns);
        return copy;
    }

    private static (string? Text, Source Source) MergeSheet(string column, string? textL, string? textR, PatientFieldStamps sl, PatientFieldStamps sr,
        string? floorL, string? floorR, Guid syncId, PatientFieldStamps merged)
    {
        var rowsL = PatientSheetCanonical.ReadRows(column, textL, syncId);
        var rowsR = PatientSheetCanonical.ReadRows(column, textR, syncId);
        var byL = rowsL.ToDictionary(r => r.RowId);
        var byR = rowsR.ToDictionary(r => r.RowId);
        var seqL = rowsL.Select(r => r.RowId).ToList();
        var seqR = rowsR.Select(r => r.RowId).ToList();

        // La marca del orden de la hoja, al menos el piso de su versión
        var orderL = PatientFieldStamps.Max(floorL, sl[column]);
        var orderR = PatientFieldStamps.Max(floorR, sr[column]);
        merged[column] = PatientFieldStamps.Max(orderL, orderR);

        // Todos los renglones de los que se sabe algo: los de las dos hojas y los de las marcas
        var ids = new List<Guid>(seqL);
        ids.AddRange(seqR.Where(id => !byL.ContainsKey(id)));
        foreach (var key in sl.Entries.Keys.Concat(sr.Entries.Keys))
        {
            if (PatientFieldStamps.TryParseRowKey(key, column, out var id, out _) && !ids.Contains(id)) ids.Add(id);
        }

        var live = new HashSet<Guid>();
        var chosen = new Dictionary<Guid, PatientSheetCanonical.Row>();
        foreach (var id in ids)
        {
            var rowKey = PatientFieldStamps.RowKey(column, id);
            var removedKey = PatientFieldStamps.RemovedKey(column, id);
            bool inL = byL.TryGetValue(id, out var rowL);
            bool inR = byR.TryGetValue(id, out var rowR);

            // Lo que una versión tiene, lo tiene al menos desde su piso; lo que no tiene, lo quitó al menos en su piso (o en su
            // propia marca de quitado, si es mayor)
            var eL = inL ? PatientFieldStamps.Max(floorL, sl[rowKey]) : sl[rowKey];
            var rL = inL ? sl[removedKey] : PatientFieldStamps.Max(floorL, sl[removedKey]);
            var eR = inR ? PatientFieldStamps.Max(floorR, sr[rowKey]) : sr[rowKey];
            var rR = inR ? sr[removedKey] : PatientFieldStamps.Max(floorR, sr[removedKey]);

            var e = PatientFieldStamps.Max(eL, eR);
            var removed = PatientFieldStamps.Max(rL, rR);
            merged[rowKey] = e;
            merged[removedKey] = removed;

            if (!inL && !inR) continue;
            if (removed != null && (e == null || PatientFieldStamps.Compare(e, removed) <= 0)) continue;
            live.Add(id);

            if (inL && inR)
            {
                int cmp = PatientFieldStamps.Compare(eL, eR);
                if (cmp == 0) cmp = string.CompareOrdinal(rowL!.Text, rowR!.Text);
                chosen[id] = cmp >= 0 ? rowL! : rowR!;
            }
            else
            {
                chosen[id] = inL ? rowL! : rowR!;
            }
        }

        // El orden del lado con la marca de orden mayor (con marcas iguales, la lista de los renglones vivos con el texto
        // canónico mayor); los vivos que solo tiene el otro, al final en su orden. Se compara ya sin los renglones muertos: así
        // combinar otra vez con una versión que ya incluye el resultado da el mismo orden
        var liveL = seqL.Where(live.Contains).ToList();
        var liveR = seqR.Where(live.Contains).ToList();
        int order = PatientFieldStamps.Compare(orderL, orderR);
        if (order == 0) order = string.CompareOrdinal(SequenceText(liveL), SequenceText(liveR));
        var (first, second) = order >= 0 ? (liveL, liveR) : (liveR, liveL);
        var result = new List<Guid>(first);
        result.AddRange(second.Where(id => !first.Contains(id)));

        var rows = result.Select(id => chosen[id]).ToList();
        var canonical = CanonicalJson.Write(new JsonArray(rows.Select(r => (JsonNode)r.Canonical.DeepClone()).ToArray()));
        // Si dice lo mismo que una de las dos, se queda su texto; primero el de la nube (como antes con el LWW: los Id locales y
        // el formato de quien la escribió, que los lectores entienden igual)
        if (canonical == PatientSheetCanonical.CanonicalText(column, textR, syncId)) return (textR, textR == textL ? Source.Local : Source.Remote);
        if (canonical == PatientSheetCanonical.CanonicalText(column, textL, syncId)) return (textL, Source.Local);
        return (PatientSheetCanonical.Write(column, rows.Select(r => r.Value)), Source.Merged);
    }

    /// <summary>La lista de RowId de una hoja como texto canónico (el desempate del orden).</summary>
    public static string SequenceText(IEnumerable<Guid> ids) =>
        CanonicalJson.Write(new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(SyncIds.Format(id))).ToArray()));

    /// <summary>
    /// El significado de una versión tal como la guarda una fila: sus valores y su estado normalizado (con su piso, sin las
    /// marcas que no lo rebasan y con los renglones quitados viejos ya purgados).
    /// </summary>
    public static string Meaning(Version version, IReadOnlyCollection<string> columns) =>
        CanonicalVersion(version.Values,
            Purged(PatientFieldStamps.Parse(version.FieldStampsJson).State(version.UpdatedAtUtc, FieldColumns(columns))),
            columns, version.SyncId);

    /// <summary>
    /// Una versión por su significado: <c>{"stamps":{…},"values":{…}}</c> en forma canónica, con los valores de las columnas
    /// (las hojas como arreglo de renglones canónicos, <see cref="PatientSheetCanonical"/>) y el estado normalizado
    /// (<see cref="PatientFieldStamps.State"/>: el piso <c>#piso</c> y las marcas que lo rebasan) sin la llave de la fila. Dos
    /// versiones que dicen lo mismo dan el mismo texto, en C# y en Kotlin.
    /// </summary>
    public static string CanonicalVersion(IReadOnlyDictionary<string, JsonNode?> values, PatientFieldStamps stamps,
        IEnumerable<string> columns, Guid patientSyncId)
    {
        var valuesNode = new JsonObject();
        foreach (var column in columns)
        {
            var value = values.GetValueOrDefault(column);
            valuesNode[column] = PatientSheetCanonical.IsSheet(column)
                ? PatientSheetCanonical.Canonical(column, CanonicalJson.AsString(value), patientSyncId)
                : CanonicalJson.Clone(value);
        }
        var stampsNode = new JsonObject();
        foreach (var (key, value) in stamps.Entries)
        {
            if (key != PatientFieldStamps.RowStampKey) stampsNode[key] = value;
        }
        return CanonicalJson.Write(new JsonObject { ["stamps"] = stampsNode, ["values"] = valuesNode });
    }
}
