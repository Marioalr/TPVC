using System;
using System.Threading;

namespace TPVC.Models;

/// <summary>
/// La fecha y hora del consultorio para lo que depende de "hoy": la edad, las citas próximas, los días que le quedan a
/// un lote y el resumen (paso 8.11). Antes cada uno leía DateTime.Today. Las pruebas fijan otro reloj con
/// <see cref="Use"/>, solo en su flujo (las pruebas corren en paralelo).
/// </summary>
public static class ClinicClock
{
    private static readonly AsyncLocal<TimeProvider?> Override = new();

    /// <summary>El reloj de este flujo: el de las pruebas o el del sistema.</summary>
    public static TimeProvider Provider => Override.Value ?? TimeProvider.System;

    /// <summary>Hora local del consultorio (la zona del reloj; en el programa, la de Windows).</summary>
    public static DateTime Now => Provider.GetLocalNow().DateTime;

    public static DateTime Today => Now.Date;

    /// <summary>Usa <paramref name="clock"/> en este flujo (y en las tareas que salgan de él) hasta desecharlo.</summary>
    public static IDisposable Use(TimeProvider clock)
    {
        var previous = Override.Value;
        Override.Value = clock;
        return new Restore(previous);
    }

    private sealed class Restore(TimeProvider? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}
