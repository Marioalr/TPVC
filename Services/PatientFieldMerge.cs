using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using TPVC.Models;
using TPVC.Services.Rules;

namespace TPVC.Services;

/// <summary>
/// Paso 8.22: la combinación por campo de los expedientes (<see cref="PatientMerge"/>, pura) unida a EF: el sello de cada
/// guardado local (<see cref="StampLocalWrite"/>, lo llama <c>AppDbContext.StampSyncMetadata</c>), las dos versiones de una
/// fila al bajarla y cómo se aplica el resultado.
/// <list type="bullet">
/// <item><b>Las columnas que se combinan</b> son las de <c>Patients</c> que viajan, menos las derivadas (el resumen, que cada
/// equipo calcula) y <c>FieldStampsJson</c> misma.</item>
/// <item><b>El sello</b> marca solo lo que EF va a escribir y cambió por su significado: con el paso 8.21 un guardado escribe
/// solo los campos editados, así que lo que EF marca como modificado es exactamente lo que se editó. Cubre solo
/// <c>AddConsultationNoteAsync</c>, la regularización de folios, la importación y la baja del expediente, sin código aparte.</item>
/// </list>
/// </summary>
public static class PatientFieldMerge
{
    private static readonly ConditionalWeakTable<IModel, TableMap> Maps = new();

    /// <summary>La tabla <c>Patients</c> del contrato para este modelo de EF.</summary>
    internal static TableMap PatientsMap(IModel model) =>
        Maps.GetValue(model, m => SyncSchema.FromModel(m).Tables.Single(t => t.ClrType == typeof(PatientRecord)));

    /// <summary>Las columnas que se combinan por campo: las que viajan, menos las derivadas y <c>FieldStampsJson</c>.</summary>
    internal static IReadOnlyList<SyncColumn> Columns(TableMap map) =>
        map.Columns.Where(c => !map.IsDerived(c.Name) && c.Name != nameof(PatientRecord.FieldStampsJson)).ToList();

    /// <summary>Sus nombres (las columnas remotas), para la combinación pura.</summary>
    public static IReadOnlyList<string> ColumnNames(IModel model) => Columns(PatientsMap(model)).Select(c => c.Name).ToList();

    // ================================================================== el sello

    /// <summary>
    /// El sello de un guardado local de un expediente (alta o cambio), con la misma marca que su <c>UpdatedAtUtc</c>
    /// (<see cref="PatientFieldStamps.ApplyLocalWrite"/>): cada campo que EF va a escribir y cambió por su significado toma la
    /// marca; en una hoja, renglón por renglón. Un alta no marca campos: solo deja la marca de la fila (lo que no se ha editado
    /// desde el alta cuenta como la marca mínima, y nadie más lo tiene). Una fila que era una escritura sin marcas (de una PC
    /// anterior, o de antes de actualizar) conserva su UpdatedAtUtc anterior como piso. Una fila marcada completa como
    /// modificada sin cambiar nada (<c>EntityState.Modified</c>: «Volver a aplicar») es una escritura de la fila completa: el
    /// piso sube a la marca nueva.
    /// </summary>
    internal static void StampLocalWrite(EntityEntry entry, DateTime stamp)
    {
        var patient = (PatientRecord)entry.Entity;
        var columns = Columns(PatientsMap(entry.Context.Model));
        var fields = new List<string>();
        var sheets = new List<(string, string?, string?)>();
        DateTime? previous = null;
        bool everyColumnMarked = false;
        if (entry.State == EntityState.Modified)
        {
            previous = (DateTime?)entry.Property(nameof(PatientRecord.UpdatedAtUtc)).OriginalValue;
            everyColumnMarked = true;
            foreach (var column in columns)
            {
                var property = entry.Property(column.Property.Name);
                if (!property.IsModified)
                {
                    everyColumnMarked = false;
                    continue;
                }
                if (PatientSheetCanonical.IsSheet(column.Name))
                {
                    var before = property.OriginalValue as string;
                    var after = property.CurrentValue as string;
                    if (PatientSheetCanonical.CanonicalText(column.Name, before, patient.SyncId)
                        != PatientSheetCanonical.CanonicalText(column.Name, after, patient.SyncId))
                    {
                        sheets.Add((column.Name, before, after));
                    }
                }
                else if (CanonicalJson.Write(CanonicalJson.FromValue(property.OriginalValue))
                         != CanonicalJson.Write(CanonicalJson.FromValue(property.CurrentValue)))
                {
                    fields.Add(column.Name);
                }
            }
        }
        bool wholeRow = everyColumnMarked && fields.Count == 0 && sheets.Count == 0;
        var fieldColumns = PatientMerge.FieldColumns(columns.Select(c => c.Name));
        patient.FieldStampsJson = PatientFieldStamps.ApplyLocalWrite(patient.FieldStampsJson, previous, stamp, fields, sheets, patient.SyncId,
            fieldColumns, wholeRow).ToJson();
    }

