using System;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TPVC.Models;

/// <summary>Sección de antecedentes NOM-004 en la ficha: plegable, con resumen de una línea.</summary>
public partial class AntecedentSection : ObservableObject
{
    public AntecedentSection(string key, string title, string summary, string detail)
    {
        Key = key;
        Title = title;
        Summary = summary;
        Detail = detail;
    }

    public string Key { get; }
    public string Title { get; }
    public string Summary { get; }
    public string Detail { get; }
    public bool IsEmpty => string.IsNullOrWhiteSpace(Detail);

    [ObservableProperty]
    private bool _isOpen;
}

public enum TimelineKind
{
    Consultation,
    Vitals,
    Prescription,
    Appointment
}

/// <summary>Una entrada del historial único de la ficha (consulta, signos, receta o cita).</summary>
public partial class TimelineEntry : ObservableObject
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    public TimelineEntry(TimelineKind kind, DateTime date, string title, string detail, string side = "")
    {
        Kind = kind;
        Date = date;
        Title = title;
        Detail = detail;
        Side = side;
    }

    public TimelineKind Kind { get; }
    public DateTime Date { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Side { get; }

    public string DayText => Date.ToString("d MMM", Mexican).TrimEnd('.');
    public string YearText => Date.ToString("yyyy", Mexican);

    public string KindText => Kind switch
    {
        TimelineKind.Consultation => "Consulta",
        TimelineKind.Vitals => "Signos",
        TimelineKind.Prescription => "Receta",
        _ => "Cita"
    };

    /// <summary>Notas largas: se muestran recortadas con "Ver completa".</summary>
    public bool IsLongDetail => Detail.Length > 140 || Detail.Count(c => c == '\n') > 1;

    public bool IsConsultation => Kind == TimelineKind.Consultation;
    public bool IsVitals => Kind == TimelineKind.Vitals;
    public bool IsPrescription => Kind == TimelineKind.Prescription;

    /// <summary>Las notas largas se muestran recortadas hasta que se piden completas.</summary>
    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>Último valor de un signo vital con su tendencia frente a la medición anterior.</summary>
public sealed record VitalSummary(string Label, string Value, string Trend, bool IsUp);

/// <summary>Día de la franja semanal del panel de agenda.</summary>
public sealed partial class AgendaDayItem : ObservableObject
{
    private static readonly CultureInfo Mexican = CultureInfo.GetCultureInfo("es-MX");

    public AgendaDayItem(DateTime date, int count)
    {
        Date = date.Date;
        Count = count;
    }

    public DateTime Date { get; }
    public int Count { get; }
    public string DayName
    {
        get
        {
            var name = Date.ToString("ddd", Mexican).TrimEnd('.');
            return name.Length == 0 ? name : char.ToUpper(name[0], Mexican) + name[1..];
        }
    }
    public string DayNumber => Date.Day.ToString(CultureInfo.InvariantCulture);
    public string CountText => Count == 0 ? "libre" : Count == 1 ? "1 cita" : $"{Count} citas";
    public bool IsToday => Date == DateTime.Today;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Hueco del día en el panel de agenda: una cita o un horario libre.</summary>
public sealed partial class AgendaSlot : ObservableObject
{
    public AgendaSlot(TimeSpan time, PatientAppointment? appointment)
    {
        Time = time;
        Appointment = appointment;
    }

    public TimeSpan Time { get; }
    public PatientAppointment? Appointment { get; }
    public string TimeText => $"{Time.Hours:D2}:{Time.Minutes:D2}";
    public bool IsFree => Appointment == null;
    public bool HasAppointment => Appointment != null;
    public string Title => Appointment?.PatientName ?? "Libre";
    public string Subtitle => Appointment == null
        ? "+ Agendar aquí"
        : $"{Appointment.Reason} · {Appointment.DurationMinutes} min";
    public string Status => Appointment?.Status ?? string.Empty;
    public bool IsAttended => Appointment?.IsAttended ?? false;
    public bool IsConfirmed => Appointment?.Status == "Confirmada";

    /// <summary>El formulario para agendar se abre dentro del propio hueco.</summary>
    [ObservableProperty]
    private bool _isFormOpen;
}

/// <summary>Sección del alta/edición en la barra lateral del formulario.</summary>
public sealed record PatientFormSection(string Key, string Number, string Title, string Status, bool IsComplete, bool IsRequired, bool IsOpen);
