using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using TPVC.Models;
using TPVC.Services;
using TPVC.Services.Rules;

namespace TPVC.Tests;

/// <summary>
/// Paso 8.25: vectores de la consulta entre equipos que faltaban para la tablet (K8). handoff-payload.json: la copia de
/// trabajo (el borrador de la consulta tal como lo guarda la PC) con un reloj fijo y las fechas tal cual, lo que viaja después
/// de PrepareForSending, la traducción al recibir (MapReceivedItems) y la búsqueda de cada cargo (FindCatalogRef).
/// linked-device.json: a quién se le puede enviar una consulta y cómo se ve cada equipo vinculado (MergeDevices, ToDevice
/// y HandoffTargets). handoff-accept.json (paso 8.22) está en GoldenVectorTests.PatientMerge.cs.
/// </summary>
public partial class GoldenVectorTests
{
    // ================================================================== la copia de trabajo, con reloj fijo

    /// <summary>Datos fijos de la consulta entre equipos (también los usan las grabaciones de contracts/hrana).</summary>
    internal static class HandoffFixture
    {
        public static readonly DateTime StartedAt = new(2026, 1, 15, 10, 30, 0);
        public static readonly DateTime SavedAt = new(2026, 1, 15, 10, 52, 41);
        public static readonly DateTime PatientUpdatedAtUtc = new(2026, 1, 15, 16, 51, 2, 123, DateTimeKind.Unspecified);
        public static readonly DateTime NoteUpdatedAtUtc = new(2026, 1, 15, 16, 50, 7, 456);

        public static Guid Id(int n) => new($"10000000-0000-4000-8000-{n:D12}");

        public static readonly Guid PatientSyncId = Id(3);
        public static readonly Guid NoteSyncId = Id(960);
        public static readonly Guid AppointmentSyncId = Id(961);

        /// <summary>El catálogo del equipo que envía (sus Id locales) y el del que recibe (otros Id, uno inactivo, uno que no tiene).</summary>
        public static List<CatalogRef> SenderRefs() => new()
        {
            new(CatalogRefKind.Product, 12, Id(970), "MED-001", "Paracetamol 500 mg tabletas", true),
            new(CatalogRefKind.Product, 15, Id(971), "CUR-001", "Gasa Estéril 10x10 cm (Paquete con 10 piezas)", true),
            new(CatalogRefKind.Product, 18, Id(972), "ANT-001", "Alcohol Etílico Desnaturalizado 70%", true),
            new(CatalogRefKind.Service, 4, Id(980), "SERV-004", "Monitoreo de Glucemia Capilar", true),
            new(CatalogRefKind.Service, 1, Id(981), "SERV-001", "Consulta Médica General", true)
        };

        public static List<CatalogRef> ReceiverRefs() => new()
        {
            new(CatalogRefKind.Product, 5001, Id(970), "MED-001", "Paracetamol 500 mg tabletas", true),
            // El mismo producto con otro SyncId (dado de alta a mano en este equipo): se encuentra por código y nombre
            new(CatalogRefKind.Product, 5002, Id(990), "CUR-001", "Gasa estéril 10x10 cm (paquete con 10 piezas)", true),
            // Inactivo aquí: ni por SyncId ni por nombre
            new(CatalogRefKind.Product, 5003, Id(972), "ANT-001", "Alcohol Etílico Desnaturalizado 70%", false),
            new(CatalogRefKind.Service, 7004, Id(980), "SERV-004", "Monitoreo de Glucemia Capilar", true),
            new(CatalogRefKind.Service, 7001, Id(981), "SERV-001", "Consulta Médica General", true)
        };