    // ================================================================== al bajar

    /// <summary>La versión de aquí: los valores como los lee la PC (la edad, al día con la fecha de nacimiento).</summary>
    internal static PatientMerge.Version LocalVersion(PatientRecord patient, TableMap map) => new()
    {
        SyncId = patient.SyncId,
        UpdatedAtUtc = patient.UpdatedAtUtc,
        FieldStampsJson = patient.FieldStampsJson,
        Values = ValuesOf(patient, map)
    };

    /// <summary>
    /// La versión de la nube. Sus valores se leen igual que los de aquí (una instancia con los valores de la fila), así la
    /// edad con fecha de nacimiento se compara al día en los dos lados y no por el número que quedó guardado.
    /// </summary>
    internal static PatientMerge.Version RemoteVersion(IReadOnlyDictionary<string, object?> row, TableMap map, Guid syncId, PatientRecord local)
    {
        var remote = new PatientRecord { SyncId = syncId };
        foreach (var column in Columns(map))
        {
            if (!row.TryGetValue(column.Name, out var raw))
            {
                // La nube no tiene esa columna (no debería pasar: el ALTER la agrega): cuenta como la de aquí
                column.Property.SetValue(remote, column.Property.GetValue(local));
                continue;
            }
            column.Property.SetValue(remote, TursoSyncService.FromRemoteValue(raw, column.ClrType, column.IsNullable));
        }
        return new PatientMerge.Version
        {
            SyncId = syncId,
            UpdatedAtUtc = TursoSyncService.ParseDate(row.GetValueOrDefault(nameof(ISyncEntity.UpdatedAtUtc))),
            FieldStampsJson = row.GetValueOrDefault(nameof(PatientRecord.FieldStampsJson))?.ToString(),
            Values = ValuesOf(remote, map)
        };
    }

    private static Dictionary<string, JsonNode?> ValuesOf(PatientRecord patient, TableMap map)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var column in Columns(map))
        {
            var value = column.Property.GetValue(patient);
            values[column.Name] = PatientSheetCanonical.IsSheet(column.Name)
                ? (value is string text ? JsonValue.Create(text) : null)
                : CanonicalJson.FromValue(value);
        }
        return values;
    }

    /// <summary>Combina la fila local (de EF, con seguimiento) con la fila de la nube.</summary>
    internal static PatientMerge.Result Merge(PatientRecord local, IReadOnlyDictionary<string, object?> row, TableMap map) =>
        PatientMerge.Merge(LocalVersion(local, map), RemoteVersion(row, map, local.SyncId, local), Columns(map).Select(c => c.Name).ToList());

    /// <summary>
    /// Pone el resultado en la fila local: lo que ganó la nube con su valor tal como llegó, una hoja combinada con su texto
    /// nuevo, y la marca y las marcas por campo del resultado. Las columnas derivadas no se tocan.
    /// </summary>
    internal static void Apply(EntityEntry entry, PatientMerge.Result result, IReadOnlyDictionary<string, object?> row, TableMap map)
    {
        var patient = (PatientRecord)entry.Entity;
        foreach (var column in Columns(map))
        {
            switch (result.Sources.GetValueOrDefault(column.Name))
            {
                case PatientMerge.Source.Remote when row.TryGetValue(column.Name, out var raw):
                    entry.Property(column.Property.Name).CurrentValue = TursoSyncService.FromRemoteValue(raw, column.ClrType, column.IsNullable);
                    break;
                case PatientMerge.Source.Merged:
                    entry.Property(column.Property.Name).CurrentValue = CanonicalJson.AsString(result.Values[column.Name]) ?? string.Empty;
                    break;
            }
        }
        patient.FieldStampsJson = result.FieldStampsJson;
        patient.UpdatedAtUtc = result.UpdatedAtUtc;
    }
}
