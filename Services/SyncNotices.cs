using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;
using TPVC.Models;
using TPVC.Services.Rules;

namespace TPVC.Services;

/// <summary>Cuál de los dos avisos de lo que llegó de otro equipo.</summary>
public enum SyncNoticeKind
{
    /// <summary>Citas empalmadas.</summary>
    Overlaps,

    /// <summary>Posibles expedientes duplicados.</summary>
    DuplicatePatients,

    /// <summary>Productos, servicios o categorías que pueden estar dados de alta dos veces (2026-10-22).</summary>
    DuplicateCatalog
}

/// <summary>
/// Paso 8.23 (decisión del médico, 2026-10-14): los avisos de lo que llegó de otro equipo (citas empalmadas y posibles
/// expedientes duplicados) se quedan hasta que el médico los descarta, también al cerrar y abrir TPVC. Son de este equipo y
/// no viajan: viven en la base local, en <c>DbMeta</c> "SyncNotices" (JSON con los pares por SyncId y lo descartado).
/// <list type="bullet">
/// <item><b>Qué se guarda:</b> cada par (los dos SyncId) con su llave de conflicto: en un empalme, el par, la fecha y la
/// hora de cada cita (si una se cambia a otra hora y sigue empalmada, es un conflicto nuevo); en un duplicado, el par.
/// Los textos (nombres, folios, horas) se arman al mostrar, con los datos de ahora.</item>
/// <item><b>Descartar</b> («Entendido») guarda las llaves de lo que se veía y lo quita para siempre en este equipo: el mismo
/// conflicto no vuelve a avisar aunque vuelva a llegar (otro equipo confirma la cita o edita al paciente). Uno nuevo sí: otra
/// pareja, o el mismo par de citas en otro horario.</item>
/// <item><b>Se depura al mostrar:</b> un empalme cuyas citas ya no existen, se cancelaron, se movieron y ya no se cruzan, o
/// son de un día que ya pasó; un duplicado cuyos expedientes ya no existen, se dieron de baja, se unieron a otro
/// (<c>MergedIntoSyncId</c>) o ya no se parecen. Lo descartado de días pasados también se olvida.</item>
/// <item><b>El catálogo</b> (desde el 2026-10-22, la tablet también lo edita): dos productos, servicios o categorías que
/// pueden ser el mismo (<see cref="CatalogDuplicateRules"/>), con la llave "catalogo:&lt;tipo&gt;:&lt;par&gt;". Se depura
/// igual: uno que ya no existe, se desactivó o ya no se parece (se le cambió el código o el nombre).</item>
/// <item><b>Restaurar un respaldo</b> regresa la base de ese momento con sus avisos y lo que se había descartado; se depuran
/// contra los datos restaurados al mostrarse. Lo que baje después vuelve a revisarse (la primera descarga no avisa).</item>
/// </list>
/// </summary>
public static class SyncNotices
{
    /// <summary>La clave de <c>DbMeta</c>.</summary>
    public const string MetaKey = "SyncNotices";

    /// <summary>Cuántas llaves descartadas se recuerdan como máximo (las más recientes).</summary>
    private const int MaxDismissed = 1000;

    // Lectura, cambio y escritura de la misma clave desde la sincronización y desde la pantalla: una a la vez
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    internal sealed class State
    {
        public List<Entry> Overlaps { get; set; } = new();
        public List<Entry> Duplicates { get; set; } = new();
        public List<Entry> Catalog { get; set; } = new();
        public List<string> Dismissed { get; set; } = new();

        public bool IsEmpty => Overlaps.Count == 0 && Duplicates.Count == 0 && Catalog.Count == 0 && Dismissed.Count == 0;
    }

    internal sealed class Entry
    {
        public Guid First { get; set; }
        public Guid Second { get; set; }
        public string Key { get; set; } = string.Empty;
        public DateTime NotedAtUtc { get; set; }

        public bool IsPair(Guid a, Guid b) => (First == a && Second == b) || (First == b && Second == a);
    }

    private static (Guid Low, Guid High) Ordered(Guid a, Guid b) =>
        string.CompareOrdinal(SyncIds.Format(a), SyncIds.Format(b)) <= 0 ? (a, b) : (b, a);

