using System;

namespace TPVC.Tests;

/// <summary>
/// Reloj que solo avanza cuando la prueba lo pide (la hora local es UTC para que el día no dependa de la zona). Lleva la
/// hora de pared (<see cref="Now"/>) y un reloj monótono (<see cref="GetTimestamp"/>): avanzar mueve los dos; mover la hora
/// de Windows (<see cref="StepWallClock"/>, o poner <see cref="Now"/> hacia atrás) solo mueve la de pared.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    /// <summary>Tiempo monótono transcurrido desde que se creó.</summary>
    public TimeSpan Elapsed { get; private set; }

    /// <summary>La hora de pared. Ponerla adelante avanza también el reloj monótono; atrás, solo la de pared.</summary>
    public DateTimeOffset Now
    {
        get => _now;
        set
        {
            var delta = value - _now;
            if (delta > TimeSpan.Zero) Elapsed += delta;
            _now = value;
        }
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Elapsed.Ticks;

    public void Advance(TimeSpan by) => Now += by;

    /// <summary>Windows corrige la hora (hacia atrás o hacia adelante): el reloj monótono no se mueve.</summary>
    public void StepWallClock(TimeSpan by) => _now += by;
}
