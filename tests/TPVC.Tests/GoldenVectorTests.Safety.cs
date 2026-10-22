using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using TPVC.Helpers;
using TPVC.Models;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>Paso 8.26, seguridad e identidad: allergy-check, patient-identity y date-mask.</summary>
public partial class GoldenVectorTests
{
    // ================================================================== contracts/golden/allergy-check.json

    [TestMethod]
    public void AllergyCheck_MatchesTheVectors()
    {
        static JsonObject Med(string? allergies, string? medicineName, string? productName = null, string? generic = null, string? activeIngredient = null,
            (string Name, string? Generic)[]? catalog = null)
        {
            var o = new JsonObject { ["check"] = "medication", ["allergies"] = allergies, ["medicineName"] = medicineName };
            if (activeIngredient != null) o["activeIngredient"] = activeIngredient;
            if (productName != null || generic != null) o["product"] = new JsonObject { ["name"] = productName ?? string.Empty, ["genericName"] = generic };
            if (catalog != null)
            {
                o["catalog"] = new JsonArray(catalog.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["genericName"] = c.Generic }).ToArray());
            }
            return o;
        }

        // Revisión 20: el catálogo con que se reconoce una alergia escrita con la marca
        var brandCatalog = new (string, string?)[]
        {
            ("TEMPRA (PARACETAMOL) 500MG C/20 TABS", "Paracetamol"),
            ("Tempra infantil 160 mg/5 ml jarabe", "Paracetamol"),
            ("Flanax 550 mg", "Naproxeno sódico"),
            ("Paracetamol 500 mg", "Paracetamol"),
            ("Bactrim F", "Sulfametoxazol/Trimetoprima"),
            ("Gasas estériles", null),
            ("Cozaar 50 mg", "Losartán potásico"),
            ("Glucophage 850 mg", "Clorhidrato de metformina 850 mg"),
            ("Viagra 50 mg", "Sildenafil citrato"),
            ("Ecotrin", "AAS 100 mg"),
        };

        var cases = new List<RuleCase>
        {
            new("marca → genérico: alergia a Paracetamol y se receta Tempra (el producto lleva su genérico)", Med("Paracetamol", "Tempra", "Tempra", "Paracetamol")),
            new("marca → genérico: el renglón de la receta de antes de v1.46 (la marca entre paréntesis)", Med("paracetamol", "Paracetamol 500 mg tabletas (Tempra)", "Tempra", "Paracetamol")),
            new("marca → genérico: el renglón de la receta (el principio activo entre paréntesis)", Med("paracetamol", "Tempra 500 mg tabletas (Paracetamol)", "Tempra 500 mg", "Paracetamol")),
            new("marca → genérico: el renglón sin el principio activo avisa por el genérico del producto", Med("Paracetamol", "Tempra 500 mg tabletas", "Tempra 500 mg", "Paracetamol")),
            new("renglón con el principio activo oculto y sin producto en el catálogo: avisa por su principio activo guardado", Med("Paracetamol", "Tempra 500 mg tabletas", activeIngredient: "Paracetamol")),
            new("el principio activo guardado no se repite si el nombre ya lo dice", Med("Penicilina", "Tempra 500 mg tabletas (Paracetamol)", activeIngredient: "Paracetamol")),
            new("genérico → marca sin catálogo: alergia a «Tempra» y se receta Paracetamol: no avisa (no sabe qué es Tempra)", Med("Tempra", "Paracetamol 500 mg", "Paracetamol 500 mg", "Paracetamol")),
            new("genérico → marca (revisión 20): alergia a «Tempra» y se receta Paracetamol; el catálogo dice que Tempra es paracetamol", Med("Tempra", "Paracetamol 500 mg", "Paracetamol 500 mg", "Paracetamol", catalog: brandCatalog)),
            new("genérico → marca: escrito a mano, sin producto elegido", Med("tempra", "Paracetamol gotas", catalog: brandCatalog)),
            new("genérico → marca: la alergia es el nombre completo del producto", Med("Bactrim F", "Sulfametoxazol/Trimetoprima 400/80 mg", catalog: brandCatalog)),
            new("genérico → marca y familia: alergia a «Flanax» (naproxeno) y se receta ibuprofeno (AINES)", Med("Flanax", "Ibuprofeno 400 mg", catalog: brandCatalog)),
            new("genérico → marca: una marca que no está en el catálogo no avisa", Med("Panadol", "Paracetamol 500 mg", catalog: brandCatalog)),
            new("genérico → marca: un producto sin genérico no aporta nada", Med("Gasas estériles", "Paracetamol 500 mg", catalog: brandCatalog)),
            new("genérico → marca: otro medicamento no avisa", Med("Tempra", "Loratadina 10 mg", catalog: brandCatalog)),
            new("genérico → marca con sal: alergia a «Cozaar» (losartán potásico) y se receta Losartán 50 mg (por su base)", Med("Cozaar", "Losartán 50 mg", catalog: brandCatalog)),
            new("genérico → marca con sal antes: «Glucophage» (clorhidrato de metformina) y Metformina 850 mg tabletas", Med("Glucophage", "Metformina 850 mg tabletas", catalog: brandCatalog)),
            new("genérico → marca con sal: la sal sola no avisa («Viagra», sildenafil citrato, y Citrato de potasio)", Med("Viagra", "Citrato de potasio 1080 mg", catalog: brandCatalog)),
            new("genérico → marca con sal: otra sal del mismo ion no avisa («Cozaar» y Cloruro de potasio)", Med("Cozaar", "Cloruro de potasio 600 mg", catalog: brandCatalog)),
            new("genérico → marca con sal: la base tiene que ser palabra completa («Cozaar» y Losartanex, que no existe)", Med("Cozaar", "Losartanex 5 mg", catalog: brandCatalog)),
            new("genérico → marca: una base de menos de 4 letras no se usa («Ecotrin», AAS 100 mg, y AAS Protect sí avisa por la familia aspirina)", Med("Ecotrin", "AAS Protect", catalog: brandCatalog)),
            new("texto libre sin producto: solo lo escrito", Med("Paracetamol", "Tempra gotas")),
            new("familia: penicilina y amoxicilina", Med("Penicilina", "Amoxicilina 500 mg", "Amoxicilina 500 mg", "Amoxicilina")),
            new("familia con acento: clavulánico y Augmentin", Med("Ácido clavulánico", "Augmentin 875/125 mg")),
            new("familia AINES por su nombre", Med("AINEs", "Ibuprofeno 400 mg")),
            new("ácido acetilsalicílico es de los AINES (la primera familia que coincide)", Med("Ácido acetilsalicílico", "Aspirina Protect 100 mg")),
            new("AAS solo es de la familia aspirina", Med("AAS", "Aspirina 500 mg")),
            new("varias alergias en renglones: la segunda avisa con su familia (ibuprofeno → Advil)", Med("Penicilina\r\nIbuprofeno", "Advil 200 mg")),
            new("separadas por coma, punto y coma o diagonal; «Ninguna» no cuenta", Med("Ninguna, látex; Sulfas / polen", "Bactrim F", "Bactrim F", "Sulfametoxazol/Trimetoprima")),
            new("sulfato no es sulfa (revisión 20): alergia a «Sulfato ferroso» y se receta Bactrim: no avisa", Med("Sulfato ferroso", "Bactrim")),
            new("sulfato no es sulfa: alergia a las sulfas y se receta sulfato ferroso: no avisa", Med("Sulfas", "Sulfato ferroso 200 mg", "Sulfato ferroso 200 mg", "Sulfato ferroso")),
            new("sulfato no es sulfa: alergia a «sulfa» y se receta salbutamol sulfato: no avisa (ni directo)", Med("sulfa", "Salbutamol sulfato 100 mcg")),
            new("sulfato no es sulfa: sulfatos de magnesio", Med("Sulfonamidas", "Sulfatos de magnesio")),
            new("sulfa: las sulfonamidas con trimetoprim-sulfametoxazol", Med("Sulfonamidas", "Trimetoprim-sulfametoxazol 160/800 mg")),
            new("sulfa: alergia a «sulfa» y se receta sulfadiazina (directo)", Med("sulfa", "Sulfadiazina de plata crema")),
            new("sulfa: alergia a Bactrim y se receta sulfametoxazol", Med("Bactrim", "Sulfametoxazol 400 mg")),
            new("sulfa: alergia al sulfato ferroso y se receta sulfato ferroso (directo, como antes)", Med("Sulfato ferroso", "Sulfato ferroso 200 mg")),
            new("la alergia contiene al medicamento (4 letras o más)", Med("Amoxicilina con clavulanato", "amoxicilina")),
            new("sin coincidencia", Med("Mariscos, polen", "Loratadina 10 mg", "Loratadina 10 mg", "Loratadina")),
            new("alergias negadas", Med("Negadas", "Penicilina benzatínica")),
            new("negadas con punto y mayúsculas", Med("NIEGA ALERGIAS.", "Penicilina")),
            new("no interrogadas (vacío)", Med("", "Penicilina")),
            new("sin medicamento ni producto", Med("Penicilina", "  ")),
        };

        foreach (var (name, allergies, chronic) in new (string, string, string)[]
                 {
                     ("alergias y crónicos", "  Penicilina, látex ", "Diabetes tipo 2"),
                     ("negadas y ninguna crónica", "Negadas", "Ninguna relevante"),
                     ("sin interrogar", "   ", ""),
                     ("respuestas que niegan (con acentos y punto)", "Ninguna conocida.", "Niega"),
                     ("«No» niega; «No sé» no", "No", "No sé"),
                     ("crónicos que niegan con otras palabras", "Sin alergias", "Sin enfermedades crónicas"),
                 })
        {
            cases.Add(new RuleCase($"resumen · {name}", new JsonObject { ["check"] = "summary", ["allergies"] = allergies, ["chronicDiseases"] = chronic }));
        }

        CheckRuleVectors("allergy-check.json", "allergy-check", cases, input =>
            {
                var patient = new PatientRecord { Allergies = Str(input, "allergies"), ChronicDiseases = Str(input, "chronicDiseases") };
                if (S(input, "check") == "summary")
                {
                    return new JsonObject
                    {
                        ["hasAllergies"] = patient.HasAllergies,
                        ["allergiesNotAsked"] = patient.AllergiesNotAsked,
                        ["allergySummary"] = patient.AllergySummary,
                        ["allergyBadge"] = patient.AllergyBadgeText,
                        ["hasChronicDiseases"] = patient.HasChronicDiseases,
                        ["chronicBadge"] = patient.ChronicBadgeText
                    };
                }
                var product = input["product"] is { } p ? new Product { Name = Str(p, "name"), GenericName = S(p, "genericName") } : null;
                var catalog = (input["catalog"] as JsonArray)?.Select(c => new Product { Name = Str(c!, "name"), GenericName = S(c!, "genericName") }).ToList();
                var (conflict, matched, warning) = AllergyRules.CheckMedication(patient, product, S(input, "medicineName"), S(input, "activeIngredient"), catalog);
                return new JsonObject { ["conflict"] = conflict, ["matchedAllergy"] = matched, ["warning"] = warning };
            },
            "Services/Rules/AllergyRules.cs (antes InventoryService.CheckMedicationAllergy): el aviso de alergia al recetar y al cargar «Se surte aquí». 'medicineName' es lo escrito en la receta (si es nulo, el nombre del producto); 'product' es el producto elegido (su genérico entra a la comparación).",
            "medication: sin alergias (hasAllergies falso: vacías o que niegan) no hay aviso. med = (medicineName o el nombre del producto) + ' ' + genérico, sin espacios a los lados, y + ' ' + activeIngredient (revisión 16: el principio activo guardado en el renglón; sin espacios a los lados) si lo hay y medicineName normalizado no lo contiene normalizado; vacío = sin aviso. Las alergias se parten por , ; / \\n \\r, sin espacios a los lados, sin las vacías y sin las que niegan (DeniesAllergies). Para cada alergia en su orden, con todo normalizado (text-search.json): 1) aviso directo si medDirecto contiene la alergia, o si la alergia tiene 4 caracteres o más, medDirecto sin espacios a los lados no está vacío y la alergia lo contiene; medDirecto es med sin las palabras excluidas (abajo) de cada familia de la que es la alergia; 1b) (revisión 20) si no, los genéricos de la marca: por cada producto de 'catalog' en su orden, su genérico sin espacios a los lados si no está vacío, no es igual a la alergia (normalizados) y la alergia normalizada es igual a su nombre normalizado o a su marca normalizada (PrescriptionNaming.BrandOf(nombre, genérico), catalog-matching.json), sin repetir genéricos iguales normalizados; avisa si med contiene uno, o (revisión 20, revisión de la rama) contiene su base como palabras completas: la base es el genérico normalizado partido en palabras por lo que no sea letra, dígito ni '%', sin las que tienen dígitos o '%' ni las de AllergyRules.SaltWords (abajo), unidas por un espacio; solo cuenta si tiene 4 letras o más y no es igual al genérico normalizado; 'palabras completas' es que ' ' + base + ' ' esté en ' ' + las palabras de med unidas por un espacio + ' '; 2) si no, por cada familia en su orden (AllergyRules.DrugFamilies, abajo): la alergia (normalizada y, si tiene genéricos de marca, alergia + ' ' + los genéricos unidos con espacio, normalizado) es de la familia si, sin las palabras excluidas de la familia, contiene la llave o un miembro, y avisa si med también lo es. 'contiene' es comparación ordinal de subcadenas. Sin 'catalog' (o vacío) no hay paso 1b.",
            "Familias (llave: miembros), en este orden: " + string.Join("; ", AllergyRules.DrugFamilies.Select(f => $"{f.Key}: {string.Join(", ", f.Members)}")) + ". Los miembros con acento nunca coinciden porque se comparan con texto sin acentos. Palabras excluidas (revisión 20): " + string.Join("; ", AllergyRules.FamilyExcludedWords.Select(f => $"{f.Key}: {string.Join(", ", f.Value)}")) + ". Se quitan del texto normalizado como palabras completas (sin letra ni dígito Unicode antes ni después; se reemplazan por un espacio): el sulfato ferroso, de magnesio o el salbutamol sulfato no son sulfonamidas. Palabras de sal y concentración (SaltWords) que no forman la base de un genérico: " + string.Join(", ", AllergyRules.SaltWords) + ".",
            "Textos: directo \"El paciente registra alergia a '{alergia}'. El medicamento recetado contiene '{medicineName o nombre}'.\"; por la marca (revisión 20) \"El paciente registra alergia a '{alergia}' ({genérico}). El medicamento recetado contiene '{medicineName o nombre}'.\"; familia \"Riesgo de hipersensibilidad: El paciente registra alergia a '{alergia}' (grupo {LLAVE EN MAYÚSCULAS}). El medicamento recetado '{nombre}' pertenece o presenta reactividad cruzada con esta familia farmacológica.\" La alergia va como se escribió (sin espacios a los lados); la llave en mayúsculas invariantes (revisión 20: antes con la cultura actual, que en tr-TR daba 'AİNES').",
            "summary (PatientRecord): niegan las respuestas que normalizadas y sin punto final son: ninguna, ninguna conocida, ningunas, negadas, negada, niega, niega alergias, no, sin alergias, no conocidas, no conocida, negativas, nada. allergySummary: las alergias sin espacios a los lados, 'No interrogadas' (vacías) o 'Negadas'; allergyBadge: 'ALERGIAS: …', 'Alergias sin interrogar' o 'Alergias negadas'. Crónicos: niegan las mismas respuestas o lo que empieza con 'ningun', 'niega' o 'negad', o es 'sin enfermedades', 'sin enfermedades cronicas', 'sin antecedentes' o 'sin cronicos'; chronicBadge 'Crónicos: …' o vacío.",
            "Revisión 20 (corrige lo que reportó el paso 8.26): una alergia escrita con la marca avisa con el genérico si el catálogo la conoce (paso 1b; 'catalog' son los productos que la PC tiene cargados al recetar: Kotlin pasa los suyos), y 'sulfato'/'sulfatos' ya no hacen a un texto de la familia sulfa (el miembro 'sulfonamida' es nuevo).");
    }

    // ================================================================== contracts/golden/patient-identity.json

    [TestMethod]
    public void PatientIdentity_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        foreach (var (name, full) in new (string, string?)[]
                 {
                     ("nombre y apellido", "Juan Pérez"),
                     ("nombre y dos apellidos", "María Peña Núñez"),
                     ("más de tres palabras: las dos últimas son los apellidos", "María José García López"),
                     ("una partícula queda en el nombre", "Ana de la Cruz Ruiz"),
                     ("con coma: apellidos, nombre(s)", "García Morales, Juan Carlos"),
                     ("con coma y un apellido", "Peña, Ana"),
                     ("con coma y más de dos apellidos", "De la Cruz Ruiz, Ana"),
                     ("una palabra", "Ana"),
                     ("espacios de más", "  Juan    Pérez   López  "),
                     ("una coma sin apellidos no es el formato con coma", ",Juan"),
                     ("vacío", "   "),
                 })
        {
            cases.Add(new RuleCase($"partir el nombre · {name}", new JsonObject { ["check"] = "parse-name", ["fullName"] = full }));
        }

        foreach (var (name, first, last, maternal) in new (string, string, string, string?)[]
                 {
                     ("con materno", " María José ", "García", "López "),
                     ("sin materno", "Juan", "Pérez", "  "),
                 })
        {
            cases.Add(new RuleCase($"armar el nombre · {name}", new JsonObject { ["check"] = "compose-name", ["firstName"] = first, ["lastName"] = last, ["maternalLastName"] = maternal }));
        }

        foreach (var (name, text) in new (string, string?)[]
                 {
                     ("dd/MM/yyyy", "14/03/1992"),
                     ("d/M/yyyy", "1/2/2000"),
                     ("con guiones", "14-03-1992"),
                     ("con puntos", "14.03.1992"),
                     ("año-mes-día", "1992-03-14"),
                     ("ocho dígitos", "14031992"),
                     ("año de dos dígitos (92 → 1992)", "14/03/92"),
                     ("29 de febrero de un año bisiesto", "29/02/2024"),
                     ("29 de febrero de un año que no lo es", "29/02/2023"),
                     ("hoy vale", "15/10/2026"),
                     ("mañana no (futura)", "16/10/2026"),
                     ("130 años justos (el año, no la fecha)", "31/12/1896"),
                     ("más de 130 años", "31/12/1895"),
                     ("espacios a los lados", "  14/03/1992 "),
                     ("mes 13", "14/13/1992"),
                     ("texto", "catorce de marzo"),
                     ("vacío", ""),
                 })
        {
            cases.Add(new RuleCase($"fecha de nacimiento · {name}", new JsonObject { ["check"] = "birth-date", ["text"] = text, ["today"] = "2026-10-15" }));
        }

        foreach (var (name, email) in new (string, string?)[]
                 {
                     ("vacío vale (no es obligatorio)", ""),
                     ("normal", "ana.lopez@correo.com.mx"),
                     ("mayúsculas y acentos antes de la @", "JOSÉ@Correo.MX"),
                     ("sin dominio de nivel superior", "ana@correo"),
                     ("dominio de una letra", "ana@correo.c"),
                     ("con espacio", "ana lopez@correo.com"),
                     ("dos @", "ana@@correo.com"),
                     ("espacios a los lados", "  ana@correo.com  "),
                 })
        {
            cases.Add(new RuleCase($"correo · {name}", new JsonObject { ["check"] = "email", ["email"] = email }));
        }

        static JsonObject Form(string first = "Ana", string last = "López", string record = "EXP-2026-0001", string? birth = "14/03/1992",
            string? age = null, string? gender = "Femenino", string? allergies = "Negadas", string? email = "")
        {
            return new JsonObject
            {
                ["check"] = "validate", ["today"] = "2026-10-15", ["firstName"] = first, ["lastName"] = last, ["recordNumber"] = record,
                ["birthDateText"] = birth, ["ageText"] = age, ["gender"] = gender, ["allergies"] = allergies, ["email"] = email
            };
        }

        foreach (var (name, form) in new (string, JsonObject)[]
                 {
                     ("todo bien: la edad sale de la fecha", Form()),
                     ("sin fecha, con la edad capturada", Form(birth: "", age: "34")),
                     ("falta el nombre", Form(first: " ")),
                     ("falta el apellido paterno", Form(last: "")),
                     ("falta el folio", Form(record: "")),
                     ("fecha escrita que no se lee", Form(birth: "31/02/1992")),
                     ("fecha futura", Form(birth: "16/10/2026")),
                     ("sin fecha ni edad", Form(birth: null, age: "")),
                     ("edad de más de 130", Form(birth: "", age: "131")),
                     ("edad negativa", Form(birth: "", age: "-1")),
                     ("falta el sexo", Form(gender: "")),
                     ("alergias sin interrogar", Form(allergies: " ")),
                     ("correo inválido", Form(email: "ana@correo")),
                     ("el orden: el primer problema gana", Form(first: "", last: "", allergies: "")),
                 })
        {
            cases.Add(new RuleCase($"validar el alta · {name}", form));
        }

        foreach (var (name, full, gender, birth, storedAge) in new (string, string, string, string?, int)[]
                 {
                     ("adulta con fecha", "García Morales, Juana", "Femenino", "1992-03-14", 0),
                     ("bebé de días", "Luis Ángel Ruiz", "Masculino", "2026-10-03", 0),
                     ("un día", "Ñoño Pérez", "Masculino", "2026-10-14", 0),
                     ("un mes justo (el día del mes ya llegó)", "Eva", "Femenino", "2026-09-15", 0),
                     ("el día del mes no ha llegado: un mes menos", "Eva", "Femenino", "2026-08-16", 0),
                     ("menos de 2 años en meses", "Óscar Díaz", "Masculino", "2025-01-15", 0),
                     ("2 años 6 meses", "Sofía", "Femenino", "2024-04-15", 0),
                     ("5 años 1 mes (hasta los 6 los meses cuentan)", "Sofía", "Fem", "2021-09-15", 0),
                     ("6 años: solo los años", "Sofía", "Femenino", "2020-09-15", 0),
                     ("niña de 10: ya ve gineco-obstétricos", "Ana", "femenino", "2016-10-15", 0),
                     ("niña de 9", "Ana", "Femenino", "2016-10-16", 0),
                     ("17 años: pediátrico; 18 ya no", "Leo", "Masculino", "2008-10-16", 0),
                     ("sin fecha: la edad capturada", "  ", "No especificado", null, 34),
                     ("sin fecha y 1 año", "ana maría", "Femenino", null, 1),
                     ("nacido un 29 de febrero, antes de su día en un año no bisiesto", "Bruno", "Masculino", "2024-02-29", 0),
                 })
        {
            cases.Add(new RuleCase($"ficha · {name}", new JsonObject
            {
                ["check"] = "record", ["today"] = "2026-10-15", ["fullName"] = full, ["gender"] = gender, ["birthDate"] = birth, ["storedAge"] = storedAge
            }));
        }

        foreach (var (name, phone) in new (string, string?)[]
                 {
                     ("diez dígitos", "5512345678"),
                     ("con lada y signos: los últimos diez dígitos (revisión 20)", "+52 (55) 1234-5678"),
                     ("con lada del país sin signos", "+52 55 1234 5678"),
                     ("once dígitos con el 1 de antes", "1 55 1234 5678"),
                     ("ya con formato no cambia", "551-234-5678"),
                     ("dígitos que no son ASCII no cuentan", "55 ١٢٣ 1234 5678"),
                     ("sin la extensión (revisión 20)", "+52 55 9876 5432 ext 10"),
                     ("extensión con punto y mayúsculas", "55 9876 5432 Ext. 123"),
                     ("extensión con acento", "55 9876 5432 extensión 7"),
                     ("extensión con x", "55 9876 5432 x22"),
                     ("extensión con #", "55 9876 5432 #4"),
                     ("«ext» sin dígitos no es extensión", "55 9876 5432 ext"),
                     ("seis dígitos", "123456"),
                     ("tres", "911"),
                     ("vacío", " "),
                 })
        {
            cases.Add(new RuleCase($"teléfono · {name}", new JsonObject { ["check"] = "phone", ["phone"] = phone }));
        }

        CheckRuleVectors("patient-identity.json", "patient-identity", cases, input =>
            {
                switch (S(input, "check"))
                {
                    case "parse-name":
                        PatientFormRules.ParseFullName(S(input, "fullName"), out var first, out var last, out var maternal);
                        return new JsonObject { ["firstName"] = first, ["lastName"] = last, ["maternalLastName"] = maternal };
                    case "compose-name":
                        return PatientFormRules.ComposeFullName(Str(input, "firstName"), Str(input, "lastName"), S(input, "maternalLastName"));
                    case "birth-date":
                        return PatientFormRules.TryParseBirthDate(S(input, "text"), ParseLocal(Str(input, "today"))) is { } date ? LocalText(date) : null;
                    case "email":
                        return PatientFormRules.IsValidEmail(S(input, "email"));
                    case "validate":
                    {
                        var today = ParseLocal(Str(input, "today"));
                        var birthText = S(input, "birthDateText");
                        var problem = PatientFormRules.Validate(new PatientFormInput(S(input, "firstName"), S(input, "lastName"), null, S(input, "recordNumber"),
                            birthText, PatientFormRules.TryParseBirthDate(birthText, today), S(input, "ageText"), S(input, "gender"), S(input, "allergies"), S(input, "email")), today, out var age);
                        return new JsonObject { ["problem"] = problem, ["age"] = age };
                    }
                    case "record":
                    {
                        var today = ParseLocal(Str(input, "today"));
                        return OnDay(today, () =>
                        {
                            var p = new PatientRecord { FullName = Str(input, "fullName"), Gender = Str(input, "gender"), BirthDate = DateOrNull(input, "birthDate"), Age = Int(input, "storedAge") };
                            return (JsonNode)new JsonObject
                            {
                                ["initials"] = p.Initials, ["age"] = p.Age, ["ageText"] = p.AgeDisplay, ["months"] = p.BirthDate is { } b ? PatientRecord.TotalMonths(b, today) : null,
                                ["isFemale"] = p.IsFemale, ["isPediatric"] = p.IsPediatric, ["showsGynecoObstetric"] = p.ShowsGynecoObstetric
                            };
                        });
                    }
                    case "phone":
                        return PatientRecord.FormatPhoneNumber(S(input, "phone"));
                    default:
                        throw new ArgumentException(S(input, "check"));
                }
            },
            "La identidad del paciente (Services/Rules/PatientFormRules.cs y Models/PatientRecord.cs). 'today' es el día del consultorio (fechas yyyy-MM-dd).",
            "parse-name (ParseFullName): sin espacios a los lados; con coma y algo a cada lado: lo de después es el nombre y de lo de antes la primera palabra es el paterno y el resto el materno. Si no (también ',Juan', que sin las partes vacías tiene una sola): por palabras separadas por espacios, 1 = nombre, 2 = nombre y paterno, 3 = nombre, paterno y materno, más = las dos últimas son los apellidos y lo demás el nombre. compose-name: 'Nombre Paterno Materno' con cada parte sin espacios a los lados, sin el materno si está vacío.",
            "birth-date (TryParseBirthDate): sin espacios a los lados, con uno de estos formatos exactos de es-MX: d/M/yyyy, dd/MM/yyyy, d-M-yyyy, dd-MM-yyyy, d.M.yyyy, dd.MM.yyyy, yyyy-MM-dd, d/M/yy, dd/MM/yy, ddMMyyyy; null si no se lee, si es posterior a hoy o si su año es menor que el de hoy menos 130. Un año de dos dígitos sigue el calendario de .NET (hasta el 49 es 20xx, del 50 en adelante 19xx); los vectores solo usan años que dan lo mismo con cualquier corte entre 2029 y 2049.",
            "email: vacío vale; si no, sin espacios a los lados debe cumplir ^[^@\\s]+@[^@\\s]+\\.[a-zA-Z]{2,}$ sin distinguir mayúsculas.",
            "validate (al guardar el alta o la edición): el primer problema en este orden, o null: nombre vacío, paterno vacío, folio vacío, fecha escrita que no se lee (birth-date null con texto), sin fecha y una edad que no es un entero entre 0 y 130, sexo vacío, alergias vacías (\"Negadas\" sí vale), correo inválido. 'age' es la de la fecha (CalculateAge) o la capturada; 0 si el problema es la edad. Los textos son los de la PC.",
            "record: initials (con coma, la primera letra de lo de después y de lo de antes; si no, de las dos primeras palabras; en mayúscula invariante; '?' sin nombre), age (meses completos / 12 con la fecha; sin ella, la capturada), months (TotalMonths: (año·12 + mes) de hoy menos los del nacimiento, uno menos si el día de hoy es menor que el del nacimiento, nunca negativo), ageText ('N días' con menos de un mes, 'N meses' con menos de 24, 'N años M meses' antes de los 6 años, si no 'N años'; '1 día', '1 mes', '1 año' en singular), isFemale (el sexo empieza con 'Fem' sin distinguir mayúsculas), isPediatric (menos de 18), showsGynecoObstetric (mujer de 10 o más).",
            "phone (FormatPhoneNumber): sin la extensión (revisión 20, decisión del médico del 2026-10-22; la misma regla que duplicate-patients.json: DuplicatePatientRules.PhoneDigitsOf), solo los dígitos ASCII 0-9, los últimos 10 (revisión 20: la lada del país se queda fuera, como los compara duplicate-patients.json; antes se quedaban los primeros 10 y '+52 55 1234 5678' daba '525-512-3456'); hasta 3 tal cual, hasta 6 como 'ddd-ddd', más como 'ddd-ddd-dddd'. El alta lo aplica a cada tecla: escribir de corrido un número con lada termina en sus últimos 10.");
    }

    // ================================================================== contracts/golden/date-mask.json

    [TestMethod]
    public void DateMask_MatchesTheVectors()
    {
        var cases = new List<RuleCase>();
        void Add(string name, string op, string text, int start, int length, string? input = null)
        {
            var o = new JsonObject { ["op"] = op, ["text"] = text, ["selectionStart"] = start, ["selectionLength"] = length };
            if (input != null) o["input"] = input;
            cases.Add(new RuleCase(name, o));
        }

        Add("escribir · primer dígito", "insert", "", 0, 0, "0");
        Add("escribir · el día completo pone su diagonal", "insert", "0", 1, 0, "8");
        Add("escribir · el mes completo pone su diagonal", "insert", "08/0", 4, 0, "8");
        Add("escribir · el año", "insert", "08/08/200", 9, 0, "8");
        Add("escribir · un quinto dígito del año no entra", "insert", "08/08/2008", 10, 0, "1");
        Add("escribir · un día de un dígito y la diagonal", "insert", "8", 1, 0, "/");
        Add("escribir · la diagonal que ya está: solo se pasa", "insert", "08/", 2, 0, "/");
        Add("escribir · la diagonal tras la automática no entra", "insert", "08/", 3, 0, "/");
        Add("escribir · el guion y el punto cuentan como diagonal", "insert", "8", 1, 0, "-");
        Add("escribir · una tercera diagonal no entra", "insert", "8/8/2008", 8, 0, "/");
        Add("escribir · letras no entran", "insert", "08/", 3, 0, "a");
        Add("escribir · un dígito dentro de un día completo y cerrado no entra", "insert", "08/08/2008", 1, 0, "1");
        Add("escribir · el tercer dígito del día empieza el mes", "insert", "08", 2, 0, "1");
        Add("escribir · sobre una selección", "insert", "08/08/2008", 0, 2, "1");
        Add("escribir · cursor fuera del texto se ajusta", "insert", "08", 99, 0, "1");
        Add("pegar · ocho dígitos", "paste", "", 0, 0, "08082008");
        Add("pegar · con diagonales", "paste", "", 0, 0, "08/08/2008");
        Add("pegar · día y mes de un dígito", "paste", "", 0, 0, "8/8/2008");
        Add("pegar · año-mes-día se acomoda", "paste", "", 0, 0, "2008-08-08");
        Add("pegar · con espacios", "paste", "", 0, 0, " 14/03/1992 ");
        Add("pegar · no cabe", "paste", "", 0, 0, "080820081");
        Add("pegar · texto sin dígitos", "paste", "", 0, 0, "ayer");
        Add("retroceso · la diagonal del final se va con el dígito anterior", "backspace", "08/", 3, 0);
        Add("retroceso · a media fecha la diagonal se queda", "backspace", "08/08/2008", 6, 0);
        Add("retroceso · un dígito", "backspace", "08/08/2008", 10, 0);
        Add("retroceso · al inicio no hay nada", "backspace", "08/08", 0, 0);
        Add("retroceso · una selección", "backspace", "08/08/2008", 3, 3);
        Add("suprimir · antes de una diagonal borra el dígito que la sigue", "delete", "08/08/2008", 2, 0);
        Add("suprimir · un dígito y lo de después se acomoda", "delete", "08/08/2008", 0, 0);
        Add("suprimir · al final no hay nada", "delete", "08/08", 5, 0);
        Add("suprimir · la diagonal del final", "delete", "08/", 2, 0);

        CheckRuleVectors("date-mask.json", "date-mask", cases, input =>
            {
                var text = Str(input, "text");
                int start = Int(input, "selectionStart"), length = Int(input, "selectionLength");
                var edit = S(input, "op") switch
                {
                    "insert" => DateMaskText.Insert(text, start, length, S(input, "input")),
                    "paste" => DateMaskText.Paste(text, start, length, S(input, "input")),
                    "backspace" => DateMaskText.Backspace(text, start, length),
                    "delete" => DateMaskText.Delete(text, start, length),
                    _ => throw new ArgumentException(S(input, "op"))
                };
                return edit is { } e ? new JsonObject { ["text"] = e.Text, ["caret"] = e.Caret } : null;
            },
            "La fecha dd/mm/aaaa con las diagonales solas (Helpers/DateMaskText.cs, fase 7): qué queda en el cuadro y dónde queda el cursor al escribir, pegar, borrar con retroceso o suprimir. 'expected' null = la tecla no cambia nada (no entra). La selección se ajusta al texto antes de todo.",
            "insert: entran dígitos ASCII y '/' ('-' y '.' cuentan como '/'), lo demás se quita; si no queda nada, null. Una '/' sola sobre una que ya está la salta; sin día o mes antes no entra. Lo escrito reemplaza la selección y el texto se acomoda en día/mes/año (2, 2 y 4 dígitos): un dígito que no cabe en el día o el mes empieza la parte siguiente con su diagonal, salvo que esa parte ya esté cerrada por una diagonal posterior (null); una tercera diagonal o un quinto dígito del año dan null. Al completar el día o el mes al final aparece su diagonal y el cursor pasa sobre ella.",
            "paste: sin espacios a los lados; yyyy-M-d (con - / o .) se acomoda a d/M/yyyy; luego como insert.",
            "backspace: con selección la borra; al inicio null; justo después de una diagonal precedida de un dígito: si es la última del texto se borran las dos, si no solo el dígito (la diagonal se queda); si no, el carácter anterior. delete: con selección la borra; al final null; sobre una diagonal seguida de un dígito borra ese dígito; si no, el carácter. Al borrar, el texto se acomoda igual que al escribir, pero sin rechazar nada.");
    }
}
