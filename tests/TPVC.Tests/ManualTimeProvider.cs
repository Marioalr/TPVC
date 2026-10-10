using System;

namespace TPVC.Tests;

/// <summary>Reloj que solo avanza cuando la prueba lo pide (la hora local es UTC para que el día no dependa de la zona).</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => Now += by;
}
