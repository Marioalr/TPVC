using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using TPVC.Models;

namespace TPVC.Services.Rules;

/// <summary>
/// Paso 8.22: las hojas del paciente (<c>Active*Json</c>) por su significado, renglón por renglón, para combinarlas y para
/// saber si dos versiones dicen lo mismo. Un renglón se lee como lo lee la PC (nombres sin distinguir mayúsculas, lo que falta
/// con su valor por omisión, el <c>RowId</c> determinista de un renglón viejo, paso 8.18) y se escribe en forma canónica
/// (<see cref="CanonicalJson"/>) con <b>todos</b> sus campos conocidos (<see cref="Fields"/>, también los que la PC omite
/// cuando valen lo de siempre) más los que no conoce, tal cual. No entran los Id locales del equipo que escribió
/// (<see cref="LocalIdFields"/>: <c>ChargedLineId</c> y <c>ServiceId</c>, que la PC sigue escribiendo para las PC anteriores
/// y Kotlin no escribe): que cada equipo escriba los suyos no es un cambio. Desde la revisión 14 un servicio lleva además su
/// lista de insumos (<c>Supplies</c>, null sin lista), cada insumo con todos sus campos conocidos menos su <c>ChargedLineId</c>:
/// la lista es parte del renglón, así que cambiarla marca el renglón y la combinación la lleva con él. Desde la revisión 16 un
/// renglón de receta lleva además <c>ActiveIngredient</c> y <c>ShowActiveIngredient</c> (null en uno sin ellos): mostrar u
/// ocultar el principio activo cambia el renglón como cualquier otra edición. Vectores en
/// <c>contracts/golden/canonical-json.json</c>.
/// </summary>
public static class PatientSheetCanonical
{
    /// <summary>Las tres hojas, en el orden del contrato.</summary>
    public static readonly IReadOnlyList<string> Columns = new[]
    {
        PatientSheetRows.PrescriptionColumn,
        PatientSheetRows.SuppliesColumn,
        PatientSheetRows.ServicesColumn
    };

