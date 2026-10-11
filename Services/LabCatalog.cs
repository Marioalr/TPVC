using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>Un estudio del catálogo: nombre, área y la abreviatura con que lo escribe el médico (BH, QS, EGO…).</summary>
public sealed record LabCatalogStudy(string Name, string Area, string Abbreviation = "")
{
    /// <summary>"Biometría hemática completa (BH)", como se ve en las sugerencias.</summary>
    public string Display => Abbreviation.Length > 0 ? $"{Name} ({Abbreviation})" : Name;

    /// <summary>true si no es del catálogo: lo escribió el consultorio en otra consulta.</summary>
    public bool IsCustom { get; init; }

    /// <summary>El texto corto del botón de los frecuentes ("BH", "QS 6"); el nombre si no tiene.</summary>
    public string Chip { get; init; } = string.Empty;

    public string ChipText => Chip.Length > 0 ? Chip : Name;
}

/// <summary>Un grupo de estudios que se piden juntos (chequeo general, control de diabetes…).</summary>
public sealed record LabPanel(string Name, IReadOnlyList<string> Studies)
{
    /// <summary>Los estudios del paquete, para la ayuda del botón.</summary>
    public string Summary => string.Join(", ", Studies);
}

/// <summary>Una indicación de un clic para el paciente: el texto del botón y lo que agrega.</summary>
public sealed record LabIndicationPreset(string Label, string Text);

/// <summary>
/// Los estudios de laboratorio que se piden en un consultorio de primer contacto, agrupados por área (desde v1.25.0).
/// Es la lista de sugerencias de la orden; lo que el médico escriba fuera de ella también se guarda y vuelve a
/// sugerirse en las consultas siguientes.
/// </summary>
public static class LabCatalog
{
    public const string Hematology = "Hematología";
    public const string Chemistry = "Química clínica";
    public const string Urinalysis = "Urianálisis";
    public const string Stool = "Coprología y parasitología";
    public const string Immunology = "Inmunología y serología";
    public const string Hormones = "Hormonas";
    public const string Microbiology = "Microbiología";
    public const string OtherArea = "Otros";

    /// <summary>Las áreas en el orden en que salen en la hoja.</summary>
    public static readonly IReadOnlyList<string> Areas = new[]
    {
        Hematology, Chemistry, Urinalysis, Stool, Immunology, Hormones, Microbiology, OtherArea
    };