    /// <summary>"empalme:&lt;SyncId menor&gt;:&lt;SyncId mayor&gt;:&lt;fecha&gt;:&lt;hora de la primera&gt;:&lt;hora de la segunda&gt;".</summary>
    public static string OverlapKey(PatientAppointment a, PatientAppointment b)
    {
        var (low, high) = string.CompareOrdinal(SyncIds.Format(a.SyncId), SyncIds.Format(b.SyncId)) <= 0 ? (a, b) : (b, a);
        return $"empalme:{SyncIds.Format(low.SyncId)}:{SyncIds.Format(high.SyncId)}:{low.Date:yyyy-MM-dd}:" +
               $"{AppointmentRules.TimeText(low.Time)}:{AppointmentRules.TimeText(high.Time)}";
    }

    /// <summary>"duplicado:&lt;SyncId menor&gt;:&lt;SyncId mayor&gt;".</summary>
    public static string DuplicateKey(Guid a, Guid b)
    {
        var (low, high) = Ordered(a, b);
        return $"duplicado:{SyncIds.Format(low)}:{SyncIds.Format(high)}";
    }

    /// <summary>"catalogo:&lt;producto|servicio|categoria&gt;:&lt;SyncId menor&gt;:&lt;SyncId mayor&gt;".</summary>
    public static string CatalogKey(CatalogDuplicateRules.Kind kind, Guid a, Guid b)
    {
        var (low, high) = Ordered(a, b);
        return $"catalogo:{CatalogDuplicateRules.KindKey(kind)}:{SyncIds.Format(low)}:{SyncIds.Format(high)}";
    }

    private static CatalogDuplicateRules.Kind? CatalogKind(string key) => key.Split(':') is { Length: 4 } parts && parts[0] == "catalogo"
        ? parts[1] switch
        {
            "producto" => CatalogDuplicateRules.Kind.Product,
            "servicio" => CatalogDuplicateRules.Kind.Service,
            "categoria" => CatalogDuplicateRules.Kind.Category,
            _ => null
        }
        : null;

    private static State Read(AppDbContext db)
    {
        var text = DbMeta.Get(db, MetaKey);
        if (string.IsNullOrWhiteSpace(text)) return new State();
        try
        {
            return JsonSerializer.Deserialize<State>(text, Json) ?? new State();
        }
        catch (JsonException ex)
        {
            AppLogger.Warn($"Los avisos de lo que llegó de otro equipo no se pudieron leer y se empiezan de nuevo: {ex.Message}");
            return new State();
        }
    }

    private static void Write(AppDbContext db, State state)
    {
        if (state.Dismissed.Count > MaxDismissed) state.Dismissed.RemoveRange(0, state.Dismissed.Count - MaxDismissed);
        if (state.IsEmpty) DbMeta.Remove(db, MetaKey);
        else DbMeta.Set(db, MetaKey, JsonSerializer.Serialize(state, Json));
    }

