using System;
using System.Collections.Generic;
using System.Linq;
using TPVC.Services;

namespace TPVC.Models;

public class Cie10Diagnosis
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description => Name;
    public string Category { get; set; } = string.Empty;

    public string DisplayText => $"{Code} - {Name}";

    public override string ToString() => DisplayText;

    private static readonly List<Cie10Diagnosis> _allDiagnoses = new()
    {
        // ==========================================
        // ENFERMEDADES INFECCIOSAS Y PARASITARIAS (A00-B99)
        // ==========================================
        new() { Code = "A09", Name = "Diarrea y gastroenteritis de presunto origen infeccioso", Category = "Infecciosas" },
        new() { Code = "A08.4", Name = "Gastroenteritis viral, no especificada", Category = "Infecciosas" },
        new() { Code = "A06.0", Name = "Disentería amebiana aguda (Amebiasis)", Category = "Infecciosas" },
        new() { Code = "A04.9", Name = "Infección bacteriana intestinal, no especificada", Category = "Infecciosas" },
        new() { Code = "B82.9", Name = "Parasitosis intestinal, sin otra especificación", Category = "Infecciosas" },
        new() { Code = "B01.9", Name = "Varicela sin complicaciones", Category = "Infecciosas" },
        new() { Code = "B02.9", Name = "Herpes zóster sin complicaciones", Category = "Infecciosas" },
        new() { Code = "B00.9", Name = "Infección por virus del herpes, no especificada (Herpes simple)", Category = "Infecciosas" },
        new() { Code = "B34.9", Name = "Infección viral, no especificada", Category = "Infecciosas" },
        new() { Code = "B35.9", Name = "Dermatofitosis (Tiña / Micosis), no especificada", Category = "Infecciosas" },
        new() { Code = "B37.9", Name = "Candidiasis, no especificada", Category = "Infecciosas" },
        new() { Code = "B37.3", Name = "Candidiasis de la vulva y de la vagina", Category = "Infecciosas" },

        // ==========================================
        // APARATO RESPIRATORIO (J00-J99)
        // ==========================================
        new() { Code = "J00", Name = "Rinofaringitis aguda (Resfriado común)", Category = "Respiratorio" },
        new() { Code = "J01.9", Name = "Sinusitis aguda, no especificada", Category = "Respiratorio" },
        new() { Code = "J02.9", Name = "Faringitis aguda, no especificada", Category = "Respiratorio" },
        new() { Code = "J03.9", Name = "Amigdalitis aguda, no especificada", Category = "Respiratorio" },
        new() { Code = "J04.0", Name = "Laringitis aguda", Category = "Respiratorio" },
        new() { Code = "J06.9", Name = "Infección respiratoria aguda de vías superiores (IRAS), no especificada", Category = "Respiratorio" },
        new() { Code = "J11.1", Name = "Influenza con otras manifestaciones respiratorias", Category = "Respiratorio" },
        new() { Code = "J12.8", Name = "Neumonía por otros virus (incluye COVID-19 confirmado)", Category = "Respiratorio" },
        new() { Code = "J18.9", Name = "Neumonía, no especificada", Category = "Respiratorio" },
        new() { Code = "J20.9", Name = "Bronquitis aguda, no especificada", Category = "Respiratorio" },
        new() { Code = "J45.9", Name = "Asma, no especificada (Crisis asmática / Broncoespasmo)", Category = "Respiratorio" },
        new() { Code = "J44.9", Name = "Enfermedad pulmonar obstructiva crónica (EPOC), no especificada", Category = "Respiratorio" },
        new() { Code = "J30.4", Name = "Rinitis alérgica, no especificada", Category = "Respiratorio" },
        new() { Code = "R05", Name = "Tos persistente en estudio", Category = "Respiratorio" },

        // ==========================================
        // APARATO CIRCULATORIO (I00-I99)
        // ==========================================
        new() { Code = "I10", Name = "Hipertensión arterial esencial (primaria)", Category = "Cardiovascular" },
        new() { Code = "I11.9", Name = "Enfermedad cardíaca hipertensiva sin insuficiencia cardíaca", Category = "Cardiovascular" },
        new() { Code = "I20.9", Name = "Angina de pecho, no especificada", Category = "Cardiovascular" },
        new() { Code = "I25.9", Name = "Cardiopatía isquémica crónica, no especificada", Category = "Cardiovascular" },
        new() { Code = "I50.9", Name = "Insuficiencia cardíaca, no especificada", Category = "Cardiovascular" },
        new() { Code = "I83.9", Name = "Venas varicosas de los miembros inferiores (Várices)", Category = "Cardiovascular" },
        new() { Code = "I84.9", Name = "Hemorroides, no especificadas", Category = "Cardiovascular" },
        new() { Code = "I95.9", Name = "Hipotensión arterial, no especificada", Category = "Cardiovascular" },

        // ==========================================
        // ENDOCRINAS, NUTRICIONALES Y METABÓLICAS (E00-E90)
        // ==========================================
        new() { Code = "E11.9", Name = "Diabetes mellitus tipo 2 sin mención de complicación", Category = "Endocrino" },
        new() { Code = "E11.6", Name = "Diabetes mellitus tipo 2 con otras complicaciones especificadas", Category = "Endocrino" },
        new() { Code = "E10.9", Name = "Diabetes mellitus tipo 1 sin complicaciones", Category = "Endocrino" },
        new() { Code = "E66.9", Name = "Obesidad, no especificada", Category = "Endocrino" },
        new() { Code = "E66.0", Name = "Obesidad debida a exceso de calorías", Category = "Endocrino" },
        new() { Code = "E78.5", Name = "Hiperlipidemia, no especificada (Dislipidemia)", Category = "Endocrino" },
        new() { Code = "E78.0", Name = "Hipercolesterolemia pura", Category = "Endocrino" },
        new() { Code = "E78.1", Name = "Hipertrigliceridemia pura", Category = "Endocrino" },
        new() { Code = "E03.9", Name = "Hipotiroidismo, no especificado", Category = "Endocrino" },
        new() { Code = "E05.9", Name = "Tirotoxicosis / Hipertiroidismo, no especificado", Category = "Endocrino" },
        new() { Code = "E79.0", Name = "Hiperuricemia sin signos de artritis / Gota", Category = "Endocrino" },
        new() { Code = "M10.9", Name = "Gota, no especificada", Category = "Endocrino" },

        // ==========================================
        // APARATO DIGESTIVO (K00-K93)
        // ==========================================
        new() { Code = "K21.9", Name = "Enfermedad por reflujo gastroesofágico (ERGE) sin esofagitis", Category = "Digestivo" },
        new() { Code = "K29.7", Name = "Gastritis, no especificada", Category = "Digestivo" },
        new() { Code = "K29.1", Name = "Otras gastritis agudas (Gastropatía erosiva)", Category = "Digestivo" },
        new() { Code = "K25.9", Name = "Úlcera gástrica, no especificada como aguda ni crónica", Category = "Digestivo" },
        new() { Code = "K30", Name = "Dispepsia funcional", Category = "Digestivo" },
        new() { Code = "K58.9", Name = "Síndrome del colon irritable (Colitis) sin diarrea", Category = "Digestivo" },
        new() { Code = "K58.0", Name = "Síndrome del colon irritable con diarrea", Category = "Digestivo" },
        new() { Code = "K59.0", Name = "Constipación / Estreñimiento crónico", Category = "Digestivo" },
        new() { Code = "K80.2", Name = "Cálculo de la vesícula biliar sin colecistitis (Colelitiasis)", Category = "Digestivo" },
        new() { Code = "K81.9", Name = "Colecistitis, no especificada", Category = "Digestivo" },
        new() { Code = "K35.8", Name = "Apendicitis aguda, otra y la no especificada", Category = "Digestivo" },
        new() { Code = "K02.9", Name = "Caries dental, no especificada / Odontalgia", Category = "Digestivo" },

        // ==========================================
        // APARATO GENITOURINARIO (N00-N99)
        // ==========================================
        new() { Code = "N39.0", Name = "Infección del tracto urinario (IVU), sitio no especificado", Category = "Genitourinario" },
        new() { Code = "N30.9", Name = "Cistitis, no especificada", Category = "Genitourinario" },
        new() { Code = "N20.9", Name = "Cálculo urinario, no especificado (Urolitiasis / Litiasis renal)", Category = "Genitourinario" },
        new() { Code = "N23", Name = "Cólico renal, no especificado", Category = "Genitourinario" },
        new() { Code = "N40", Name = "Hiperplasia prostática benigna (HPB)", Category = "Genitourinario" },
        new() { Code = "N76.0", Name = "Vaginitis aguda / Cervicovaginitis", Category = "Genitourinario" },
        new() { Code = "N94.6", Name = "Dismenorrea, no especificada", Category = "Genitourinario" },
        new() { Code = "N92.6", Name = "Menstruación irregular, no especificada", Category = "Genitourinario" },
        new() { Code = "N95.1", Name = "Estados menopáusicos y climatéricos", Category = "Genitourinario" },

        // ==========================================
        // SISTEMA OSTEOMUSCULAR Y TEJIDO CONECTIVO (M00-M99)
        // ==========================================
        new() { Code = "M54.5", Name = "Lumbago no especificado (Lumbalgia mecánica)", Category = "Músculo-esquelético" },
        new() { Code = "M54.2", Name = "Cervicalgia", Category = "Músculo-esquelético" },
        new() { Code = "M79.1", Name = "Mialgia / Contractura muscular", Category = "Músculo-esquelético" },
        new() { Code = "M19.9", Name = "Osteoartrosis (Artrosis), sitio no especificado", Category = "Músculo-esquelético" },
        new() { Code = "M17.9", Name = "Gonartrosis (Artrosis de rodilla), no especificada", Category = "Músculo-esquelético" },
        new() { Code = "M06.9", Name = "Artritis reumatoide, no especificada", Category = "Músculo-esquelético" },
        new() { Code = "M77.9", Name = "Entesopatía / Tendinitis, no especificada", Category = "Músculo-esquelético" },
        new() { Code = "S93.4", Name = "Esguince y desgarro del tobillo", Category = "Músculo-esquelético" },
        new() { Code = "S63.5", Name = "Esguince de la muñeca", Category = "Músculo-esquelético" },
        new() { Code = "T14.0", Name = "Traumatismo superficial / Contusión, no especificada", Category = "Músculo-esquelético" },

        // ==========================================
        // PIEL Y TEJIDO SUBCUTÁNEO (L00-L99)
        // ==========================================
        new() { Code = "L20.9", Name = "Dermatitis atópica, no especificada", Category = "Dermatología" },
        new() { Code = "L23.9", Name = "Dermatitis alérgica de contacto, causa no especificada", Category = "Dermatología" },
        new() { Code = "L50.9", Name = "Urticaria, no especificada", Category = "Dermatología" },
        new() { Code = "L70.9", Name = "Acné, no especificado", Category = "Dermatología" },
        new() { Code = "L03.9", Name = "Celulitis de sitio no especificado (Infección de tejidos blandos)", Category = "Dermatología" },
        new() { Code = "L02.9", Name = "Absceso cutáneo, forúnculo y ántrax", Category = "Dermatología" },
        new() { Code = "L01.0", Name = "Impétigo [cualquier sitio]", Category = "Dermatología" },
        new() { Code = "L60.0", Name = "Uña encarnada (Onicocriptosis)", Category = "Dermatología" },
        new() { Code = "B35.1", Name = "Tiña de las uñas (Onicomicosis)", Category = "Dermatología" },

        // ==========================================
        // SISTEMA NERVIOSO Y SALUD MENTAL (G00-G99, F00-F99)
        // ==========================================
        new() { Code = "G43.9", Name = "Migraña, no especificada", Category = "Neurología" },
        new() { Code = "G44.2", Name = "Cefalea tensional", Category = "Neurología" },
        new() { Code = "R51", Name = "Cefalea (Dolor de cabeza no especificado)", Category = "Neurología" },
        new() { Code = "R42", Name = "Mareo y desvanecimiento (Vértigo periférico)", Category = "Neurología" },
        new() { Code = "F41.9", Name = "Trastorno de ansiedad, no especificado (Crisis de ansiedad)", Category = "Salud Mental" },
        new() { Code = "F41.1", Name = "Trastorno de ansiedad generalizada", Category = "Salud Mental" },
        new() { Code = "F32.9", Name = "Episodio depresivo, no especificado", Category = "Salud Mental" },
        new() { Code = "G47.0", Name = "Trastornos del inicio y del mantenimiento del sueño (Insomnio)", Category = "Salud Mental" },

        // ==========================================
        // SÍNTOMAS, SIGNOS Y CONTROL GENERAL (R00-R99, Z00-Z99)
        // ==========================================
        new() { Code = "R50.9", Name = "Fiebre, no especificada (Síndrome febril)", Category = "Síntomas Generales" },
        new() { Code = "R53", Name = "Malestar y fatiga / Astenia y adinamia", Category = "Síntomas Generales" },
        new() { Code = "R10.4", Name = "Otros dolores abdominales y los no especificados", Category = "Síntomas Generales" },
        new() { Code = "R11", Name = "Náusea y vómito", Category = "Síntomas Generales" },
        new() { Code = "Z00.0", Name = "Examen médico general (Check-up / Control de salud)", Category = "Control Preventivo" },
        new() { Code = "Z01.0", Name = "Examen de ojos y de la visión", Category = "Control Preventivo" },
        new() { Code = "Z02.1", Name = "Examen médico para admisión preempleo / Certificado médico", Category = "Control Preventivo" },
        new() { Code = "Z71.3", Name = "Consulta para asesoramiento y supervisión dietética", Category = "Control Preventivo" },
        new() { Code = "Z34.9", Name = "Supervisión de embarazo normal, no especificado (Control prenatal)", Category = "Control Preventivo" }
    };

    public static IEnumerable<Cie10Diagnosis> Search(string query, int maxResults = 15)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _allDiagnoses.Take(maxResults);
        }

        var normalized = TextSearch.Normalize(query);
        return _allDiagnoses
            .Where(d => TextSearch.Matches(normalized, d.Code, d.Name, d.Category))
            .Take(maxResults);
    }
}