    public static readonly IReadOnlyList<LabCatalogStudy> Studies = new LabCatalogStudy[]
    {
        new("Biometría hemática completa", Hematology, "BH"),
        new("Velocidad de sedimentación globular", Hematology, "VSG"),
        new("Tiempos de coagulación (TP, TTPa, INR)", Hematology, "TP"),
        new("Grupo sanguíneo y factor Rh", Hematology),
        new("Reticulocitos", Hematology),
        new("Frotis de sangre periférica", Hematology),

        new("Glucosa en ayuno", Chemistry),
        new("Química sanguínea de 3 elementos", Chemistry, "QS 3"),
        new("Química sanguínea de 6 elementos", Chemistry, "QS 6"),
        new("Química sanguínea de 27 elementos", Chemistry, "QS 27"),
        new("Hemoglobina glucosilada", Chemistry, "HbA1c"),
        new("Curva de tolerancia a la glucosa", Chemistry, "CTOG"),
        new("Perfil de lípidos", Chemistry),
        new("Pruebas de función hepática", Chemistry, "PFH"),
        new("Electrolitos séricos (Na, K, Cl)", Chemistry, "ES"),
        new("Creatinina sérica", Chemistry),
        new("Ácido úrico", Chemistry),
        new("Calcio, fósforo y magnesio", Chemistry),
        new("Amilasa y lipasa", Chemistry),
        new("Perfil de hierro (hierro, ferritina y transferrina)", Chemistry),
        new("Vitamina D (25-hidroxi)", Chemistry),
        new("Vitamina B12", Chemistry),
        new("Microalbuminuria", Chemistry),

        new("Examen general de orina", Urinalysis, "EGO"),
        new("Urocultivo con antibiograma", Urinalysis),
        new("Depuración de creatinina en orina de 24 horas", Urinalysis),
        new("Proteínas en orina de 24 horas", Urinalysis),

        new("Coproparasitoscópico seriado (3 muestras)", Stool, "CPS"),
        new("Coprológico", Stool),
        new("Sangre oculta en heces", Stool),
        new("Amiba en fresco", Stool),
        new("Coprocultivo", Stool),
        new("Antígeno de Helicobacter pylori en heces", Stool),
        new("Rotavirus en heces", Stool),

        new("Proteína C reactiva", Immunology, "PCR"),
        new("Factor reumatoide", Immunology, "FR"),
        new("Antiestreptolisinas", Immunology, "ASO"),
        new("Reacciones febriles", Immunology),
        new("Prueba de embarazo en sangre (fracción beta de HGC)", Immunology, "HGC"),
        new("Prueba de embarazo en orina", Immunology),
        new("VDRL", Immunology),
        new("VIH (anticuerpos)", Immunology, "VIH"),
        new("Antígeno de superficie de hepatitis B", Immunology, "HBsAg"),
        new("Anticuerpos contra hepatitis C", Immunology, "VHC"),
        new("Antígeno prostático específico total y libre", Immunology, "APE"),
        new("Dengue (antígeno NS1 e IgM/IgG)", Immunology),
        new("Influenza A y B (antígeno)", Immunology),
        new("SARS-CoV-2 (antígeno)", Immunology),

        new("Perfil tiroideo (TSH, T3 y T4)", Hormones),
        new("Hormona estimulante de la tiroides", Hormones, "TSH"),
        new("Insulina basal", Hormones),
        new("Perfil hormonal femenino (FSH, LH, estradiol, progesterona y prolactina)", Hormones),
        new("Prolactina", Hormones),
        new("Testosterona total", Hormones),
        new("Cortisol matutino", Hormones),

        new("Exudado faríngeo con cultivo", Microbiology),
        new("Exudado vaginal con cultivo", Microbiology),
        new("Cultivo de secreción con antibiograma", Microbiology),
        new("Hemocultivo", Microbiology)
    };

    /// <summary>Los que más se piden: botones de un clic en la sección de laboratorio.</summary>
    public static readonly IReadOnlyList<LabCatalogStudy> Frequent = new[]
    {
        ("BH", "Biometría hemática completa"), ("QS 6", "Química sanguínea de 6 elementos"), ("EGO", "Examen general de orina"),
        ("Perfil de lípidos", "Perfil de lípidos"), ("HbA1c", "Hemoglobina glucosilada"), ("PFH", "Pruebas de función hepática"),
        ("Perfil tiroideo", "Perfil tiroideo (TSH, T3 y T4)"), ("Coproparasitoscópico", "Coproparasitoscópico seriado (3 muestras)"),
        ("PCR", "Proteína C reactiva"), ("Embarazo en orina", "Prueba de embarazo en orina")
    }.Select(x => Find(x.Item2)! with { Chip = x.Item1 }).ToList();

    public static readonly IReadOnlyList<LabPanel> Panels = new[]
    {
        new LabPanel("Chequeo general", new[]
        {
            "Biometría hemática completa", "Química sanguínea de 6 elementos", "Perfil de lípidos", "Examen general de orina"
        }),
        new LabPanel("Control de diabetes", new[]
        {
            "Glucosa en ayuno", "Hemoglobina glucosilada", "Perfil de lípidos", "Química sanguínea de 3 elementos", "Microalbuminuria", "Examen general de orina"
        }),
        new LabPanel("Control prenatal", new[]
        {
            "Biometría hemática completa", "Grupo sanguíneo y factor Rh", "Glucosa en ayuno", "Examen general de orina", "Urocultivo con antibiograma",
            "VDRL", "VIH (anticuerpos)"
        })
    };

