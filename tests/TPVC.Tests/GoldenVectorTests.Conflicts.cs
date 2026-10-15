using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.23: los vectores de las citas empalmadas (appointment-overlap.json) y de los posibles expedientes duplicados
/// (duplicate-patients.json), que Kotlin repite igual, y el formato congelado de schema.json (la revisión 13 es la base de la
/// tablet: desde ahí solo cambios aditivos).
/// </summary>
public partial class GoldenVectorTests
{
    private static Guid Sid(int n) => new($"c0000000-0000-4000-8000-{n:D12}");

    // ================================================================== contracts/golden/appointment-overlap.json

    private static AppointmentRules.Appointment Cita(int id, string date, string? time, int minutes = 30, string? status = "Programada") =>
        new(Sid(id), DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture), time, minutes, status);

    private static JsonObject CitaNode(AppointmentRules.Appointment a) => new()
    {
        ["SyncId"] = SyncIds.Format(a.SyncId),
        ["Date"] = TursoSyncService.FormatDate(a.Date),
        ["Time"] = a.Time,
        ["DurationMinutes"] = a.DurationMinutes,
        ["Status"] = a.Status
    };

    /// <summary>Los casos de dos citas: nombre, las dos y si se empalman (la prueba de la regla también los usa).</summary>
    internal static readonly (string Name, AppointmentRules.Appointment A, AppointmentRules.Appointment B, bool Expected)[] OverlapPairs =
    {
        ("misma hora el mismo día", Cita(1, "2026-10-15", "10:30"), Cita(2, "2026-10-15", "10:30"), true),
        ("se tocan: una termina a las 10:30 y la otra empieza a las 10:30", Cita(1, "2026-10-15", "10:00"), Cita(2, "2026-10-15", "10:30"), false),
        ("se cruzan 15 minutos", Cita(1, "2026-10-15", "10:00"), Cita(2, "2026-10-15", "10:15"), true),
        ("una dentro de otra (60 y 15 minutos)", Cita(1, "2026-10-15", "10:00", 60), Cita(2, "2026-10-15", "10:15", 15), true),
        ("el mismo horario en otro día", Cita(1, "2026-10-15", "10:30"), Cita(2, "2026-10-16", "10:30"), false),
        ("una cancelada no se empalma", Cita(1, "2026-10-15", "10:30"), Cita(2, "2026-10-15", "10:30", status: "Cancelada"), false),
        ("solo 'Cancelada' tal cual cuenta como cancelada", Cita(1, "2026-10-15", "10:30"), Cita(2, "2026-10-15", "10:30", status: "cancelada"), true),
        ("una atendida sí cuenta (ocupó el horario)", Cita(1, "2026-10-15", "10:30", status: "Atendida"), Cita(2, "2026-10-15", "10:45", status: "Confirmada"), true),
        ("sin duración (0) dura 30 minutos: se cruza a las 10:20", Cita(1, "2026-10-15", "10:00", 0), Cita(2, "2026-10-15", "10:20"), true),
        ("sin duración (0) dura 30 minutos: toca a las 10:30", Cita(1, "2026-10-15", "10:00", 0), Cita(2, "2026-10-15", "10:30"), false),
        ("duración negativa también dura 30 minutos", Cita(1, "2026-10-15", "10:00", -15), Cita(2, "2026-10-15", "10:25"), true),
        ("pasa de la medianoche: se empalma con otra del mismo día", Cita(1, "2026-10-15", "23:45"), Cita(2, "2026-10-15", "23:50"), true),
        ("pasa de la medianoche: no se compara con el día siguiente", Cita(1, "2026-10-15", "23:45"), Cita(2, "2026-10-16", "00:00"), false),
        ("la misma cita (mismo SyncId) no se empalma consigo misma", Cita(1, "2026-10-15", "10:30"), Cita(1, "2026-10-15", "10:30"), false),
        ("12 y 24 horas: '10:30 AM' y '10:30'", Cita(1, "2026-10-15", "10:30 AM"), Cita(2, "2026-10-15", "10:30"), true),
        ("12 y 24 horas: '2:00 p. m.' y '14:15'", Cita(1, "2026-10-15", "2:00 p. m."), Cita(2, "2026-10-15", "14:15"), true),
        ("12 y 24 horas: '12:30 am' es 00:30", Cita(1, "2026-10-15", "12:30 am"), Cita(2, "2026-10-15", "00:45"), true),
        ("con segundos: '09:59:30' cruza a las 10:29", Cita(1, "2026-10-15", "09:59:30"), Cita(2, "2026-10-15", "10:29"), true),
        ("una hora que no se lee cuenta como las 10:00 ('mediodía')", Cita(1, "2026-10-15", "mediodía"), Cita(2, "2026-10-15", "10:15"), true),
        ("'3 PM' no se lee (sin minutos): cuenta como las 10:00, no las 15:00", Cita(1, "2026-10-15", "3 PM"), Cita(2, "2026-10-15", "15:00"), false),
        ("'7:5' no se lee (minutos de un dígito): cuenta como las 10:00", Cita(1, "2026-10-15", "7:5"), Cita(2, "2026-10-15", "10:00"), true),
        ("sin hora (vacía o nula) cuenta como las 10:00", Cita(1, "2026-10-15", null), Cita(2, "2026-10-15", ""), true),
    };

    [TestMethod]
    public void AppointmentOverlap_MatchesTheVectors()
    {
        var cases = new JsonArray();
        foreach (var (name, a, b, _) in OverlapPairs)
        {
            cases.Add(Case($"empalme · {name}", new JsonObject { ["check"] = "overlaps", ["a"] = CitaNode(a), ["b"] = CitaNode(b) },
                AppointmentRules.Overlaps(a, b)));
        }

        foreach (var (time, minutes) in new (string?, int)[] { ("10:30", 45), ("9:15 p. m.", 0), ("hoy", -5), (null, 30), ("23:45", 30), ("09:59:30", 30) })
        {
            var a = Cita(1, "2026-10-15", time, minutes);
            cases.Add(Case($"horario · '{time}' por {minutes} min", new JsonObject { ["check"] = "interval", ["a"] = CitaNode(a) }, new JsonObject
            {
                ["startSeconds"] = (long)AppointmentRules.Start(a.Time).TotalSeconds,
                ["minutes"] = AppointmentRules.Minutes(a.DurationMinutes),
                ["endSeconds"] = (long)AppointmentRules.End(a).TotalSeconds
            }));
        }

        // Después de bajar: los pares de una lista en que está alguna de las citas que llegaron
        var day = new[]
        {
            Cita(1, "2026-10-15", "10:00"), Cita(2, "2026-10-15", "10:15"), Cita(3, "2026-10-15", "10:20", 10),
            Cita(4, "2026-10-15", "12:00"), Cita(5, "2026-10-15", "12:00", status: "Cancelada"), Cita(6, "2026-10-16", "10:00")
        };
        foreach (var (name, involving) in new (string, int[]?)[]
                 {
                     ("todos los pares", null),
                     ("solo los de la cita que llegó (la 3)", new[] { 3 }),
                     ("la que llegó no se empalma con nada (la 6, otro día; la 5, cancelada)", new[] { 5, 6 }),
                 })
        {
            var pairs = AppointmentRules.Pairs(day, involving?.Select(Sid).ToHashSet());
            cases.Add(Case($"pares · {name}", new JsonObject
            {
                ["check"] = "pairs",
                ["appointments"] = new JsonArray(day.Select(a => (JsonNode)CitaNode(a)).ToArray()),
                ["involving"] = involving == null ? null : new JsonArray(involving.Select(i => (JsonNode)JsonValue.Create(SyncIds.Format(Sid(i)))).ToArray())
            }, new JsonArray(pairs.Select(p => (JsonNode)new JsonArray(SyncIds.Format(p.First.SyncId), SyncIds.Format(p.Second.SyncId))).ToArray())));
        }

        CheckOrWrite("contracts/golden/appointment-overlap.json", VectorFile("appointment-overlap", cases,
            "Citas empalmadas (Services/Rules/AppointmentRules, paso 8.23, H2). Dos citas (PatientAppointments) se empalman si: no son la misma (SyncId; uno vacío es una cita nueva sin guardar), ninguna tiene Status 'Cancelada' (exacto; 'Atendida' sí cuenta), tienen la misma Date (solo la fecha, dateOnly) y sus horarios [inicio, fin) se cruzan: inicioA < finB y inicioB < finA. Tocarse no es empalme.",
            "interval: en segundos desde la medianoche de su Date. El inicio es la hora de Time leída con la regla de patient-summary.json ('time': H:mm o HH:mm, :ss opcional, AM/PM opcional; lo que no se lee, las 10:00); 'minutes' es DurationMinutes, o 30 si es 0 o negativa (la agenda la dibuja igual); el fin, inicio + minutes. El fin puede pasar de 86400 (la medianoche) y sigue contando solo en su mismo día: no se compara con las citas del día siguiente.",
            "Diferencia a propósito con DateTime.TryParse (la agenda de la PC lo usaba hasta v1.39.0): '3 PM' y '7:5' no se leen y cuentan como las 10:00. TPVC siempre escribe 'HH:mm'.",
            "pairs: los pares empalmados de la lista, cada par una vez [primera, segunda] por hora y luego SyncId en minúsculas (orden ordinal), los días en orden. Con 'involving' (los SyncId de las citas que acaban de bajar) solo los pares en que está alguna; null = todos.",
            "Antes de agendar o cambiar una cita: sincronización rápida (3 s como máximo, regla «Sincronización rápida» de schema.json) y luego la pregunta 'Ya hay una cita a las 10:30 con … ¿Agendar de todos modos?'. Después de bajar: 'pairs' con las citas que llegaron de hoy en adelante, aviso y la marca 'Empalme' en la agenda. Nunca se cancela una cita sola."));
    }

    // ================================================================== contracts/golden/duplicate-patients.json

    private static DuplicatePatientRules.Patient Pac(int id, string? name, string? birth = null, string? phone = null, bool active = true, int? merged = null) =>
        new(Sid(100 + id), name, birth == null ? null : DateTime.ParseExact(birth, "yyyy-MM-dd", CultureInfo.InvariantCulture), phone, active,
            merged is { } m ? Sid(100 + m) : null);

    private static JsonObject PacNode(DuplicatePatientRules.Patient p) => new()
    {
        ["SyncId"] = SyncIds.Format(p.SyncId),
        ["FullName"] = p.FullName,
        ["BirthDate"] = p.BirthDate is { } b ? TursoSyncService.FormatDate(b) : null,
        ["Phone"] = p.Phone,
        ["IsActive"] = p.IsActive,
        ["MergedIntoSyncId"] = p.MergedIntoSyncId is { } m ? SyncIds.Format(m) : null
    };

    /// <summary>Los casos de dos expedientes: nombre, los dos y por qué se parecen (la prueba de la regla también los usa).</summary>
    internal static readonly (string Name, DuplicatePatientRules.Patient A, DuplicatePatientRules.Patient B, DuplicatePatientRules.Match Expected)[] DuplicatePairsCases =
    {
        ("acentos y mayúsculas, misma fecha de nacimiento", Pac(1, "José Pérez Núñez", "1990-02-01"), Pac(2, "JOSE PEREZ NUNEZ", "1990-02-01"), DuplicatePatientRules.Match.BirthDate),
        ("espacios de más, tabulador y espacio duro, mismo teléfono", Pac(1, "  María   del\tCarmen López ", phone: "5512345678"), Pac(2, "maria del carmen lopez", phone: "5512345678"), DuplicatePatientRules.Match.Phone),
        ("diéresis: Güemes y Guemes", Pac(1, "Ana Güemes", "1985-07-20"), Pac(2, "Ana Guemes", "1985-07-20"), DuplicatePatientRules.Match.BirthDate),
        ("teléfono con lada y formato: '+52 (55) 1234-5678' y '55 1234 5678'", Pac(1, "Luis Rojas", phone: "+52 (55) 1234-5678"), Pac(2, "Luis Rojas", phone: "55 1234 5678"), DuplicatePatientRules.Match.Phone),
        ("teléfono con 044: los últimos 10 dígitos", Pac(1, "Luis Rojas", phone: "044 55 1234 5678"), Pac(2, "Luis Rojas", phone: "5512345678"), DuplicatePatientRules.Match.Phone),
        ("teléfono de 8 dígitos: no se compara", Pac(1, "Luis Rojas", phone: "1234-5678"), Pac(2, "Luis Rojas", phone: "1234 5678"), DuplicatePatientRules.Match.None),
        ("mismo nombre, otra fecha de nacimiento y sin teléfono", Pac(1, "Juan Pérez", "1990-02-01"), Pac(2, "Juan Pérez", "1991-02-01"), DuplicatePatientRules.Match.None),
        ("mismo nombre, otra fecha de nacimiento pero el mismo teléfono", Pac(1, "Juan Pérez", "1990-02-01", "5598765432"), Pac(2, "Juan Pérez", "1991-02-01", "55-9876-5432"), DuplicatePatientRules.Match.Phone),
        ("misma fecha y mismo teléfono", Pac(1, "Juan Pérez", "1990-02-01", "5598765432"), Pac(2, "juan perez", "1990-02-01", "+525598765432"), DuplicatePatientRules.Match.BirthDateAndPhone),
        ("uno sin fecha de nacimiento: solo cuenta el teléfono (aquí, otro)", Pac(1, "Juan Pérez", null, "5598765432"), Pac(2, "Juan Pérez", "1990-02-01", "5511112222"), DuplicatePatientRules.Match.None),
        ("otro nombre con la misma fecha y teléfono", Pac(1, "Juan Pérez", "1990-02-01", "5598765432"), Pac(2, "Juana Pérez", "1990-02-01", "5598765432"), DuplicatePatientRules.Match.None),
        ("un guion cuenta: 'María-José' no es 'María José'", Pac(1, "María-José Ruiz", "2001-03-03"), Pac(2, "María José Ruiz", "2001-03-03"), DuplicatePatientRules.Match.None),
        ("un punto cuenta: 'Ma. Elena' no es 'Ma Elena'", Pac(1, "Ma. Elena Soto", "1970-01-01"), Pac(2, "Ma Elena Soto", "1970-01-01"), DuplicatePatientRules.Match.None),
        ("uno dado de baja (IsActive falso) no cuenta", Pac(1, "Juan Pérez", "1990-02-01"), Pac(2, "Juan Pérez", "1990-02-01", active: false), DuplicatePatientRules.Match.None),
        ("uno unido a otro (MergedIntoSyncId) no cuenta", Pac(1, "Juan Pérez", "1990-02-01"), Pac(2, "Juan Pérez", "1990-02-01", merged: 1), DuplicatePatientRules.Match.None),
        ("el mismo expediente (mismo SyncId) no es duplicado de sí mismo", Pac(1, "Juan Pérez", "1990-02-01"), Pac(1, "Juan Pérez", "1990-02-01"), DuplicatePatientRules.Match.None),
        ("sin nombre no cuenta", Pac(1, "  ", "1990-02-01"), Pac(2, "", "1990-02-01"), DuplicatePatientRules.Match.None),
    };

    [TestMethod]
    public void DuplicatePatients_MatchTheVectors()
    {
        var cases = new JsonArray();
        foreach (var name in new[] { "José Pérez Núñez", "  María   del\tCarmen López ", "Ana Güemes", "ÑOÑO", "María-José", "" })
        {
            cases.Add(Case($"nombre · '{name}'", new JsonObject { ["check"] = "name", ["name"] = name }, DuplicatePatientRules.NormalizeName(name)));
        }
        foreach (var phone in new[] { "+52 (55) 1234-5678", "044 55 1234 5678", "55 1234 5678", "1234-5678", "", "５５１２３４５６７８" })
        {
            cases.Add(Case($"teléfono · '{phone}'", new JsonObject { ["check"] = "phone", ["phone"] = phone }, DuplicatePatientRules.PhoneKey(phone)));
        }
        foreach (var (name, a, b, _) in DuplicatePairsCases)
        {
            cases.Add(Case($"par · {name}", new JsonObject { ["check"] = "compare", ["a"] = PacNode(a), ["b"] = PacNode(b) },
                DuplicatePatientRules.Compare(a, b).ToString()));
        }

        // Después de bajar: los pares en que está alguno de los que llegaron
        var patients = new[]
        {
            Pac(1, "Juan Pérez", "1990-02-01"), Pac(2, "JUAN PEREZ", "1990-02-01"), Pac(3, "Juan Perez", null, "5598765432"),
            Pac(4, "Juan Pérez", "1975-05-05", "55 9876 5432"), Pac(5, "Ana López", "1980-01-01"), Pac(6, "Ana Lopez", "1980-01-01", active: false)
        };
        foreach (var (name, involving) in new (string, int[])[]
                 {
                     ("llegó el 2", new[] { 2 }),
                     ("llegó el 4 (mismo teléfono que el 3)", new[] { 4 }),
                     ("llegaron el 5 y el 6 (el 6 está dado de baja)", new[] { 5, 6 }),
                 })
        {
            var pairs = DuplicatePatientRules.Pairs(patients, involving.Select(i => Sid(100 + i)).ToHashSet());
            cases.Add(Case($"pares · {name}", new JsonObject
            {
                ["check"] = "pairs",
                ["patients"] = new JsonArray(patients.Select(p => (JsonNode)PacNode(p)).ToArray()),
                ["involving"] = new JsonArray(involving.Select(i => (JsonNode)JsonValue.Create(SyncIds.Format(Sid(100 + i)))).ToArray())
            }, new JsonArray(pairs.Select(p => (JsonNode)new JsonArray(SyncIds.Format(p.First.SyncId), SyncIds.Format(p.Second.SyncId), p.Match.ToString())).ToArray())));
        }

        CheckOrWrite("contracts/golden/duplicate-patients.json", VectorFile("duplicate-patients", cases,
            "Posibles expedientes duplicados (Services/Rules/DuplicatePatientRules, paso 8.23, H3). No hay CURP: dos expedientes pueden ser el mismo paciente si su nombre normalizado es igual y además tienen la misma BirthDate (solo la fecha) o los mismos últimos 10 dígitos de Phone. 'compare' da el motivo: None, BirthDate, Phone o BirthDateAndPhone.",
            "name: la normalización de la búsqueda (Services/TextSearch.Normalize: quitar espacios de los extremos, NFD, quitar las marcas sin espacio (categoría Mn: acentos, diéresis, la tilde de la ñ), minúsculas invariantes, NFC) y luego los espacios seguidos como uno (separa todo lo que char.IsWhiteSpace de .NET o Char.isWhitespace() de Kotlin toman por espacio, también el tabulador y el espacio duro U+00A0). Nada más: un guion o un punto siguen contando.",
            "phone: solo los dígitos ASCII 0-9 (los de ancho completo no cuentan), los últimos 10; con menos de 10, null (no se compara).",
            "Solo cuentan los expedientes con IsActive, sin MergedIntoSyncId y con nombre; nunca el mismo SyncId (uno vacío es un expediente nuevo sin guardar).",
            "pairs: los pares de posibles duplicados en que está alguno de 'involving' (los SyncId de los expedientes que acaban de bajar), cada par una vez [SyncId menor, SyncId mayor, motivo], ordenados por SyncId en minúsculas (orden ordinal).",
            "Al crear un expediente: aviso con los parecidos y se puede crear de todos modos. Después de bajar: 'pairs' con los que llegaron, en un aviso (en la PC, el banner de Pacientes). Nunca se une nada solo (la herramienta de unir es el paso opcional 8.A, que escribirá MergedIntoSyncId)."));
    }

    // ================================================================== formato congelado (schema.json, revisión 13)

    /// <summary>schema.json de hoy con un cambio hecho a mano.</summary>
    private static string Mutated(Action<JsonObject> change)
    {
        var schema = JsonNode.Parse(SyncContract.BuildSchemaJson(Model()))!.AsObject();
        change(schema);
        return schema.ToJsonString();
    }

    private static JsonObject TableOf(JsonObject schema, string table) =>
        schema["tables"]!.AsArray().Select(t => t!.AsObject()).Single(t => t["table"]!.GetValue<string>() == table);

    private static JsonObject ColumnOf(JsonObject schema, string table, string column) =>
        TableOf(schema, table)["columns"]!.AsArray().Select(c => c!.AsObject()).Single(c => c["name"]!.GetValue<string>() == column);

    [TestMethod]
    public void TheFrozenSchema_RejectsChangesThatAreNotAdditive()
    {
        var current = SyncContract.BuildSchemaJson(Model());
        Assert.IsEmpty(NonAdditiveChanges(current, current), "El esquema de hoy contra sí mismo");

        foreach (var (name, change) in new (string, Action<JsonObject>)[]
                 {
                     ("quitar una tabla", s => s["tables"]!.AsArray().Remove(TableOf(s, "LabResults"))),
                     ("quitar una columna", s => TableOf(s, "Patients")["columns"]!.AsArray().Remove(ColumnOf(s, "Patients", "Phone"))),
                     ("cambiar el tipo remoto", s => ColumnOf(s, "Products", "Price")["remoteType"] = "REAL"),
                     ("cambiar el tipo de C#", s => ColumnOf(s, "Products", "Price")["clrType"] = "double"),
                     ("una columna con nulo pasa a obligatoria", s => ColumnOf(s, "Patients", "OriginalFolio")["nullable"] = false),
                     ("cambiar el timeKind", s => ColumnOf(s, "Sales", "Date")["timeKind"] = "utc"),
                     ("cambiar un alias", s => ColumnOf(s, "Sales", "PatientName").Remove("aliasOf")),
                     ("cambiar el número de un valor del enum", s => ColumnOf(s, "InventoryMovements", "MovementType")["enumValues"]!["SalidaVenta"] = 99),
                     ("quitar un valor del enum", s => ColumnOf(s, "InventoryMovements", "MovementType")["enumValues"]!.AsObject().Remove("SaldoInicial")),
                     ("una derivada deja de serlo", s => ColumnOf(s, "Patients", "LastVisit")["derived"] = false),
                     ("quitar una referencia", s => TableOf(s, "SaleItems")["references"]!.AsArray().Clear()),
                     ("una referencia cambia de principal", s => TableOf(s, "Appointments")["references"]![0]!["principal"] = "Product"),
                     ("una referencia opcional pasa a obligatoria", s => TableOf(s, "InventoryMovements")["references"]!.AsArray()
                         .Single(r => r!["column"]!.GetValue<string>() == "SaleItemSyncId")!["required"] = true),
                     ("un índice único nuevo en una tabla que ya existe", s => TableOf(s, "Patients")["unique"]!.AsArray().Add(new JsonArray("RecordNumber"))),
                 })
        {
            // "Antes", el archivo de hoy; "generado", el cambiado
            Assert.IsNotEmpty(NonAdditiveChanges(current, Mutated(change)), $"No se detectó: {name}");
        }
    }

    [TestMethod]
    public void TheFrozenSchema_AcceptsAdditiveChanges_ThatStillNeedANewRevision()
    {
        var current = SyncContract.BuildSchemaJson(Model());
        foreach (var (name, change) in new (string, Action<JsonObject>)[]
                 {
                     ("una tabla nueva", s => s["tables"]!.AsArray().Add(new JsonObject { ["table"] = "Nueva", ["entityType"] = "Nueva", ["columns"] = new JsonArray(), ["references"] = new JsonArray(), ["unique"] = new JsonArray() })),
                     ("una columna nueva (8.20: InventoryMovements.CountedQuantity)", s => TableOf(s, "InventoryMovements")["columns"]!.AsArray().Add(new JsonObject
                     {
                         ["name"] = "CountedQuantity", ["role"] = "data", ["remoteType"] = "INTEGER", ["nullable"] = true, ["clrType"] = "int", ["derived"] = false
                     })),
                     ("un valor nuevo del enum (8.20: Conteo = 9)", s => ColumnOf(s, "InventoryMovements", "MovementType")["enumValues"]!["Conteo"] = 9),
                     ("una columna pasa a derivada (8.20: las existencias)", s => ColumnOf(s, "Products", "CurrentStock")["derived"] = true),
                     ("otra nota", s => ColumnOf(s, "Products", "CurrentStock")["note"] = "Otra nota"),
                 })
        {
            var mutated = Mutated(change);
            Assert.IsEmpty(NonAdditiveChanges(current, mutated), name);

            // Con el mismo número de revisión no pasa: todo cambio de las tablas (también una nota) sube la revisión
            var sameRevision = Assert.ThrowsExactly<AssertFailedException>(() => RequireAdditiveChangeWithNewRevision(current, mutated));
            StringAssert.Contains(sameRevision.Message, "suba SyncContract.Revision");
            var bumped = JsonNode.Parse(mutated)!.AsObject();
            bumped["contract"] = SyncContract.Revision + 1;
            RequireAdditiveChangeWithNewRevision(current, bumped.ToJsonString());
        }

        // Un índice local que deja de ser único (como RecordNumber y Folio en el paso 8.16) sí es aditivo
        var withUnique = Mutated(s => TableOf(s, "Patients")["unique"]!.AsArray().Add(new JsonArray("RecordNumber")));
        Assert.IsEmpty(NonAdditiveChanges(withUnique, current));

        var notAdditive = Mutated(s => ColumnOf(s, "Products", "Price")["remoteType"] = "REAL");
        var frozen = Assert.ThrowsExactly<AssertFailedException>(() => RequireAdditiveChangeWithNewRevision(current, notAdditive));
        StringAssert.Contains(frozen.Message, $"congelado desde la revisión {SyncContract.FrozenRevision}");
        var stored = JsonNode.Parse(System.IO.File.ReadAllText(RepoPath("contracts/sync/schema.json")))!["contract"]!.GetValue<int>();
        Assert.IsGreaterThanOrEqualTo(SyncContract.FrozenRevision, stored, "schema.json ya es la base congelada de la tablet (o una revisión posterior)");
    }
}