        /// <summary>El borrador de la consulta como lo guarda la PC (Id locales, sin identificadores de sincronización).</summary>
        public static ConsultationDraft WorkingCopy() => new()
        {
            SavedAt = SavedAt,
            PatientId = 7,
            PatientName = "Ana Pérez Núñez",
            Folio = "CNS-20260115-103000",
            StartedAt = StartedAt,
            ChiefComplaintLabel = "Consulta Médica General",
            ChiefComplaint = "Control mensual de glucosa",
            Diagnosis = "E11.9 Diabetes mellitus tipo 2 sin complicaciones",
            TreatmentPlan = "Continuar metformina 850 mg cada 12 horas",
            TA = "135/85",
            Glucose = "142",
            Section = 4,
            SavedToRecord = true,
            AttendingAppointmentId = 31,
            Items = new List<ConsultationProductItem>
            {
                Item(12, "MED-001", "Paracetamol 500 mg tabletas", 45m, 1),
                Item(15, "CUR-001", "Gasa Estéril 10x10 cm (Paquete con 10 piezas)", 35m, 2),
                Item(18, "ANT-001", "Alcohol Etílico Desnaturalizado 70%", 50m, 1),
                Item(-4, "SERV-004", "Monitoreo de Glucemia Capilar", 80m, 1),
                Item(HandoffPayloadCodec.FreeChargeId("Curación menor"), "", "Curación menor", 150m, 1),
                // Un producto que ya no está en el catálogo de este equipo: viaja como cargo libre
                Item(99, "VEN-009", "Venda elástica 10 cm", 25m, 1)
            },
            Prescriptions = new List<PatientPrescriptionItem>
            {
                new() { RowId = Id(962), MedicineName = "Metformina 850 mg", Dosage = "1 tableta cada 12 horas por 30 días", RecommendedQuantity = 2 }
            }
        };

        private static ConsultationProductItem Item(int productId, string code, string name, decimal price, int quantity) => new()
        {
            ProductId = productId, Code = code, Name = name, UnitPrice = price, Quantity = quantity, AddedAt = StartedAt.AddMinutes(12)
        };

        public const string PatientFieldStampsJson =
            "{\"ActiveSuppliesJson\":\"2026-01-15 16:40:00.0000000\",\"Allergies\":\"2026-01-10 09:00:00.0000000\",\"UpdatedAtUtc\":\"2026-01-15 16:51:02.1230000\"}";

        /// <summary>La copia que viaja: la de trabajo después de PrepareForSending con los datos de arriba.</summary>
        public static ConsultationDraft Payload()
        {
            var draft = WorkingCopy();
            HandoffPayloadCodec.PrepareForSending(draft, SenderRefs(), PatientSyncId, PatientUpdatedAtUtc, NoteSyncId, AppointmentSyncId,
                feeServiceId: 1, savedAt: SavedAt, PatientFieldStampsJson, NoteUpdatedAtUtc);
            return draft;
        }
    }

    private static JsonNode DraftJson(ConsultationDraft draft) => JsonNode.Parse(ConsultationDraftStore.Serialize(draft))!;

    private static JsonArray RefsJson(IEnumerable<CatalogRef> refs) => new(refs.Select(r => (JsonNode?)new JsonObject
    {
        ["kind"] = r.Kind.ToString(),
        ["id"] = r.Id,
        ["syncId"] = SyncIds.Format(r.SyncId),
        ["code"] = r.Code,
        ["name"] = r.Name,
        ["isActive"] = r.IsActive
    }).ToArray());

    private static List<CatalogRef> ParseRefs(JsonNode node) => node.AsArray().Select(r => new CatalogRef(
        Enum.Parse<CatalogRefKind>(r!["kind"]!.GetValue<string>()), r["id"]!.GetValue<int>(), Guid.Parse(r["syncId"]!.GetValue<string>()),
        r["code"]!.GetValue<string>(), r["name"]!.GetValue<string>(), r["isActive"]!.GetValue<bool>())).ToList();

