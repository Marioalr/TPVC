using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services.Rules;

namespace TPVC.Services;

/// <summary>
/// Paso 8.21 (H1a), guardar solo lo que cambió. Antes <c>SavePatientAsync</c> copiaba la fila completa de la instancia en
/// memoria, y una copia vieja (la ficha o la consulta abiertas, que <c>CanReplacePatientInstance</c> no deja cambiar)
/// pisaba lo que otro camino había escrito mientras tanto: la próxima cita que pone <c>SaveAppointmentAsync</c>, el folio
/// regularizado o lo que bajó de otro equipo.
/// <list type="bullet">
/// <item><b>Los campos</b> son todas las columnas de <see cref="PatientRecord"/> en el modelo de EF menos la identidad y lo
/// que pone la sincronización (<see cref="Metadata"/>): una columna nueva entra sola (lo vigila
/// <c>PatientFieldsTests</c>). <c>IsActive</c> es la baja lógica del expediente, no una lápida: es un campo más.</item>
/// <item><b>La foto</b> (<see cref="PatientFieldSnapshot"/>) va con la instancia (<c>PatientRecord.StoredSnapshot</c>): se
/// toma al leerla de la base (<c>GetPatientsAsync</c>, <c>GetPatientSummariesAsync</c>, <c>GetPatientByIdAsync</c>) y se
/// renueva después de cada guardado. Regla: la foto es lo que la instancia tenía la última vez que estuvo al día con la
/// base, campo por campo; lo editado es lo que difiere de ella. La foto nunca se mueve sin la instancia.</item>
/// <item><b>Las hojas</b> (<see cref="Sheets"/>) se comparan por su texto canónico
/// (<c>PatientRecord.CanonicalSheetJson</c>), después de <c>Sync*Json</c>. Una hoja que cambió se escribe completa: la
/// combinación por renglón es del paso 8.22.</item>
/// <item><b>Después de guardar</b> (<see cref="Refresh"/>) la instancia toma de la fila fresca los campos que no editó (la
/// próxima cita, la última visita, el folio…) y la foto queda con la fila fresca. Una hoja que no editó y que en la base
/// es otra no se cambia en memoria: sus renglones son los objetos que ven las hojas, la cuenta de la consulta y "Se surte
/// aquí", y cambiarlos a media consulta los soltaría. Su foto se queda como estaba (la instancia sigue sin editarla, así
/// que no se escribe) y la instancia no toma la marca de la fila: al soltarla (se cierra la consulta o el formulario)
/// <c>PatientsWorkspace.ReloadIfReleasedAsync</c> recarga y la cambia por la de la base.</item>
/// </list>
/// </summary>
public static class PatientFields
{
    /// <summary>Lo que no es un campo: la identidad del registro y lo que pone <c>StampSyncMetadata</c> o el alta.</summary>
    public static readonly IReadOnlySet<string> Metadata = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(PatientRecord.Id),
        nameof(PatientRecord.SyncId),
        nameof(PatientRecord.UpdatedAtUtc),
        nameof(PatientRecord.CreatedAt)
    };

    /// <summary>Las hojas del paciente: se comparan por su texto canónico y se escriben completas.</summary>
    public static readonly IReadOnlySet<string> Sheets = new HashSet<string>(StringComparer.Ordinal)
    {
        PatientSheetRows.PrescriptionColumn,
        PatientSheetRows.SuppliesColumn,
        PatientSheetRows.ServicesColumn
    };

    /// <summary>Un campo: su nombre (el de la propiedad y la columna), su lugar en la foto y cómo leerlo y escribirlo sin reflexión en cada uso.</summary>
    public sealed class Field
    {
        internal Field(PropertyInfo property, int index)
        {
            Property = property;
            Index = index;
            var instance = Expression.Parameter(typeof(PatientRecord), "patient");
            var value = Expression.Parameter(typeof(object), "value");
            var member = Expression.Property(instance, property);
            Get = Expression.Lambda<Func<PatientRecord, object?>>(Expression.Convert(member, typeof(object)), instance).Compile();
            Set = Expression.Lambda<Action<PatientRecord, object?>>(
                Expression.Assign(member, Expression.Convert(value, property.PropertyType)), instance, value).Compile();
        }

        public string Name => Property.Name;
        public PropertyInfo Property { get; }
        /// <summary>Su lugar en <see cref="All"/> y en los valores de <see cref="PatientFieldSnapshot"/>.</summary>
        internal int Index { get; }
        public bool IsSheet => Sheets.Contains(Name);
        internal Func<PatientRecord, object?> Get { get; }
        internal Action<PatientRecord, object?> Set { get; }
    }

    private static readonly Lazy<IReadOnlyList<Field>> _all = new(() => FromModel(DesignModel()));
    private static readonly Lazy<IReadOnlyDictionary<string, Field>> _byName =
        new(() => All.ToDictionary(f => f.Name, StringComparer.Ordinal));

    /// <summary>Los campos del expediente, en orden por nombre.</summary>
    public static IReadOnlyList<Field> All => _all.Value;

    /// <summary>El campo con ese nombre, o null si no es un campo del expediente.</summary>
    internal static Field? Find(string name) => _byName.Value.TryGetValue(name, out var field) ? field : null;

    /// <summary>
    /// Arma la lista fuera del hilo de la interfaz (al arrancar): la primera vez lee el modelo y compila los accesos, y si
    /// no, lo pagaría la primera carga de pacientes.
    /// </summary>
    public static void Warm() => _ = _byName.Value;

    /// <summary>Los campos de un modelo de EF: sus propiedades de <see cref="PatientRecord"/> menos <see cref="Metadata"/>.</summary>
    internal static IReadOnlyList<Field> FromModel(IModel model)
    {
        var entity = model.FindEntityType(typeof(PatientRecord))
                     ?? throw new InvalidOperationException("El modelo no tiene la tabla de expedientes.");
        return entity.GetProperties()
            .Where(p => !Metadata.Contains(p.Name))
            .Select(p => p.PropertyInfo
                         ?? throw new InvalidOperationException($"La columna {p.Name} del expediente no es una propiedad de PatientRecord."))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select((p, index) => new Field(p, index))
            .ToList();
    }

    /// <summary>
    /// El modelo de <see cref="AppDbContext"/> sin abrir ninguna base. Con las mismas opciones que la app
    /// (<see cref="AppDbContext.ConfigureSqlite"/>), EF lo toma del que ya armó en lugar de armarlo otra vez.
    /// </summary>
    private static IModel DesignModel()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.ConfigureSqlite(builder, "Data Source=:memory:");
        using var db = new AppDbContext(builder.Options);
        return db.Model;
    }

    // ================================================================== la foto

    /// <summary>La foto de los campos de una instancia tal como están ahora.</summary>
    public static PatientFieldSnapshot Capture(PatientRecord patient)
    {
        var fields = All;
        var values = new object?[fields.Count];
        for (int i = 0; i < values.Length; i++) values[i] = fields[i].Get(patient);
        return new PatientFieldSnapshot(values);
    }

    /// <summary>Recién leída de la base: la instancia guarda su foto.</summary>
    public static PatientRecord Track(PatientRecord patient)
    {
        patient.StoredSnapshot = Capture(patient);
        return patient;
    }

    public static void Track(IEnumerable<PatientRecord> patients)
    {
        foreach (var patient in patients) Track(patient);
    }

    /// <summary>
    /// Los campos donde <paramref name="edited"/> difiere de <paramref name="baseline"/>; sin foto, todos (la fila completa,
    /// como antes del paso 8.21). Un campo que falta en la foto cuenta como cambiado
    /// (<see cref="PatientFieldSnapshot.Without"/>).
    /// </summary>
    public static IReadOnlyList<Field> Changed(PatientRecord edited, PatientFieldSnapshot? baseline) =>
        baseline == null
            ? All
            : All.Where(f => !baseline.Has(f) || !SameValue(edited, f, f.Get(edited), baseline[f])).ToList();

    /// <summary>
    /// Lo que un guardado escribe sobre la fila recién leída (<paramref name="stored"/>):
    /// <list type="bullet">
    /// <item>lo editado (<see cref="Changed"/>), que gana siempre;</item>
    /// <item>lo que la instancia trae distinto de su propia foto sin que cuente como edición, cuando se guarda con otra foto
    /// (el formulario: el teléfono solo con dígitos, "No determinado", "Normal"…), solo si en la base sigue como estaba en
    /// la foto; si otro equipo lo cambió, se queda el de la base;</item>
    /// <item>si la fila se escribe de todos modos, una hoja que dice lo mismo que la de la base pero con otro texto (sin los
    /// <c>RowId</c> que se derivan, o con el formato de otro equipo): se escribe con el de la PC, como desde el paso 8.18
    /// ("se guarda con cualquier guardado del expediente"). Si dice otra cosa, se queda la de la base.</item>
    /// </list>
    /// Sin foto, todos los campos (la fila completa).
    /// </summary>
    public static List<Field> ToWrite(PatientRecord edited, PatientFieldSnapshot? baseline, PatientRecord stored)
    {
        var write = Changed(edited, baseline).ToList();
        if (baseline == null) return write;

        if (edited.StoredSnapshot is { } own && !ReferenceEquals(own, baseline))
        {
            foreach (var field in All.Except(write).ToList())
            {
                if (own.Has(field)
                    && !SameValue(edited, field, field.Get(edited), own[field])
                    && SameValue(edited, field, field.Get(stored), own[field]))
                {
                    write.Add(field);
                }
            }
        }

        if (write.Count > 0)
        {
            foreach (var field in All.Where(f => f.IsSheet).Except(write).ToList())
            {
                var mine = field.Get(edited);
                var theirs = field.Get(stored);
                if (!Equals(mine, theirs) && SameValue(edited, field, mine, theirs)) write.Add(field);
            }
        }
        return write;
    }

    /// <summary>Igual en la base: los valores de siempre con <c>Equals</c>; las hojas, por su texto canónico.</summary>
    internal static bool SameValue(PatientRecord patient, Field field, object? a, object? b)
    {
        if (Equals(a, b)) return true;
        if (!field.IsSheet) return false;
        return string.Equals(patient.CanonicalSheetJson(field.Name, a as string), patient.CanonicalSheetJson(field.Name, b as string), StringComparison.Ordinal);
    }

    /// <summary>
    /// Después de guardar <paramref name="instance"/> con <paramref name="baseline"/> (la foto con que se comparó), con la
    /// fila que quedó en la base (<paramref name="fresh"/>):
    /// <list type="bullet">
    /// <item>un campo que la instancia no editó (igual a la foto) toma el valor de la fila;</item>
    /// <item>uno que editó se queda (ya es el de la fila, o lo volvieron a cambiar mientras se guardaba: sigue siendo una
    /// edición para el siguiente guardado);</item>
    /// <item>una hoja que no editó y que en la base es otra se queda como estaba, con su foto (ver la clase);</item>
    /// <item>la foto nueva es la fila, y la marca de la fila pasa a la instancia salvo que le quede una hoja vieja.</item>
    /// </list>
    /// </summary>
    public static void Refresh(PatientRecord instance, PatientRecord fresh, PatientFieldSnapshot? baseline)
    {
        if (ReferenceEquals(instance, fresh)) return;
        var values = new object?[All.Count];
        bool staleSheet = false;
        foreach (var field in All)
        {
            var current = field.Get(instance);
            var stored = field.Get(fresh);
            bool untouched = baseline != null && baseline.Has(field) && SameValue(instance, field, current, baseline[field]);
            if (field.IsSheet)
            {
                if (SameValue(instance, field, current, stored))
                {
                    values[field.Index] = stored;
                }
                else if (untouched)
                {
                    values[field.Index] = baseline![field];
                    staleSheet = true;
                }
                else
                {
                    values[field.Index] = stored;
                }
            }
            else
            {
                if (untouched) field.Set(instance, stored);
                values[field.Index] = stored;
            }
        }

        instance.StoredSnapshot = new PatientFieldSnapshot(values);
        if (!staleSheet) instance.UpdatedAtUtc = fresh.UpdatedAtUtc;
    }

    /// <summary>
    /// Otro camino ya escribió esos campos en la base y la instancia los acaba de reflejar con el mismo valor (el resumen
    /// de la nota de la consulta, la receta corregida): la foto los toma de la instancia, así la regla se mantiene y un
    /// cambio posterior (descartar la consulta) sí se ve como cambio. Sin foto no hace nada.
    /// </summary>
    public static void MarkStored(PatientRecord patient, IEnumerable<string> fields)
    {
        if (patient.StoredSnapshot is not { } snapshot) return;
        var updates = new List<(PatientFields.Field, object?)>();
        foreach (var name in fields)
        {
            var field = Find(name) ?? throw new ArgumentException($"{name} no es un campo del expediente.", nameof(fields));
            if (field.IsSheet) patient.SyncSheetJson(name);
            updates.Add((field, field.Get(patient)));
        }
        patient.StoredSnapshot = snapshot.With(updates);
    }
}

