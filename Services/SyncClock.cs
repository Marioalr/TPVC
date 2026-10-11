using System;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TPVC.Data;

namespace TPVC.Services;

/// <summary>
/// La hora con que se marca un cambio local (paso 8.7): la de este equipo más su desfase con la nube, que mide cada
/// sincronización. Así un equipo con el reloj adelantado o atrasado marca como si tuviera la hora del servidor, y una
/// marca adelantada deja de ganar todos los conflictos.
/// </summary>
public interface ISyncClock
{
    DateTime UtcNow { get; }

    /// <summary>Desfase de este equipo con la nube (hora del servidor − hora de aquí) para la base de <paramref name="db"/>.</summary>
    TimeSpan OffsetFor(AppDbContext db);
}

/// <summary>
/// El reloj del sistema con el desfase guardado en <see cref="DbMeta.ClockOffsetMs"/> (lo usan también los triggers de las
/// lápidas). Se lee una vez por base y se recuerda; la sincronización lo actualiza al medirlo.
/// </summary>
public sealed class SyncClock : ISyncClock
{
    public static SyncClock Default { get; } = new();

    private readonly ConcurrentDictionary<string, TimeSpan> _offsets = new(StringComparer.OrdinalIgnoreCase);

    public DateTime UtcNow => DateTime.UtcNow;

    public TimeSpan OffsetFor(AppDbContext db) => _offsets.GetOrAdd(Key(db), _ => Load(db));

    /// <summary>Recuerda el desfase recién medido para esta base (ya guardado en DbMeta por quien lo midió).</summary>
    public void Remember(AppDbContext db, TimeSpan offset) => _offsets[Key(db)] = offset;

    private static string Key(AppDbContext db) => db.Database.GetConnectionString() ?? string.Empty;

    private static TimeSpan Load(AppDbContext db)
    {
        try
        {
            return long.TryParse(DbMeta.Get(db, DbMeta.ClockOffsetMs), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms)
                ? TimeSpan.FromMilliseconds(ms)
                : TimeSpan.Zero;
        }
        catch (Exception)
        {
            // Una base sin DbMeta (todavía sin el registro de cambios): sin desfase
            return TimeSpan.Zero;
        }
    }
}