    [TestMethod]
    public void HandoffPayload_MatchesTheVector()
    {
        var cases = new JsonArray();

        // Enviar: la copia de trabajo y lo que viaja
        void Prepare(string name, ConsultationDraft working, Guid patient, DateTime? patientUpdated, string? stamps, Guid? note, DateTime? noteUpdated,
            Guid? appointment, int? fee)
        {
            var input = new JsonObject
            {
                ["workingCopy"] = DraftJson(working),
                ["senderRefs"] = RefsJson(HandoffFixture.SenderRefs()),
                ["patientSyncId"] = SyncIds.Format(patient),
                ["patientUpdatedAtUtc"] = patientUpdated is { } p ? TursoSyncService.FormatDate(p) : null,
                ["patientFieldStampsJson"] = stamps,
                ["progressNoteSyncId"] = note is { } n ? SyncIds.Format(n) : null,
                ["progressNoteUpdatedAtUtc"] = noteUpdated is { } u ? TursoSyncService.FormatDate(u) : null,
                ["attendingAppointmentSyncId"] = appointment is { } a ? SyncIds.Format(a) : null,
                ["feeServiceId"] = fee,
                ["savedAt"] = TursoSyncService.FormatDate(HandoffFixture.SavedAt)
            };
            HandoffPayloadCodec.PrepareForSending(working, HandoffFixture.SenderRefs(), patient, patientUpdated, note, appointment, fee, HandoffFixture.SavedAt,
                stamps, noteUpdated);
            cases.Add(Case(name, input, new JsonObject { ["payload"] = DraftJson(working) }));
        }

        Prepare("prepare-full", HandoffFixture.WorkingCopy(), HandoffFixture.PatientSyncId, HandoffFixture.PatientUpdatedAtUtc,
            HandoffFixture.PatientFieldStampsJson, HandoffFixture.NoteSyncId, HandoffFixture.NoteUpdatedAtUtc, HandoffFixture.AppointmentSyncId, 1);
        Prepare("prepare-without-stamps-note-or-appointment", HandoffFixture.WorkingCopy(), HandoffFixture.PatientSyncId, HandoffFixture.PatientUpdatedAtUtc,
            null, null, HandoffFixture.NoteUpdatedAtUtc, null, null);
        Prepare("prepare-unstamped-patient", HandoffFixture.WorkingCopy(), HandoffFixture.PatientSyncId, HandoffFixture.PatientUpdatedAtUtc,
            "", HandoffFixture.NoteSyncId, HandoffFixture.NoteUpdatedAtUtc, null, 99);

        // Recibir: cada cargo con el Id de este equipo, o como cargo libre
        void Receive(string name, ConsultationDraft payload, List<CatalogRef> refs)
        {
            var input = new JsonObject { ["payload"] = DraftJson(payload), ["receiverRefs"] = RefsJson(refs) };
            var freeLines = HandoffPayloadCodec.MapReceivedItems(payload, refs);
            cases.Add(Case(name, input, new JsonObject
            {
                ["items"] = new JsonArray(payload.Items.Select(i => (JsonNode?)new JsonObject
                {
                    ["name"] = i.Name,
                    ["lineKind"] = i.LineKind,
                    ["productId"] = i.ProductId <= -1_000_000 ? null : i.ProductId,
                    ["freeCharge"] = i.ProductId <= -1_000_000
                }).ToArray()),
                ["freeLines"] = new JsonArray(freeLines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray())
            }));
        }

        Receive("receive-translates-to-local-ids", HandoffFixture.Payload(), HandoffFixture.ReceiverRefs());
        Receive("receive-with-an-empty-catalog", HandoffFixture.Payload(), new List<CatalogRef>());

        // FindCatalogRef: por SyncId (activo) y si no, el mismo código y el mismo nombre normalizado (activo)
        void Find(string name, CatalogRefKind kind, string? syncId, string code, string itemName)
        {
            var refs = HandoffFixture.ReceiverRefs();
            var item = new ConsultationProductItem { CatalogSyncId = syncId == null ? null : Guid.Parse(syncId), Code = code, Name = itemName };
            var match = HandoffPayloadCodec.FindCatalogRef(refs, kind, item);
            cases.Add(Case(name, new JsonObject
            {
                ["refs"] = RefsJson(refs),
                ["kind"] = kind.ToString(),
                ["item"] = new JsonObject { ["catalogSyncId"] = syncId, ["code"] = code, ["name"] = itemName }
            }, new JsonObject { ["matchId"] = match?.Id }));
        }

        Find("find-by-sync-id", CatalogRefKind.Product, SyncIds.Format(HandoffFixture.Id(970)), "OTRO", "Otro nombre");
        Find("find-by-code-and-normalized-name", CatalogRefKind.Product, SyncIds.Format(HandoffFixture.Id(971)), "cur-001", "GASA ESTERIL 10x10 CM (PAQUETE CON 10 PIEZAS)");
        Find("find-sync-id-of-an-inactive-product", CatalogRefKind.Product, SyncIds.Format(HandoffFixture.Id(972)), "ANT-001", "Alcohol Etílico Desnaturalizado 70%");
        Find("find-same-name-other-code", CatalogRefKind.Product, null, "MED-002", "Paracetamol 500 mg tabletas");
        Find("find-without-sync-id", CatalogRefKind.Product, null, "MED-001", "paracetamol 500 MG tabletas");
        Find("find-other-kind", CatalogRefKind.Service, SyncIds.Format(HandoffFixture.Id(970)), "MED-001", "Paracetamol 500 mg tabletas");
        Find("find-service-by-sync-id", CatalogRefKind.Service, SyncIds.Format(HandoffFixture.Id(980)), "SERV-004", "Monitoreo de Glucemia Capilar");

        // Quien recibe entiende la copia solo si ni el renglón ni el borrador son de una versión más nueva
        foreach (var (payloadVersion, draftVersion) in new[] { (1, 1), (2, 1), (1, 2), (0, 1) })
        {
            cases.Add(Case($"can-read-payload-{payloadVersion}-draft-{draftVersion}",
                new JsonObject { ["payloadVersion"] = payloadVersion, ["draftVersion"] = draftVersion },
                JsonValue.Create(HandoffPayloadCodec.CanRead(payloadVersion, new ConsultationDraft { Version = draftVersion }))));
        }

        var generated = SyncContract.ToStableJson(new JsonObject
        {
            ["rule"] = "handoff-payload",
            ["culture"] = "es-MX",
            ["today"] = null,
            ["now"] = TursoSyncService.FormatDate(HandoffFixture.SavedAt),
            ["contract"] = SyncContract.Revision,
            ["protocol"] = ConsultationHandoffService.ProtocolVersion,
            ["notes"] = new JsonArray(new[]
            {
                "La consulta entre equipos (docs/TURSO_SYNC_ARCHITECTURE.md §4.5; Services/Rules/HandoffPayloadCodec.cs). La copia de trabajo es el borrador de la consulta como lo guarda la PC (ConsultationDraftStore, consulta-en-curso.json), con un reloj fijo: las fechas van tal cual (TestAssets/fase6/handoff-payload.golden.json las cambia por marcadores).",
                "prepare-*: PrepareForSending con savedAt fijo. Quita los Id locales (PatientId 0, AttendingAppointmentId null, cada ProductId 0) y las marcas del envío (Handoff, ReceivedHandoffId, ReceivedHandoffToken); pone PatientSyncId, PatientUpdatedAtUtc, PatientFieldStamps (el estado normalizado de FieldStampsJson con '#piso', sin 'UpdatedAtUtc', golden/field-stamps.json; el objeto vacío si no hay marcas), ProgressNoteSyncId y su ProgressNoteUpdatedAtUtc (solo con nota), AttendingAppointmentSyncId y FeeServiceSyncId (el servicio del honorario por su Id local). Cada cargo: ProductId > 0 = producto, -1 a -999999 = servicio (-Id), lo demás o lo que no está en el catálogo = 'libre'; LineKind y CatalogSyncId. La copia viaja como el texto JSON de payload (ConsultationHandoffs.Payload, PayloadVersion 1).",
                "Las fechas del borrador van como las escribe System.Text.Json (ISO 8601, sin zona: hora del consultorio; PatientUpdatedAtUtc, ProgressNoteUpdatedAtUtc y PatientFieldStamps son UTC sin 'Z'). Kotlin compara por significado (los valores), no el texto.",
                "receive-*: MapReceivedItems. 'producto' y 'servicio' se buscan con FindCatalogRef en el catálogo de quien recibe (productId = Id del producto o -Id del servicio); lo que no se encuentra y lo 'libre' se cobra como cargo libre (freeCharge true, productId null): la PC le da un Id local que sale del nombre (FreeChargeId, -1000000 a -1999999) y solo vale en ese equipo, así que el vector no lo fija y Kotlin usa el suyo; los productos y servicios que no se encontraron van en freeLines (el aviso).",
                "find-*: FindCatalogRef: primero el SyncId (de la misma clase y activo); si no, el mismo código (sin importar mayúsculas) y el mismo nombre normalizado (TextSearch.Normalize: sin acentos, minúsculas), activo. matchId = el Id del renglón de refs, o null.",
                "can-read-*: CanRead: la copia se entiende si PayloadVersion <= 1 (el protocolo de la consulta entre equipos) y la Version del borrador <= 1."
            }.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["cases"] = cases
        });
        CheckOrWrite("contracts/golden/handoff-payload.json", generated);

        // Y el archivo se vuelve a correr con las reglas de C#: cada caso desde su entrada
        foreach (var c in JsonNode.Parse(System.IO.File.ReadAllText(RepoPath("contracts/golden/handoff-payload.json")))!["cases"]!.AsArray())
        {
            var name = c!["name"]!.GetValue<string>();
            var input = c["input"]!;
            JsonNode? actual;
            if (name.StartsWith("prepare-", StringComparison.Ordinal))
            {
                var working = ConsultationDraftStore.Deserialize(input["workingCopy"]!.ToJsonString())!;
                DateTime? Date(string key) => input[key] is { } v ? TursoSyncService.ParseDate(v.GetValue<string>()) : null;
                Guid? Id(string key) => input[key] is { } v ? Guid.Parse(v.GetValue<string>()) : null;
                HandoffPayloadCodec.PrepareForSending(working, ParseRefs(input["senderRefs"]!), Id("patientSyncId")!.Value, Date("patientUpdatedAtUtc"),
                    Id("progressNoteSyncId"), Id("attendingAppointmentSyncId"), input["feeServiceId"]?.GetValue<int>(), Date("savedAt")!.Value,
                    input["patientFieldStampsJson"]?.GetValue<string>(), Date("progressNoteUpdatedAtUtc"));
                actual = new JsonObject { ["payload"] = DraftJson(working) };
            }
            else if (name.StartsWith("receive-", StringComparison.Ordinal))
            {
                var payload = ConsultationDraftStore.Deserialize(input["payload"]!.ToJsonString())!;
                var freeLines = HandoffPayloadCodec.MapReceivedItems(payload, ParseRefs(input["receiverRefs"]!));
                actual = new JsonObject
                {
                    ["items"] = new JsonArray(payload.Items.Select(i => (JsonNode?)new JsonObject
                    {
                        ["name"] = i.Name, ["lineKind"] = i.LineKind, ["productId"] = i.ProductId <= -1_000_000 ? null : i.ProductId, ["freeCharge"] = i.ProductId <= -1_000_000
                    }).ToArray()),
                    ["freeLines"] = new JsonArray(freeLines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray())
                };
            }
            else if (name.StartsWith("find-", StringComparison.Ordinal))
            {
                var item = input["item"]!;
                var match = HandoffPayloadCodec.FindCatalogRef(ParseRefs(input["refs"]!), Enum.Parse<CatalogRefKind>(input["kind"]!.GetValue<string>()),
                    new ConsultationProductItem
                    {
                        CatalogSyncId = item["catalogSyncId"] is { } s ? Guid.Parse(s.GetValue<string>()) : null,
                        Code = item["code"]!.GetValue<string>(),
                        Name = item["name"]!.GetValue<string>()
                    });
                actual = new JsonObject { ["matchId"] = match?.Id };
            }
            else
            {
                actual = JsonValue.Create(HandoffPayloadCodec.CanRead(input["payloadVersion"]!.GetValue<int>(),
                    new ConsultationDraft { Version = input["draftVersion"]!.GetValue<int>() }));
            }
            Assert.AreEqual(c["expected"]!.ToJsonString(), actual!.ToJsonString(), name);
        }
    }

    // ================================================================== equipos vinculados

    private static Dictionary<string, object?> DeviceRow(JsonObject row) =>
        row.ToDictionary(kv => kv.Key, kv => kv.Value switch
        {
            null => null,
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number => (object?)long.Parse(v.ToJsonString(), CultureInfo.InvariantCulture),
            JsonValue v => v.GetValue<string>(),
            _ => kv.Value.ToJsonString()
        });

    private static JsonObject DeviceJson(LinkedDevice d, bool target) => new()
    {
        ["deviceId"] = d.DeviceId,
        ["deviceName"] = d.DeviceName,
        ["role"] = d.Role?.ToString(),
        ["deviceCode"] = d.DeviceCode,
        ["isThisDevice"] = d.IsThisDevice,
        ["isPrimary"] = d.IsPrimary,
        ["isOnline"] = d.IsOnline,
        ["isAvailable"] = d.IsAvailable,
        ["isRevoked"] = d.IsRevoked,
        ["isPendingApproval"] = d.IsPendingApproval,
        ["mergesAsWholeRow"] = d.MergesAsWholeRow,
        ["ignoresServiceSupplies"] = d.IgnoresServiceSupplies,
        ["tokensRotated"] = d.TokensRotatedAtUtc != null,
        ["pendingCount"] = d.PendingCount,
        ["seenText"] = d.SeenText,
        // "Sin conexión desde …" depende de la zona y del día de la computadora: no va
        ["statusText"] = d.StatusText.StartsWith("Sin conexión desde", StringComparison.Ordinal) ? null : d.StatusText,
        ["handoffTarget"] = target
    };

    [TestMethod]
    public void LinkedDevice_MatchesTheVector()
    {
        var now = new DateTime(2026, 1, 15, 17, 0, 0, DateTimeKind.Utc);
        string Ago(TimeSpan ago) => TursoSyncService.FormatDate(now - ago);

        JsonObject Heartbeat(string id, string name, string? role, string version, TimeSpan? ago, string? platform = null, int? handoff = null,
            int? contract = null, int? pending = null)
        {
            var row = new JsonObject { ["DeviceId"] = id, ["DeviceName"] = name, ["DeviceRole"] = role, ["AppVersion"] = version };
            if (ago is { } a) row["LastSeenUtc"] = Ago(a);
            if (platform != null) row["Platform"] = platform;
            if (handoff != null) row["HandoffProtocol"] = handoff;
            if (contract != null) row["ContractRevision"] = contract;
            if (pending != null) row["PendingCount"] = pending;
            return row;
        }

        JsonObject Registry(string id, string code, string role, string name, TimeSpan? revokedAgo = null)
        {
            var row = new JsonObject { ["DeviceId"] = id, ["DeviceCode"] = code, ["Role"] = role, ["DisplayName"] = name };
            if (revokedAgo is { } r) row["RevokedAtUtc"] = Ago(r);
            return row;
        }

        var heartbeats = new JsonArray(
            Heartbeat("device-pc", "Consultorio", "WindowsDesktop", "1.43.0", TimeSpan.FromSeconds(5), "Windows", 1, 15, 0),
            Heartbeat("device-laptop", "Laptop", "WindowsDesktop", "1.43.0", TimeSpan.FromMinutes(2), "Windows", 1, 15, 2),
            Heartbeat("device-pc-vieja", "PC de recepción", "WindowsDesktop", "1.38.0", TimeSpan.FromHours(3), "Windows", 1, 11),
            Heartbeat("device-pc-v126", "PC v1.26", "WindowsDesktop", "1.26.0", TimeSpan.FromDays(2)),
            Heartbeat("device-pc-v116", "PC v1.16", "WindowsDesktop", "1.16.0", TimeSpan.FromMinutes(1)),
            Heartbeat("tab-1", "Galaxy Tab S9", "AndroidTablet", "0.3.0", TimeSpan.FromSeconds(30), "Android", 1, 13, 0),
            Heartbeat("cel-1", "Moto G", "AndroidMobileAdmin", "0.3.0", TimeSpan.FromSeconds(10), "Android", 1, 15),
            Heartbeat("tab-nueva", "Tablet sin aprobar", "AndroidTablet", "0.3.0", TimeSpan.FromSeconds(20), "Android", 1, 15),
            Heartbeat("tab-perdida", "Tablet perdida", "AndroidTablet", "0.2.0", TimeSpan.FromDays(3), "Android", 1, 13),
            Heartbeat("tab-sin-consulta", "Tablet de prueba", "AndroidTablet", "0.1.0", TimeSpan.FromSeconds(40), "Android", 0, 13),
            Heartbeat("tab-olvidada", "Tablet olvidada", "AndroidTablet", "0.3.0", TimeSpan.FromDays(45), "Android", 1, 15));
        var registry = new JsonArray(
            Registry("device-pc", "P1", "WindowsDesktop", "PC del consultorio"),
            Registry("device-laptop", "P2", "WindowsDesktop", "Laptop"),
            Registry("tab-1", "T1", "AndroidTablet", "Tablet del consultorio"),
            Registry("cel-1", "C1", "AndroidMobileAdmin", "Celular del doctor"),
            Registry("tab-perdida", "T2", "AndroidTablet", "Tablet perdida", revokedAgo: TimeSpan.FromDays(1)),
            Registry("tab-sin-consulta", "T3", "AndroidTablet", "Tablet de prueba"),
            Registry("tab-olvidada", "T4", "AndroidTablet", "Tablet olvidada"),
            // Registrado y sin latido (se limpió después de 60 días)
            Registry("tab-sin-latido", "T5", "AndroidTablet", "Tablet guardada"));

        var cases = new JsonArray();
        void Add(string name, string thisDevice, string? primary, DateTime? rotatedAt)
        {
            var input = new JsonObject
            {
                ["heartbeats"] = heartbeats.DeepClone(),
                ["registry"] = registry.DeepClone(),
                ["primaryDeviceId"] = primary,
                ["thisDeviceId"] = thisDevice,
                ["serverNowUtc"] = TursoSyncService.FormatDate(now),
                ["tokensRotatedAtUtc"] = rotatedAt is { } r ? TursoSyncService.FormatDate(r) : null
            };
            cases.Add(Case(name, input, LinkedDevicesExpected(input)));
        }

        Add("seen-from-the-primary-pc", "device-pc", "device-pc", null);
        Add("seen-from-the-tablet", "tab-1", "device-pc", null);
        Add("after-rotating-the-tokens", "device-pc", "device-pc", now.AddMinutes(-1));

        var generated = SyncContract.ToStableJson(new JsonObject
        {
            ["rule"] = "linked-device",
            ["culture"] = "es-MX",
            ["today"] = null,
            ["now"] = TursoSyncService.FormatDate(now),
            ["contract"] = SyncContract.Revision,
            ["notes"] = new JsonArray(new[]
            {
                "Equipos vinculados y a quién se le ofrece «Enviar a…» (ConsultationHandoffService.MergeDevices, ToDevice y HandoffTargets). Entrada: las filas de RegisteredDevices (latidos) y de DeviceRegistry como las devuelven las instrucciones check.peers.* de hrana/statements.json, la hora de la nube y este equipo. Salida: un equipo por latido (en su orden) y después los registrados sin latido.",
                "Rol, código y quitado salen de DeviceRegistry; un Android sin fila espera aprobación (no recibe nada); una PC sin fila conserva el rol de su latido. En línea = latido hace menos de 3 min. Recibe consultas (isAvailable) si HandoffProtocol >= 1, o sin él (PC v1.17–v1.26) si AppVersion >= 1.17.0, y no está quitado ni esperando aprobación.",
                "handoffTarget (a quién se le ofrece la consulta, y quién cuenta como compañero para la cadencia del 8.24): no este equipo, ni quitado ni sin aprobar, un rol que puede llevar consultas (no el celular) y con latido de hace menos de 30 días. «Enviar a…» además solo deja elegir los isAvailable.",
                "mergesAsWholeRow: otra PC con Windows (o sin plataforma y sin rol de Android) vista en 30 días, sin quitar, con ContractRevision < 12 (aviso del 8.22). ignoresServiceSupplies: recibe consultas pero su ContractRevision < 14 (los insumos ajustados de un servicio). tokensRotated: no se ha conectado desde que se anularon los tokens (paso 8.15).",
                "statusText y seenText son los textos de la PC; statusText es null cuando dice «Sin conexión desde <hora local>», que depende de la zona y del día de la computadora."
            }.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            ["cases"] = cases
        });
        CheckOrWrite("contracts/golden/linked-device.json", generated);

        foreach (var c in JsonNode.Parse(System.IO.File.ReadAllText(RepoPath("contracts/golden/linked-device.json")))!["cases"]!.AsArray())
        {
            Assert.AreEqual(c!["expected"]!.ToJsonString(), LinkedDevicesExpected(c["input"]!.AsObject()).ToJsonString(), c["name"]!.GetValue<string>());
        }
    }

    private static JsonArray LinkedDevicesExpected(JsonObject input)
    {
        var now = DateTime.SpecifyKind(TursoSyncService.ParseDate(input["serverNowUtc"]!.GetValue<string>()), DateTimeKind.Utc);
        DateTime? rotated = input["tokensRotatedAtUtc"] is { } r ? DateTime.SpecifyKind(TursoSyncService.ParseDate(r.GetValue<string>()), DateTimeKind.Utc) : null;
        var devices = ConsultationHandoffService.MergeDevices(
            input["heartbeats"]!.AsArray().Select(h => DeviceRow(h!.AsObject())),
            input["registry"]!.AsArray().Select(h => DeviceRow(h!.AsObject())),
            input["primaryDeviceId"]?.GetValue<string>(), now, input["thisDeviceId"]!.GetValue<string>(), rotated);
        var targets = ConsultationHandoffService.HandoffTargets(devices, now).Select(d => d.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new JsonArray(devices.Select(d => (JsonNode?)DeviceJson(d, targets.Contains(d.DeviceId))).ToArray());
    }
}