    /// <summary>Indicaciones de un clic para el paciente.</summary>
    public static readonly IReadOnlyList<LabIndicationPreset> IndicationPresets = new LabIndicationPreset[]
    {
        new("Ayuno", "Ayuno de 8 a 12 horas (puede tomar agua)."),
        new("Primera orina", "Orina: la primera de la mañana, del chorro medio, en frasco estéril."),
        new("Antes del antibiótico", "Tomar las muestras antes de iniciar el antibiótico."),
        new("Traer resultados", "Traer los resultados a su próxima consulta.")
    };

    /// <summary>El estudio del catálogo con ese nombre o esa abreviatura (sin importar acentos ni mayúsculas); null si no está.</summary>
    public static LabCatalogStudy? Find(string? nameOrAbbreviation)
    {
        var key = TextSearch.Normalize(nameOrAbbreviation);
        if (key.Length == 0) return null;
        return Studies.FirstOrDefault(s => TextSearch.Normalize(s.Name) == key)
               ?? Studies.FirstOrDefault(s => s.Abbreviation.Length > 0 && TextSearch.Normalize(s.Abbreviation) == key);
    }

    /// <summary>El área de un estudio: la del catálogo, o "Otros" si lo escribió el médico.</summary>
    public static string AreaOf(string? name) => Find(name)?.Area ?? OtherArea;

    /// <summary>Lugar del área en la hoja (las que no conoce, al final).</summary>
    public static int AreaOrder(string? area)
    {
        for (int i = 0; i < Areas.Count; i++)
        {
            if (string.Equals(Areas[i], area, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return Areas.Count;
    }

    /// <summary>
    /// Sugerencias para lo escrito: el catálogo y los estudios que el consultorio ya pidió (<paramref name="custom"/>).
    /// Cada palabra escrita debe aparecer en el nombre o la abreviatura; primero la abreviatura exacta, luego lo que
    /// empieza igual, luego una palabra que empieza igual y al final lo que la contiene.
    /// </summary>
    public static IReadOnlyList<LabCatalogStudy> Search(string? query, IEnumerable<LabCatalogStudy>? custom = null, int max = 8)
    {
        var q = TextSearch.Normalize(query);
        if (q.Length == 0) return Array.Empty<LabCatalogStudy>();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var candidates = Studies.AsEnumerable();
        if (custom != null)
        {
            var known = new HashSet<string>(Studies.Select(s => TextSearch.Normalize(s.Name)), StringComparer.Ordinal);
            candidates = candidates.Concat(custom.Where(c => known.Add(TextSearch.Normalize(c.Name))));
        }

        return candidates
            .Select(study =>
            {
                var name = TextSearch.Normalize(study.Name);
                var abbreviation = TextSearch.Normalize(study.Abbreviation);
                var text = name + " " + abbreviation;
                if (!words.All(w => text.Contains(w, StringComparison.Ordinal))) return (study, rank: -1);
                int rank = abbreviation.Length > 0 && abbreviation == q ? 0
                    : name.StartsWith(q, StringComparison.Ordinal) ? 1
                    : abbreviation.Length > 0 && abbreviation.StartsWith(q, StringComparison.Ordinal) ? 2
                    : (" " + name).Contains(" " + words[0], StringComparison.Ordinal) ? 3
                    : 4;
                return (study, rank);
            })
            .Where(x => x.rank >= 0)
            .OrderBy(x => x.rank)
            .ThenBy(x => x.study.Name.Length)
            .Take(max)
            .Select(x => x.study)
            .ToList();
    }

    /// <summary>El estudio que entra a la orden: el del catálogo (con su nombre completo y su área) o lo escrito, en "Otros".</summary>
    public static LabOrderStudy ToStudy(string name)
    {
        var known = Find(name);
        return known != null
            ? new LabOrderStudy { Name = known.Name, Area = known.Area }
            : new LabOrderStudy { Name = name.Trim(), Area = OtherArea };
    }
}
