using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TPVC.Tests.Fase6;

/// <summary>
/// Graba los avisos PropertyChanged de Pacientes sin importar qué objeto los levanta: el coordinador y
/// cada propiedad de solo lectura cuyo tipo es un ObservableObject de TPVC.ViewModels* (hoy solo
/// Checkout; después de la fase 6, también Session, Agenda, Consultation...). Así un miembro que se mueve
/// a otro ViewModel sigue apareciendo con el mismo nombre y en el mismo orden.
/// </summary>
internal sealed class PatientsNotificationRecorder : IDisposable
{
    private readonly object _gate = new();
    private readonly List<string> _names = new();
    private readonly List<(INotifyPropertyChanged Source, PropertyChangedEventHandler Handler)> _subscriptions = new();
    private readonly int? _onlyThread;
    private long _lastEventTicks = DateTime.UtcNow.Ticks;

    /// <summary>Objetos observados (el coordinador primero).</summary>
    public IReadOnlyList<object> Sources { get; }

    /// <param name="root">El coordinador (MedicalRecordsViewModel).</param>
    /// <param name="sameThreadOnly">
    /// Solo los avisos levantados en el hilo que crea el grabador: para los escenarios síncronos, donde
    /// los temporizadores (reloj de la consulta, cierre del aviso a los 4 s) corren en otros hilos.
    /// </param>
    public PatientsNotificationRecorder(object root, bool sameThreadOnly)
    {
        _onlyThread = sameThreadOnly ? Environment.CurrentManagedThreadId : null;
        var sources = new List<object>();
        Walk(root, sources);
        Sources = sources;
        foreach (var source in sources.OfType<INotifyPropertyChanged>())
        {
            PropertyChangedEventHandler handler = (_, e) => Record(e.PropertyName);
            source.PropertyChanged += handler;
            _subscriptions.Add((source, handler));
        }
    }

    private static void Walk(object node, List<object> visited)
    {
        if (visited.Any(v => ReferenceEquals(v, node))) return;
        visited.Add(node);
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (property.GetIndexParameters().Length > 0 || property.SetMethod?.IsPublic == true) continue;
            var type = property.PropertyType;
            if (!typeof(ObservableObject).IsAssignableFrom(type)) continue;
            if (type.Namespace?.StartsWith("TPVC.ViewModels", StringComparison.Ordinal) != true) continue;
            if (property.GetValue(node) is { } child) Walk(child, visited);
        }
    }

    private void Record(string? name)
    {
        if (name == null) return;
        if (_onlyThread != null && Environment.CurrentManagedThreadId != _onlyThread) return;
        lock (_gate)
        {
            _names.Add(name);
            _lastEventTicks = DateTime.UtcNow.Ticks;
        }
    }

    /// <summary>Avisos en el orden en que llegaron.</summary>
    public List<string> Names
    {
        get { lock (_gate) return _names.ToList(); }
    }

    /// <summary>Avisos sin repetir, en orden alfabético (escenarios con base de datos o tareas en segundo plano).</summary>
    public List<string> Set => Names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

    public void Clear()
    {
        lock (_gate) _names.Clear();
    }

    /// <summary>Espera a que no lleguen avisos durante <paramref name="quiet"/> (tareas lanzadas sin esperar).</summary>
    public async Task WaitForQuietAsync(int quietMs = 400, int maxMs = 15000)
    {
        var started = DateTime.UtcNow;
        while ((DateTime.UtcNow - started).TotalMilliseconds < maxMs)
        {
            await Task.Delay(50);
            long last;
            lock (_gate) last = _lastEventTicks;
            if ((DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalMilliseconds >= quietMs) return;
        }
        Assert.Fail($"Los avisos no se detuvieron en {maxMs} ms");
    }

    public void Dispose()
    {
        foreach (var (source, handler) in _subscriptions) source.PropertyChanged -= handler;
        _subscriptions.Clear();
    }
}