    /// <summary>Los campos conocidos de cada hoja que entran a la forma canónica, con el nombre con que se escriben.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [PatientSheetRows.PrescriptionColumn] = new[]
        {
            nameof(PatientPrescriptionItem.RowId), nameof(PatientPrescriptionItem.MedicineName), nameof(PatientPrescriptionItem.Dosage),
            nameof(PatientPrescriptionItem.RecommendedQuantity), nameof(PatientPrescriptionItem.Presentation),
            nameof(PatientPrescriptionItem.DispenseInClinic), nameof(PatientPrescriptionItem.ActiveIngredient),
            nameof(PatientPrescriptionItem.ShowActiveIngredient)
        },
        [PatientSheetRows.SuppliesColumn] = new[]
        {
            nameof(PatientSupplyItem.RowId), nameof(PatientSupplyItem.SupplyName), nameof(PatientSupplyItem.Instructions),
            nameof(PatientSupplyItem.Quantity), nameof(PatientSupplyItem.UnitPrice), nameof(PatientSupplyItem.ChargedConsultationFolio),
            nameof(PatientSupplyItem.ChargedUnits), nameof(PatientSupplyItem.ChargedLineKind), nameof(PatientSupplyItem.ChargedLineSyncId)
        },
        [PatientSheetRows.ServicesColumn] = new[]
        {
            nameof(PatientServiceItem.RowId), nameof(PatientServiceItem.ServiceSyncId), nameof(PatientServiceItem.ServiceCode),
            nameof(PatientServiceItem.ServiceName), nameof(PatientServiceItem.Category), nameof(PatientServiceItem.Notes),
            nameof(PatientServiceItem.Price), nameof(PatientServiceItem.Quantity), nameof(PatientServiceItem.ChargedConsultationFolio),
            nameof(PatientServiceItem.ChargedUnits), nameof(PatientServiceItem.Supplies)
        }
    };

    /// <summary>
    /// Revisión 13: los campos conocidos de cada insumo de un servicio (<see cref="PatientServiceItem.Supplies"/>), con todos sus
    /// valores, igual que un renglón; <c>ChargedLineId</c> es su Id local y no entra (<see cref="ServiceSupplyLocalIdFields"/>).
    /// La lista va en su orden; sin lista es null.
    /// </summary>
    public static readonly IReadOnlyList<string> ServiceSupplyFields = new[]
    {
        nameof(PatientServiceSupply.SupplyName), nameof(PatientServiceSupply.Quantity), nameof(PatientServiceSupply.UnitPrice),
        nameof(PatientServiceSupply.ChargedLineKind), nameof(PatientServiceSupply.ChargedLineSyncId)
    };

    /// <summary>El Id local de cada insumo de un servicio: viaja, pero no es parte de su significado.</summary>
    public static readonly IReadOnlyList<string> ServiceSupplyLocalIdFields = new[] { nameof(PatientServiceSupply.ChargedLineId) };

    /// <summary>Los Id locales del equipo que escribió el renglón: viajan, pero no son parte de su significado.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> LocalIdFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [PatientSheetRows.PrescriptionColumn] = Array.Empty<string>(),
        [PatientSheetRows.SuppliesColumn] = new[] { nameof(PatientSupplyItem.ChargedLineId) },
        [PatientSheetRows.ServicesColumn] = new[] { nameof(PatientServiceItem.ServiceId) }
    };

    public static bool IsSheet(string column) => Fields.ContainsKey(column);

    /// <summary>Un renglón de una hoja: su identidad, el renglón como lo usa la PC y su forma canónica.</summary>
    public sealed record Row(Guid RowId, IPatientSheetRow Value, JsonObject Canonical, string Text);

    /// <summary>
    /// Los renglones de una hoja guardada, en orden, como los lee la PC: un texto vacío o que no se puede leer es una hoja
    /// vacía, un elemento nulo no es renglón, y un renglón sin RowId (o repetido) recibe el determinista por su posición.
    /// </summary>
    public static List<Row> ReadRows(string column, string? json, Guid patientSyncId)
    {
        var rows = new List<Row>();
        foreach (var value in PatientRecord.ReadSheetRows(column, json, patientSyncId))
        {
            if (value?.RowId is not { } id) continue;
            var canonical = CanonicalRow(column, value);
            rows.Add(new Row(id, value, canonical, CanonicalJson.Write(canonical)));
        }
        return rows;
    }

    /// <summary>La hoja en forma canónica: el arreglo de sus renglones canónicos, en su orden.</summary>
    public static JsonArray Canonical(string column, string? json, Guid patientSyncId) =>
        new(ReadRows(column, json, patientSyncId).Select(r => (JsonNode)r.Canonical.DeepClone()).ToArray());

    /// <summary>El texto canónico de una hoja guardada.</summary>
    public static string CanonicalText(string column, string? json, Guid patientSyncId) => CanonicalJson.Write(Canonical(column, json, patientSyncId));

    /// <summary>El texto de la hoja con estos renglones, con el formato de la PC.</summary>
    public static string Write(string column, IEnumerable<IPatientSheetRow> rows) => PatientRecord.WriteSheetRows(column, rows);

    /// <summary>
    /// Paso 8.22: lo que escribe el guardado de una hoja desde una copia (la ficha o la consulta abiertas, paso 8.21): las
    /// ediciones de la copia contra su foto (<paramref name="baseline"/>), renglón por renglón, puestas sobre la hoja que está
    /// en la base (<paramref name="stored"/>). En el orden de la copia: un renglón que la copia agregó o cambió va como lo
    /// tiene la copia (aunque otro equipo lo haya quitado: la edición es más nueva); uno que no tocó va como está en la base
    /// (otro equipo pudo cambiarlo), o no va si otro equipo lo quitó. Después, los renglones que llegaron de otro equipo y la
    /// copia no conoce, en el orden de la base. Los que la copia quitó no van. Si el resultado dice lo mismo que la copia,
    /// regresa el texto de la copia.
    /// </summary>
    public static string Rebase(string column, string? baseline, string? edited, string? stored, Guid patientSyncId)
    {
        var before = ReadRows(column, baseline, patientSyncId).ToDictionary(r => r.RowId);
        var copy = ReadRows(column, edited, patientSyncId);
        var inBase = ReadRows(column, stored, patientSyncId);
        var baseById = inBase.ToDictionary(r => r.RowId);
        var copyIds = copy.Select(r => r.RowId).ToHashSet();

        var result = new List<Row>();
        foreach (var row in copy)
        {
            bool changed = !before.TryGetValue(row.RowId, out var previous) || previous.Text != row.Text;
            if (changed) result.Add(row);
            else if (baseById.TryGetValue(row.RowId, out var current)) result.Add(current);
        }
        result.AddRange(inBase.Where(r => !before.ContainsKey(r.RowId) && !copyIds.Contains(r.RowId)));

        var canonical = CanonicalJson.Write(new JsonArray(result.Select(r => (JsonNode)r.Canonical.DeepClone()).ToArray()));
        return canonical == CanonicalText(column, edited, patientSyncId) ? edited ?? string.Empty : Write(column, result.Select(r => r.Value));
    }

    private static readonly Dictionary<(Type, string), PropertyInfo> Properties = new();

    private static PropertyInfo Property(Type type, string name)
    {
        lock (Properties)
        {
            if (!Properties.TryGetValue((type, name), out var property))
            {
                property = type.GetProperty(name) ?? throw new InvalidOperationException($"{type.Name} no tiene {name}.");
                Properties[(type, name)] = property;
            }
            return property;
        }
    }

    /// <summary>El renglón con todos sus campos conocidos (menos los Id locales) y los que no conoce, en forma canónica.</summary>
    public static JsonObject CanonicalRow(string column, IPatientSheetRow row)
    {
        var obj = new JsonObject();
        foreach (var name in Fields[column])
        {
            var value = Property(row.GetType(), name).GetValue(row);
            obj[name] = value is IEnumerable<PatientServiceSupply?> supplies
                ? new JsonArray(supplies.Select(s => (JsonNode?)(s == null ? null : CanonicalServiceSupply(s))).ToArray())
                : CanonicalJson.FromValue(value);
        }
        AddExtensionData(obj, row.ExtensionData);
        // Los nodos se reordenan al escribirlos: CanonicalJson.Write ordena las llaves
        return obj;
    }

    /// <summary>Un insumo de un servicio con todos sus campos conocidos (menos su Id local) y los que no conoce.</summary>
    public static JsonObject CanonicalServiceSupply(PatientServiceSupply supply)
    {
        var obj = new JsonObject();
        foreach (var name in ServiceSupplyFields)
        {
            obj[name] = CanonicalJson.FromValue(Property(typeof(PatientServiceSupply), name).GetValue(supply));
        }
        AddExtensionData(obj, supply.ExtensionData);
        return obj;
    }

    private static void AddExtensionData(JsonObject obj, Dictionary<string, System.Text.Json.JsonElement>? extra)
    {
        if (extra == null) return;
        foreach (var (key, element) in extra.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            obj[key] = CanonicalJson.FromElement(element);
        }
    }
}
