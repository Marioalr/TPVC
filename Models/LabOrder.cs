using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TPVC.Models;

/// <summary>Un estudio de la orden de laboratorio: su nombre y el área del laboratorio que lo procesa.</summary>
public sealed class LabOrderStudy
{
    public string Name { get; set; } = string.Empty;

    /// <summary>"Hematología", "Química clínica"…; "Otros" en un estudio escrito a mano que no está en el catálogo.</summary>
    public string Area { get; set; } = string.Empty;

    public LabOrderStudy Clone() => new() { Name = Name, Area = Area };
}

/// <summary>
/// La orden de estudios de laboratorio de una consulta (desde v1.25.0): los estudios y las indicaciones para el
/// paciente. Es opcional: una consulta sin estudios guarda "" en <see cref="PatientConsultation.LabOrderJson"/>.
/// </summary>
public sealed class LabOrder
{
    // Acentos legibles en la base y en la nube (la app de Kotlin lee el mismo JSON)
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public List<LabOrderStudy> Studies { get; set; } = new();

    /// <summary>Indicaciones para el paciente (ayuno, primera orina de la mañana…); puede ir vacío.</summary>
    public string Indications { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsEmpty => Studies.Count == 0;

    /// <summary>"Biometría hemática completa, Examen general de orina" (tarjeta del historial y resumen de la sección).</summary>
    [JsonIgnore]
    public string Summary => string.Join(", ", Studies.Select(s => s.Name));

    /// <summary>La orden guardada; vacía si no hay o si el texto no se puede leer.</summary>
    public static LabOrder FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new LabOrder();
        try
        {
            var order = JsonSerializer.Deserialize<LabOrder>(json, JsonOptions) ?? new LabOrder();
            order.Studies = order.Studies
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => new LabOrderStudy { Name = s.Name.Trim(), Area = (s.Area ?? string.Empty).Trim() })
                .ToList();
            order.Indications = (order.Indications ?? string.Empty).Trim();
            return order;
        }
        catch (JsonException)
        {
            return new LabOrder();
        }
    }

    /// <summary>El JSON que se guarda en la consulta; "" si no hay estudios (las indicaciones solas no son una orden).</summary>
    public string ToJson() => IsEmpty ? string.Empty : JsonSerializer.Serialize(this, JsonOptions);

    public LabOrder Clone() => new()
    {
        Studies = Studies.Select(s => s.Clone()).ToList(),
        Indications = Indications
    };
}