/// <summary>
/// Paso 8.21: los valores de los campos de un expediente (<see cref="PatientFields"/>) en un momento dado; las hojas, con
/// el texto guardado. No cambia: una versión nueva es otra foto. Los valores van en un arreglo por el lugar de cada campo
/// en <see cref="PatientFields.All"/> (cada carga de la lista toma la foto de todos los pacientes).
/// </summary>
public sealed class PatientFieldSnapshot
{
    /// <summary>Un campo que no está en la foto (<see cref="Without"/>).</summary>
    private static readonly object Missing = new();

    private readonly object?[] _values;

    internal PatientFieldSnapshot(object?[] values)
    {
        _values = values;
    }

    public bool Has(string field) => PatientFields.Find(field) is { } f && Has(f);

    public object? this[string field] =>
        this[PatientFields.Find(field) ?? throw new KeyNotFoundException($"{field} no es un campo del expediente.")];

    internal bool Has(PatientFields.Field field) => !ReferenceEquals(_values[field.Index], Missing);

    internal object? this[PatientFields.Field field] =>
        Has(field) ? _values[field.Index] : throw new KeyNotFoundException($"{field.Name} no está en la foto.");

    /// <summary>La misma foto sin esos campos: al guardar con ella se escriben aunque no hayan cambiado.</summary>
    public PatientFieldSnapshot Without(IEnumerable<string> fields)
    {
        var values = (object?[])_values.Clone();
        foreach (var name in fields)
        {
            if (PatientFields.Find(name) is { } field) values[field.Index] = Missing;
        }
        return new PatientFieldSnapshot(values);
    }

    /// <summary>La misma foto con esos valores.</summary>
    internal PatientFieldSnapshot With(IEnumerable<(PatientFields.Field Field, object? Value)> updates)
    {
        var values = (object?[])_values.Clone();
        foreach (var (field, value) in updates) values[field.Index] = value;
        return new PatientFieldSnapshot(values);
    }
}
