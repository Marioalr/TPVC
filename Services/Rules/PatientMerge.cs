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
/// <item><b>Por campo gana la marca mayor</b> (<see cref="PatientFieldStamps"/>); una marca que falta cuenta como la mínima.</item>
/// <item><b>Escritura sin marcas:</b> si el <c>UpdatedAtUtc</c> de una versión es mayor que el de sus marcas
/// (<see cref="PatientFieldStamps.IsStampless"/>: una PC v1.19–v1.38, o una escritura que no pasó por el sello), cada campo
/// en que difiere de la otra toma ese <c>UpdatedAtUtc</c> como marca: equivale al LWW de siempre, no a una moneda al aire.</item>
/// <item><b>Marcas iguales y valores distintos:</b> gana el valor con el texto canónico mayor en orden ordinal, así los dos
/// lados eligen lo mismo.</item>
/// <item><b>Hojas por renglón</b> (por <c>RowId</c>): unión de los renglones; cada uno con su marca y su marca de quitado
/// (vive si la suya es mayor; con marcas iguales gana quitado); su contenido, el del lado con la marca mayor (con marcas
/// iguales, el texto canónico mayor). El orden es el del lado con la marca de orden mayor (con marcas iguales, la lista de
/// RowId de sus renglones vivos con el texto canónico mayor), y los renglones vivos que solo tiene el otro lado van al final
/// en su orden. Una versión sin marcas escribió su hoja completa con su <c>UpdatedAtUtc</c>: cada renglón que tiene cuenta con esa marca, y
/// uno que no tiene y la otra sí, como quitado con ella.</item>
/// <item>Las llaves que esta versión no conoce se conservan; gana la marca mayor.</item>
/// <item><b>¿Cambió algo?</b> Se compara por significado (<see cref="CanonicalVersion"/>: valores canónicos, hojas
/// canónicas y marcas, sin la llave de la fila). Si el resultado es otro que la versión remota, la marca de la fila queda en
/// <c>max(L, R) + 1 ms</c> y la fila se registra para subir; si no, se queda la versión remota tal cual.</item>
/// </list>
/// Es conmutativa e idempotente y absorbe (combinar con una versión que ya incluye el resultado da ese resultado: nadie vuelve
/// a subir). Entre versiones con marcas, lo que se sabe (valores, marcas y qué renglones viven) es además asociativo y solo
/// crece; el orden de los renglones depende de qué versión ganó el orden, y cuando todos saben lo mismo gana la lista de
/// texto canónico mayor. Por eso los equipos convergen sin subidas repetidas, aunque la nube rechace subidas a la mitad
/// (<c>PatientMergeTests</c>).
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

        /// <summary>Las marcas combinadas, sin la llave de la fila.</summary>
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
        var sl = PatientFieldStamps.Parse(local.FieldStampsJson);
        var sr = PatientFieldStamps.Parse(remote.FieldStampsJson);
        bool stamplessL = sl.IsStampless(local.UpdatedAtUtc);
        bool stamplessR = sr.IsStampless(remote.UpdatedAtUtc);
        var uL = PatientFieldStamps.Format(local.UpdatedAtUtc);
        var uR = PatientFieldStamps.Format(remote.UpdatedAtUtc);
        var syncId = local.SyncId;

        // Toda llave (también las que esta versión no conoce): la marca mayor, cada una como cuenta en su fila
        var merged = new PatientFieldStamps();
        foreach (var key in sl.Entries.Keys.Union(sr.Entries.Keys, StringComparer.Ordinal))
        {
            if (key == PatientFieldStamps.RowStampKey) continue;
            merged[key] = PatientFieldStamps.Max(sl.Effective(key, local.UpdatedAtUtc), sr.Effective(key, remote.UpdatedAtUtc));
        }

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
                var (value, source) = MergeSheet(column, CanonicalJson.AsString(mine), CanonicalJson.AsString(theirs), sl, sr,
                    local.UpdatedAtUtc, remote.UpdatedAtUtc, stamplessL, stamplessR, uL, uR, syncId, merged);
                values[column] = value == null ? null : JsonValue.Create(value);
                sources[column] = source;
                continue;
            }

            var a = CanonicalJson.Write(mine);
            var b = CanonicalJson.Write(theirs);
            if (a == b)
            {
                values[column] = CanonicalJson.Clone(mine);
                sources[column] = Source.Local;
                continue;
            }

            var el = stamplessL ? uL : sl.Effective(column, local.UpdatedAtUtc);
            var er = stamplessR ? uR : sr.Effective(column, remote.UpdatedAtUtc);
            int cmp = PatientFieldStamps.Compare(el, er);
            if (cmp == 0) cmp = CanonicalJson.CompareOrdinal(a, b);
            values[column] = CanonicalJson.Clone(cmp > 0 ? mine : theirs);
            sources[column] = cmp > 0 ? Source.Local : Source.Remote;
            merged[column] = PatientFieldStamps.Max(el, er);
        }

        merged.Purge(PatientSheetCanonical.Columns);

        var canonicalMerged = CanonicalVersion(values, merged, columns, syncId);
        bool differsFromRemote = canonicalMerged != CanonicalVersion(remoteValues, sr, columns, syncId);
        bool differsFromLocal = canonicalMerged != CanonicalVersion(local.Values, sl, columns, syncId);

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

    private static (string? Text, Source Source) MergeSheet(string column, string? textL, string? textR, PatientFieldStamps sl, PatientFieldStamps sr,
        DateTime updatedL, DateTime updatedR, bool stamplessL, bool stamplessR, string uL, string uR, Guid syncId, PatientFieldStamps merged)
    {
        var rowsL = PatientSheetCanonical.ReadRows(column, textL, syncId);
        var rowsR = PatientSheetCanonical.ReadRows(column, textR, syncId);
        var byL = rowsL.ToDictionary(r => r.RowId);
        var byR = rowsR.ToDictionary(r => r.RowId);
        var seqL = rowsL.Select(r => r.RowId).ToList();
        var seqR = rowsR.Select(r => r.RowId).ToList();

        // La marca del orden de la hoja (una versión sin marcas lo puso con su UpdatedAtUtc: escribió su hoja completa)
        var orderL = stamplessL ? PatientFieldStamps.Max(sl.Effective(column, updatedL), uL) : sl.Effective(column, updatedL);
        var orderR = stamplessR ? PatientFieldStamps.Max(sr.Effective(column, updatedR), uR) : sr.Effective(column, updatedR);
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
            var eL = sl.Effective(rowKey, updatedL);
            var rL = sl.Effective(removedKey, updatedL);
            var eR = sr.Effective(rowKey, updatedR);
            var rR = sr.Effective(removedKey, updatedR);
            bool inL = byL.TryGetValue(id, out var rowL);
            bool inR = byR.TryGetValue(id, out var rowR);

            // Una versión sin marcas escribió su hoja completa con su UpdatedAtUtc: lo que tiene, lo tiene desde entonces (aunque
            // sus marcas viejas digan otra cosa), y lo que la otra tiene y ella no, lo quitó entonces
            if (stamplessL)
            {
                if (inL) eL = PatientFieldStamps.Max(eL, uL);
                else if (inR) rL = PatientFieldStamps.Max(rL, uL);
            }
            if (stamplessR)
            {
                if (inR) eR = PatientFieldStamps.Max(eR, uR);
                else if (inL) rR = PatientFieldStamps.Max(rR, uR);
            }

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
    /// Una versión por su significado: <c>{"stamps":{…},"values":{…}}</c> en forma canónica, con los valores de las columnas
    /// (las hojas como arreglo de renglones canónicos, <see cref="PatientSheetCanonical"/>) y las marcas sin la llave de la
    /// fila. Dos versiones que dicen lo mismo dan el mismo texto, en C# y en Kotlin.
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