    /// <summary>
    /// Agrega lo que llegó: un conflicto ya descartado no se agrega; un par que ya está no se repite (si su llave cambió, se
    /// pone al día). Lo nuevo va primero. true si cambió algo.
    /// </summary>
    public static async Task<bool> AddAsync(IDbContextFactory<AppDbContext> factory, SyncArrivals arrivals, CancellationToken ct = default)
    {
        if (arrivals.IsEmpty) return false;
        await Gate.WaitAsync(ct);
        try
        {
            using var db = factory.CreateDbContext();
            var state = Read(db);
            var dismissed = state.Dismissed.ToHashSet(StringComparer.Ordinal);
            bool changed = false;
            var now = DateTime.UtcNow;

            foreach (var overlap in arrivals.Overlaps.Reverse())
            {
                var key = OverlapKey(overlap.First, overlap.Second);
                if (dismissed.Contains(key)) continue;
                var existing = state.Overlaps.FirstOrDefault(e => e.IsPair(overlap.First.SyncId, overlap.Second.SyncId));
                if (existing != null)
                {
                    if (existing.Key == key) continue;
                    state.Overlaps.Remove(existing);
                }
                var (low, high) = Ordered(overlap.First.SyncId, overlap.Second.SyncId);
                state.Overlaps.Insert(0, new Entry { First = low, Second = high, Key = key, NotedAtUtc = now });
                changed = true;
            }

            foreach (var pair in arrivals.DuplicatePatients.Reverse())
            {
                var key = DuplicateKey(pair.First.SyncId, pair.Second.SyncId);
                if (dismissed.Contains(key) || state.Duplicates.Any(e => e.Key == key)) continue;
                var (low, high) = Ordered(pair.First.SyncId, pair.Second.SyncId);
                state.Duplicates.Insert(0, new Entry { First = low, Second = high, Key = key, NotedAtUtc = now });
                changed = true;
            }

            foreach (var pair in arrivals.DuplicateCatalog.Reverse())
            {
                var key = pair.Key;
                if (dismissed.Contains(key) || state.Catalog.Any(e => e.Key == key)) continue;
                var (low, high) = Ordered(pair.First.SyncId, pair.Second.SyncId);
                state.Catalog.Insert(0, new Entry { First = low, Second = high, Key = key, NotedAtUtc = now });
                changed = true;
            }

            if (changed) Write(db, state);
            return changed;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Los avisos de ahora, con los datos de ahora y depurados (ver la clase): lo que ya no aplica se quita de la base.
    /// <paramref name="today"/> es el día del consultorio.
    /// </summary>
    public static async Task<SyncArrivals> LoadAsync(IDbContextFactory<AppDbContext> factory, DateTime today, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var db = factory.CreateDbContext();
            var state = Read(db);
            if (state.IsEmpty) return SyncArrivals.None;
            bool changed = false;
            var dismissed = state.Dismissed.ToHashSet(StringComparer.Ordinal);

            // Empalmes: las dos citas existen, siguen empalmadas y son de hoy en adelante
            var overlaps = new List<AppointmentOverlap>();
            var appointmentIds = state.Overlaps.SelectMany(e => new[] { e.First, e.Second }).Distinct().ToList();
            var appointments = appointmentIds.Count == 0
                ? new Dictionary<Guid, PatientAppointment>()
                : await db.Appointments.AsNoTracking().Where(a => appointmentIds.Contains(a.SyncId)).ToDictionaryAsync(a => a.SyncId, ct);
            foreach (var entry in state.Overlaps.ToList())
            {
                if (!appointments.TryGetValue(entry.First, out var a) || !appointments.TryGetValue(entry.Second, out var b)
                    || a.Date.Date < today.Date || !AppointmentRules.Overlaps(AppointmentRules.Of(a), AppointmentRules.Of(b)))
                {
                    state.Overlaps.Remove(entry);
                    changed = true;
                    continue;
                }
                var key = OverlapKey(a, b);
                if (key != entry.Key)
                {
                    // Una se movió y sigue empalmada: es otro conflicto, salvo que ese ya se haya descartado
                    if (dismissed.Contains(key))
                    {
                        state.Overlaps.Remove(entry);
                        changed = true;
                        continue;
                    }
                    entry.Key = key;
                    changed = true;
                }
                var first = AppointmentRules.Start(a.Time) <= AppointmentRules.Start(b.Time) ? a : b;
                overlaps.Add(new AppointmentOverlap(first, ReferenceEquals(first, a) ? b : a));
            }

            // Duplicados: los dos existen, activos, sin unir a otro, y se siguen pareciendo
            var duplicates = new List<DuplicatePatientPair>();
            var patientIds = state.Duplicates.SelectMany(e => new[] { e.First, e.Second }).Distinct().ToList();
            var patients = patientIds.Count == 0
                ? new Dictionary<Guid, PatientRecord>()
                : (await db.Patients.AsNoTracking()
                    .Where(p => patientIds.Contains(p.SyncId))
                    .Select(p => new { p.Id, p.SyncId, p.FullName, p.RecordNumber, p.BirthDate, p.Phone, p.IsActive, p.MergedIntoSyncId })
                    .ToListAsync(ct))
                .ToDictionary(p => p.SyncId, p => new PatientRecord
                {
                    Id = p.Id, SyncId = p.SyncId, FullName = p.FullName, RecordNumber = p.RecordNumber, BirthDate = p.BirthDate,
                    Phone = p.Phone, IsActive = p.IsActive, MergedIntoSyncId = p.MergedIntoSyncId
                });
            foreach (var entry in state.Duplicates.ToList())
            {
                var match = patients.TryGetValue(entry.First, out var a) && patients.TryGetValue(entry.Second, out var b)
                    ? DuplicatePatientRules.Compare(DuplicatePatientRules.Of(a), DuplicatePatientRules.Of(b))
                    : DuplicatePatientRules.Match.None;
                if (match == DuplicatePatientRules.Match.None)
                {
                    state.Duplicates.Remove(entry);
                    changed = true;
                    continue;
                }
                duplicates.Add(new DuplicatePatientPair(patients[entry.First], patients[entry.Second], match));
            }

            // Catálogo: los dos existen, activos, y se siguen pareciendo
            var catalog = new List<DuplicateCatalogPair>();
            if (state.Catalog.Count > 0)
            {
                var ids = state.Catalog.SelectMany(e => new[] { e.First, e.Second }).Distinct().ToList();
                var items = await LoadCatalogItemsAsync(db, ids, ct);
                foreach (var entry in state.Catalog.ToList())
                {
                    var kind = CatalogKind(entry.Key);
                    var match = kind != null && items.TryGetValue((kind.Value, entry.First), out var a) && items.TryGetValue((kind.Value, entry.Second), out var b)
                        ? CatalogDuplicateRules.Compare(a, b)
                        : CatalogDuplicateRules.Match.None;
                    if (match == CatalogDuplicateRules.Match.None)
                    {
                        state.Catalog.Remove(entry);
                        changed = true;
                        continue;
                    }
                    catalog.Add(new DuplicateCatalogPair(items[(kind!.Value, entry.First)], items[(kind.Value, entry.Second)], match));
                }
            }

            // Lo descartado de días que ya pasaron ya no puede volver a llegar como aviso
            var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            int before = state.Dismissed.Count;
            // "empalme:<SyncId>:<SyncId>:<fecha>:<HH:mm>:<HH:mm>": la fecha es la cuarta parte (las horas también llevan ':')
            state.Dismissed.RemoveAll(k => k.StartsWith("empalme:", StringComparison.Ordinal) && k.Split(':', 5) is { Length: 5 } parts
                                           && string.CompareOrdinal(parts[3], todayText) < 0);
            changed |= state.Dismissed.Count != before;

            if (changed) Write(db, state);
            return overlaps.Count == 0 && duplicates.Count == 0 && catalog.Count == 0
                ? SyncArrivals.None
                : new SyncArrivals(overlaps, duplicates, catalog);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>«Entendido»: lo que se ve de ese aviso se descarta para siempre en este equipo.</summary>
    public static async Task DismissAsync(IDbContextFactory<AppDbContext> factory, SyncNoticeKind kind, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var db = factory.CreateDbContext();
            var state = Read(db);
            var entries = kind switch
            {
                SyncNoticeKind.Overlaps => state.Overlaps,
                SyncNoticeKind.DuplicatePatients => state.Duplicates,
                _ => state.Catalog
            };
            if (entries.Count == 0) return;
            foreach (var entry in entries.Where(e => !state.Dismissed.Contains(e.Key))) state.Dismissed.Add(entry.Key);
            entries.Clear();
            Write(db, state);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Lo que la regla del catálogo necesita de cada producto, servicio y categoría con esos SyncId.</summary>
    internal static async Task<Dictionary<(CatalogDuplicateRules.Kind, Guid), CatalogDuplicateRules.Item>> LoadCatalogItemsAsync(
        AppDbContext db, IReadOnlyCollection<Guid>? syncIds, CancellationToken ct)
    {
        var products = db.Products.AsNoTracking();
        var services = db.ClinicalServices.AsNoTracking();
        var categories = db.Categories.AsNoTracking();
        if (syncIds != null)
        {
            products = products.Where(p => syncIds.Contains(p.SyncId));
            services = services.Where(s => syncIds.Contains(s.SyncId));
            categories = categories.Where(c => syncIds.Contains(c.SyncId));
        }
        var items = new List<CatalogDuplicateRules.Item>();
        items.AddRange((await products.Select(p => new { p.SyncId, p.Code, p.Name, p.Presentation, p.IsActive }).ToListAsync(ct))
            .Select(p => new CatalogDuplicateRules.Item(CatalogDuplicateRules.Kind.Product, p.SyncId, p.Code, p.Name, p.Presentation, p.IsActive)));
        items.AddRange((await services.Select(s => new { s.SyncId, s.Code, s.Name, s.IsActive }).ToListAsync(ct))
            .Select(s => new CatalogDuplicateRules.Item(CatalogDuplicateRules.Kind.Service, s.SyncId, s.Code, s.Name, null, s.IsActive)));
        items.AddRange((await categories.Select(c => new { c.SyncId, c.Name }).ToListAsync(ct))
            .Select(c => new CatalogDuplicateRules.Item(CatalogDuplicateRules.Kind.Category, c.SyncId, null, c.Name)));
        return items.GroupBy(i => (i.Kind, i.SyncId)).ToDictionary(g => g.Key, g => g.First());
    }
}
