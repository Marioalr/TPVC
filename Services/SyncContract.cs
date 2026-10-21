using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TPVC.Models;

namespace TPVC.Services;

/// <summary>
/// El contrato de sincronización exportado (<c>contracts/sync/schema.json</c>, paso 8.3): lo que la app de Kotlin
/// necesita saber de cada tabla para hablar el protocolo v2 igual que la PC. Sale del mismo <see cref="SyncSchema"/>
/// que usa el motor, así que un cambio en el modelo de EF aparece como diferencia del archivo (la prueba
/// <c>GoldenVectorTests</c> lo detecta y lo regenera con <c>TPVC_WRITE_GOLDEN=1</c>).
/// </summary>
public static class SyncContract
{
    /// <summary>
    /// Revisión del contrato. Sube con cada cambio del esquema, que solo puede ser aditivo (columnas o tablas
    /// nuevas). La PC la escribe en <c>_tpvc_meta.contract</c> y entra a la huella del esquema.
    /// 2 (v1.21.0): <c>Products.UnitCost</c> y <c>Products.CostExcludesVat</c>.
    /// 3 (v1.22.0): <c>Products.SupplierCode</c>.
    /// 4 (v1.25.0): <c>PatientConsultations.LabOrderJson</c>.
    /// 5 (v1.27.0): la tabla <c>DeviceRegistry</c> y las columnas nuevas del latido en <c>RegisteredDevices</c>.
    /// 6 (v1.29.0): el membrete en la nube: <c>ClinicProfiles.CrestImageHash</c> y <c>HeaderImageHash</c>, <c>ClinicAssets</c> y <c>ClinicAssetChunks</c>.
    /// 7 (v1.30.0): los resultados de laboratorio: la tabla sincronizada <c>LabResults</c>, y <c>LabBlobs</c> y <c>LabBlobChunks</c> con su contenido.
    /// 8 (v1.34.0): los folios desde la nube: la tabla <c>FolioCounters</c>, y <c>Patients.RecordNumber</c> y <c>Sales.Folio</c> dejan de ser únicos.
    /// 9 (v1.35.0): <c>Patients.OriginalFolio</c> y <c>Sales.OriginalFolio</c>, el folio con letra que la PC principal regularizó.
    /// 10 (v1.36.0): el formato de las hojas del paciente (<c>Active*Json</c>): <c>RowId</c> en cada renglón, y
    /// <c>ChargedLineKind</c>, <c>ChargedLineSyncId</c> y <c>ServiceSyncId</c> en lugar de los Id locales (paso 8.18).
    /// 11 (v1.37.0): el Kardex completo (paso 8.19): <c>InventoryMovements.Delta</c> y <c>SaleItemSyncId</c>,
    /// <c>MovementType.SaldoInicial</c>, <c>Sales.LedgerVersion</c> y <c>ProductBatches.ClosedAtUtc</c>.
    /// 12 (v1.39.0): la combinación por campo en pacientes (paso 8.22): <c>Patients.FieldStampsJson</c>, y la próxima cita y la
    /// última visita (<c>NextAppointment*</c>, <c>LastVisit</c>) como columnas derivadas.
    /// 13 (v1.40.0): paso 8.23, <c>Patients.MergedIntoSyncId</c> y las reglas de empalmes, duplicados y la sincronización rápida.
    /// Desde aquí el esquema queda congelado como base de la tablet: <see cref="FrozenRevision"/>.
    /// 14 (pedido del médico del 2026-10-14): en <c>Patients.ActiveServicesJson</c>, la lista de insumos de cada renglón de servicio
    /// cargado (<c>Supplies</c>), ajustable solo para esa consulta (ninguna tabla ni columna cambia).
    /// 15 (paso 8.24): la cadencia y la marca de cambios: el índice parcial <c>IX_ConsultationHandoffs_Pending</c> (F2) y la regla
    /// «Cadencia» con la revisión literal en <c>contracts/hrana/statements.json</c> (ninguna tabla sincronizada cambia).
    /// 16 (pedido del médico del 2026-10-21): en <c>Patients.ActivePrescriptionJson</c> (y la receta de la nota), el principio activo
    /// de cada renglón de marca (<c>ActiveIngredient</c>) y si va entre paréntesis (<c>ShowActiveIngredient</c>); ninguna tabla ni
    /// columna cambia. Un equipo anterior no reconoce el producto de esos renglones: no se le envía una consulta que los tiene
    /// (<see cref="ActiveIngredientRevision"/>).
    /// </summary>
    public const int Revision = 16;

    /// <summary>
    /// Paso 8.23: la revisión de <c>contracts/sync/schema.json</c> que es la base de la tablet. Desde ella solo se admiten cambios
    /// aditivos (tablas, columnas o valores de un enum nuevos, referencias nuevas opcionales en una tabla que ya existe, una
    /// columna que pasa a derivada, un índice que deja de ser único); <c>GoldenVectorTests</c> lo exige al regenerar.
    /// </summary>
    public const int FrozenRevision = 13;

    /// <summary>La revisión desde la que un equipo marca cada campo del expediente (paso 8.22): las anteriores lo combinan como fila completa.</summary>
    public const int FieldStampsRevision = 12;

    /// <summary>
    /// La revisión desde la que un equipo conoce los insumos de cada renglón de servicio (<c>Supplies</c> en
    /// <c>ActiveServicesJson</c>, pedido del 2026-10-14). Uno anterior ve el servicio sin sus ajustes y, al quitarlo, descuenta
    /// los insumos por defecto de hoy en lugar de los que se cargaron: «Enviar a…» y Equipos vinculados lo avisan.
    /// </summary>
    public const int ServiceSuppliesRevision = 14;

    /// <summary>
    /// La revisión desde la que un equipo conoce el renglón de receta de hoy (la marca afuera y el principio activo entre
    /// paréntesis, <c>ActiveIngredient</c> y <c>ShowActiveIngredient</c>, pedido del 2026-10-21). Uno anterior no encuentra el
    /// producto de esos renglones y, al quitar uno, dejaría su cargo en la cuenta: «Enviar a…» no le manda una consulta cuya
    /// receta los tiene y lo dice.
    /// </summary>
    public const int ActiveIngredientRevision = 16;

    /// <summary>Cómo se interpreta una fecha: todas viajan como texto sin zona.</summary>
    public enum TimeKind
    {
        /// <summary>Hora UTC (marcas de sincronización).</summary>
        Utc,

        /// <summary>Hora local del consultorio (America/Mexico_City), con hora: hoy sale de DateTime.Now.</summary>
        ClinicLocal,

        /// <summary>Solo la fecha del consultorio; la hora siempre es 00:00:00.</summary>
        DateOnly
    }

    private static string KindName(TimeKind kind) => kind switch
    {
        TimeKind.Utc => "utc",
        TimeKind.ClinicLocal => "clinicLocal",
        _ => "dateOnly"
    };

    /// <summary>
    /// El tipo de cada fecha sincronizada. Una columna DateTime nueva sin declarar aquí hace fallar la exportación:
    /// el contrato tiene que decir cómo la interpreta la tablet.
    /// </summary>
    private static readonly Dictionary<(Type, string), TimeKind> TimeKinds = new()
    {
        [(typeof(ClinicalService), nameof(ClinicalService.CreatedAt))] = TimeKind.ClinicLocal,
        [(typeof(InventoryMovement), nameof(InventoryMovement.Date))] = TimeKind.ClinicLocal,
        [(typeof(LabResult), nameof(LabResult.ResultDate))] = TimeKind.DateOnly,
        [(typeof(LabResult), nameof(LabResult.CreatedAt))] = TimeKind.ClinicLocal,
        [(typeof(PatientAppointment), nameof(PatientAppointment.Date))] = TimeKind.DateOnly,
        [(typeof(PatientAppointment), nameof(PatientAppointment.CreatedAt))] = TimeKind.ClinicLocal,
        [(typeof(PatientConsultation), nameof(PatientConsultation.Date))] = TimeKind.ClinicLocal,
        [(typeof(PatientConsultation), nameof(PatientConsultation.StartedAt))] = TimeKind.ClinicLocal,
        [(typeof(PatientConsultation), nameof(PatientConsultation.EndedAt))] = TimeKind.ClinicLocal,
        [(typeof(PatientRecord), nameof(PatientRecord.BirthDate))] = TimeKind.DateOnly,
        [(typeof(PatientRecord), nameof(PatientRecord.LastVisit))] = TimeKind.ClinicLocal,
        [(typeof(PatientRecord), nameof(PatientRecord.NextAppointmentDate))] = TimeKind.DateOnly,
        [(typeof(PatientRecord), nameof(PatientRecord.CreatedAt))] = TimeKind.ClinicLocal,
        [(typeof(Product), nameof(Product.CreatedAt))] = TimeKind.ClinicLocal,
        [(typeof(Product), nameof(Product.UpdatedAt))] = TimeKind.ClinicLocal,
        [(typeof(ProductBatch), nameof(ProductBatch.ExpirationDate))] = TimeKind.DateOnly,
        [(typeof(ProductBatch), nameof(ProductBatch.ReceivedDate))] = TimeKind.ClinicLocal,
        [(typeof(ProductBatch), nameof(ProductBatch.ClosedAtUtc))] = TimeKind.Utc,
        [(typeof(Sale), nameof(Sale.Date))] = TimeKind.ClinicLocal,
        [(typeof(Sale), nameof(Sale.CancelledAt))] = TimeKind.ClinicLocal,
    };

    /// <summary>Columnas que repiten a otra (propiedad con get y set sobre la misma variable): se escriben iguales.</summary>
    private static readonly Dictionary<(Type, string), string> Aliases = new()
    {
        [(typeof(Sale), nameof(Sale.PatientName))] = nameof(Sale.CustomerName),
        [(typeof(SaleItem), nameof(SaleItem.ProductName))] = nameof(SaleItem.Description),
    };

    /// <summary>Lo que un equipo que escribe la tabla tiene que mantener por su cuenta.</summary>
    private static readonly Dictionary<(Type, string), string> Notes = new()
    {
        [(typeof(Product), nameof(Product.CurrentStock))] =
            "Existencias del producto. Hoy viaja como valor y se aplica al bajar; quien registra una venta o un movimiento la actualiza en la misma escritura. Pasa a derivada en el paso 8.20.",
        [(typeof(InventoryMovement), nameof(InventoryMovement.Delta))] =
            "Lo que el movimiento suma al libro de existencias, con signo: positivo entra, negativo sale (desde la revisión 11). Quantity es |Delta|. 0 = fila anterior a la revisión 11: cuenta +Quantity si MovementType es EntradaCompra, EntradaAjuste o EntradaDevolucion, o AjusteInventario con StockAfter >= StockBefore, y si no −Quantity. Un movimiento nuevo nunca lleva 0 (regla «Existencias»).",
        [(typeof(InventoryMovement), nameof(InventoryMovement.MovementType))] =
            "SaldoInicial (8, desde la revisión 11) solo lo escribe la PC principal al convertir las existencias en libro (paso 8.20).",
        [(typeof(InventoryMovement), nameof(InventoryMovement.StockBefore))] =
            "Existencia del producto antes de este renglón y, en StockAfter, después (informativo: el Kardex la muestra). Las partes de una operación la van bajando renglón por renglón.",
        [(typeof(Sale), nameof(Sale.LedgerVersion))] =
            "Cómo se cancela (desde la revisión 11): 1 = la venta dejó un movimiento por lote con su SaleItemSyncId y cada uno se devuelve a su lote; 0 = venta anterior, que se devuelve por producto (regla «Existencias»). Toda venta nueva lleva 1.",
        [(typeof(ProductBatch), nameof(ProductBatch.ClosedAtUtc))] =
            "Lote cerrado (desde la revisión 11): «Eliminar lote» ya no lo borra; lo cierra con Quantity 0 e IsDepleted 1 y deja un movimiento con su lote que lleva su libro a 0. Nulo = abierto. FEFO salta los cerrados y las pantallas no los muestran; un movimiento que llega después sigue en su lote.",
        [(typeof(PatientRecord), nameof(PatientRecord.OriginalFolio))] =
            "El folio con letra (EXP-2026-T1-001) con que se creó sin conexión, después de que la PC principal le dio el número normal (desde la revisión 9). La búsqueda compara RecordNumber y OriginalFolio. Solo la escribe la PC principal; nulo si nunca llevó letra.",
        [(typeof(Sale), nameof(Sale.OriginalFolio))] =
            "El folio con letra (TKT-20261009-T1-0001) con que se cobró sin conexión, después de que la PC principal le dio el número normal (desde la revisión 9). El ticket reimpreso lo muestra debajo del folio y la búsqueda lo compara. Solo la escribe la PC principal; nulo si nunca llevó letra.",
        [(typeof(PatientRecord), nameof(PatientRecord.LastVisit))] =
            "Derivada (desde la revisión 12): la fecha con hora de la consulta más reciente del expediente; sin consultas, la que tenía (la del alta). Cada equipo la calcula después de cada guardado y de cada bajada (contracts/golden/patient-summary.json); sube como información y nunca se aplica al bajar, salvo al crear la fila.",
        [(typeof(PatientRecord), nameof(PatientRecord.FieldStampsJson))] =
            "Marcas por campo (desde la revisión 12): objeto JSON plano en forma canónica {llave: marca}, marcas de 27 caracteres en UTC. Llaves: '<columna>' (la última vez que cambió ese campo; en una hoja, su orden), '<hoja>/<RowId>' (renglón agregado o cambiado), '<hoja>/<RowId>/quitado' (renglón quitado), '#piso' (lo que no tiene marca propia vale al menos esta: la última escritura de la fila completa) y 'UpdatedAtUtc' (el UpdatedAtUtc de la fila con que se mantuvieron; si la fila tiene uno mayor o no la tiene, es una escritura sin marcas, con piso en su UpdatedAtUtc). Las llaves que no se conocen se conservan. '' = sin marcas; un texto dañado o con una llave repetida, también. Regla «Pacientes por campo», contracts/golden/field-stamps.json.",
        [(typeof(PatientRecord), nameof(PatientRecord.MergedIntoSyncId))] =
            "El SyncId (minúsculas) del expediente con que se unió este (desde la revisión 13; la herramienta de unir es el paso opcional 8.A y hasta entonces nadie la escribe). No es una referencia: sin llave foránea y el otro expediente puede no existir en el equipo. Es un campo más del expediente (se marca y se combina por campo, regla «Pacientes por campo»). Nulo = no se unió; uno con valor no cuenta para los avisos de duplicados (regla «Duplicados»).",
        [(typeof(PatientRecord), nameof(PatientRecord.ActivePrescriptionJson))] =
            "Hoja de receta vigente: arreglo JSON de renglones {RowId, MedicineName, ActiveIngredient, ShowActiveIngredient, Dosage, RecommendedQuantity, Presentation, DispenseInClinic} (Presentation solo si la hay), nombres PascalCase que se leen sin distinguir mayúsculas y acentos sin escapar; \"\" = hoja vacía. RowId desde la revisión 10 (regla «Hojas del paciente», contracts/golden/synced-json-columns.json). MedicineName es el renglón tal como se imprime: desde la revisión 16 la marca con su concentración y forma y el principio activo entre paréntesis (\"Tempra 500 mg tabletas (Paracetamol)\"); los renglones de antes, con el genérico afuera y la marca entre paréntesis, se quedan así. ActiveIngredient (desde la revisión 16; no se escribe si no lo hay) es el texto entre paréntesis de un renglón de marca, y ShowActiveIngredient (va con él) si MedicineName lo trae; quien los cambia («Mostrar (…)») cambia los dos en el mismo renglón (contracts/golden/catalog-matching.json, 'with-active-ingredient').",
        [(typeof(PatientRecord), nameof(PatientRecord.ActiveSuppliesJson))] =
            "Hoja de insumos: arreglo JSON de renglones {RowId, SupplyName, Instructions, Quantity, UnitPrice} y, si la consulta en curso los cargó a su cuenta, ChargedConsultationFolio, ChargedUnits, ChargedLineKind ('producto' o 'libre'), ChargedLineSyncId (SyncId del producto; no en un cargo libre) y ChargedLineId (Id local del equipo que escribió; solo para las PC anteriores a la revisión 10). Mismo formato que ActivePrescriptionJson; RowId, ChargedLineKind y ChargedLineSyncId desde la revisión 10.",
        [(typeof(PatientRecord), nameof(PatientRecord.ActiveServicesJson))] =
            "Hoja de servicios: arreglo JSON de renglones {RowId, ServiceId, ServiceSyncId, ServiceCode, ServiceName, Category, Notes, Price, Quantity} y, si la consulta en curso los cargó a su cuenta, ChargedConsultationFolio, ChargedUnits y Supplies. ServiceSyncId es el SyncId del servicio del catálogo (no lo lleva uno fuera del catálogo); ServiceId es el Id local del equipo que escribió (0 fuera del catálogo; solo para las PC anteriores a la revisión 10). RowId y ServiceSyncId desde la revisión 10. Supplies (desde la revisión 14; no se escribe si no hay lista) es lo que el renglón cargó a la cuenta además del servicio: arreglo de {SupplyName, Quantity (piezas de todas las unidades juntas), UnitPrice, ChargedLineKind ('producto' o 'libre'), ChargedLineSyncId (SyncId del producto; no en un cargo libre) y ChargedLineId (Id local del equipo que escribió; Kotlin no lo escribe)}; [] = el renglón no cargó insumos. Regla «Hojas del paciente».",
        [(typeof(PatientConsultation), nameof(PatientConsultation.PrescriptionsJson))] =
            "La receta de la nota: arreglo JSON con los mismos renglones que Patients.ActivePrescriptionJson. Desde la revisión 10 puede traer el RowId del renglón de la hoja de donde salió; aquí no se usa para nada y se conserva.",
        [(typeof(PatientRecord), nameof(PatientRecord.NextAppointmentDate))] =
            "Derivada (desde la revisión 12): la fecha de la próxima cita en orden clínico (la primera que no está 'Cancelada' ni 'Atendida' desde hoy, por fecha, hora y SyncId); NextAppointmentTime, NextAppointmentReason y NextAppointmentNotes son los de esa cita (sin cita: nulo y ''). Cada equipo la calcula después de cada guardado y de cada bajada (contracts/golden/patient-summary.json); agendar ya no escribe el expediente. Sube como información y nunca se aplica al bajar, salvo al crear la fila.",
        [(typeof(PatientConsultation), nameof(PatientConsultation.LabOrderJson))] =
            "Orden de laboratorio (desde la revisión 4): JSON {\"Studies\":[{\"Name\",\"Area\"}],\"Indications\"}, con acentos sin escapar; \"\" si la consulta no pidió estudios. Los nombres de las propiedades se leen sin distinguir mayúsculas.",
        [(typeof(LabResult), nameof(LabResult.ContentSha256))] =
            "SHA-256 del archivo en hex con minúsculas (desde la revisión 7): su llave en LabBlobs. El archivo nunca cambia; otro archivo es otro resultado.",
        [(typeof(LabResult), nameof(LabResult.MimeType))] =
            "\"application/pdf\" o \"image/jpeg\" (varias fotos se guardan como un PDF con una página por foto).",
        [(typeof(LabResult), nameof(LabResult.SizeBytes))] =
            "Bytes del archivo; igual a LabBlobs.SizeBytes.",
        [(typeof(LabResult), nameof(LabResult.PageCount))] =
            "Páginas del PDF o fotos que lo forman; 0 si no se sabe.",
    };

    private static readonly string[] Rules =
    {
        "Cada tabla de 'tables' tiene una fila remota por registro, con llave SyncId: GUID en minúsculas con guiones. Los Id enteros locales nunca viajan: una referencia viaja como el SyncId de su principal (columna <Referencia>SyncId).",
        "La subida va tabla por tabla en el orden de 'tables' (los principales antes que sus dependientes) y al final las lápidas. Cada lápida es su upsert seguido de DELETE de la fila con UpdatedAtUtc <= DeletedAtUtc.",
        "UpdatedAtUtc la pone el equipo que escribe (contracts/golden/stamping.json); SyncedAtUtc la pone la nube con su propia hora (strftime('%Y-%m-%d %H:%M:%f','now')); OriginDeviceId es el DeviceId del equipo que escribió.",
        "Gana la versión con UpdatedAtUtc estrictamente más nueva; con marcas iguales se conserva la que ya está (contracts/golden/lww.json).",
        "Valores con la codificación de contracts/golden/values.json. Las fechas viajan como yyyy-MM-dd HH:mm:ss.fffffff, sin zona y sin conversión al escribir; 'timeKind' dice si son UTC, hora local del consultorio (America/Mexico_City) o solo fecha.",
        "Todo SyncId viaja, y entra a un UUIDv5 (contracts/golden/uuid5.json), en minúsculas. En la base local de la PC están en mayúsculas (así guarda EF Core los Guid en SQLite); se comparan como GUID, no como texto.",
        "Una columna obligatoria que llega nula se guarda con su 'nullValue'.",
        "El upsert escribe solo las columnas que el equipo conoce (ON CONFLICT DO UPDATE SET columna por columna, nunca INSERT OR REPLACE), para no borrar las que agregue una versión más nueva.",
        "Un equipo que se actualiza y conoce columnas nuevas de una tabla la baja completa antes de subir nada (la PC lo hace desde el paso 8.7).",
        "Las columnas de 'unique' son únicas solo en la base local; en la nube la única llave es SyncId. Desde la revisión 8 (paso 8.16) Patients.RecordNumber y Sales.Folio ya no son únicas: un folio repetido que llega se guarda tal cual (antes se renombraba con -R1 solo en el equipo que lo recibía) y aparece en un aviso.",
        "Folios (paso 8.16): con la nube verificada, EXP y TKT toman su número de FolioCounters con la instrucción 'folio.take' de hrana/statements.json (2 s de espera; el piso es el mayor número local de la serie). Sin conexión, sin respuesta, con la cuota agotada o con el token rechazado, el folio lleva el código del equipo de DeviceRegistry (EXP-2026-T1-001, TKT-20261009-T1-0001) y el consecutivo de esa serie con letra, el mayor entre el guardado fuera de la base y el local; sin código no se crea. Con la nube apagada o sin verificar, el mayor local + 1. CNS sale de la hora y sin conexión lleva la letra. Los folios con letra no cuentan para la serie normal. Vectores en contracts/golden/folios.json. Regularización (paso 8.17): solo la PC principal, una vez al día y con todos los equipos sin cambios pendientes, le da a cada expediente y ticket con letra el siguiente número de su serie (EXP del año del folio, TKT de su día), guarda el folio con letra en OriginalFolio y actualiza las citas del paciente que lo copian (PatientRecordNumber); todo sube por el ciclo normal. Un registro que vuelve con su folio con letra recibe el mismo número que ya tenía. La tablet y el celular nunca regularizan, y su búsqueda compara también OriginalFolio.",
        "Solo la PC con Windows crea o cambia el esquema de la nube ('ddl', también las columnas nuevas de RegisteredDevices con ALTER TABLE) y escribe _tpvc_meta (claves 'schema' = 'fingerprint' y 'contract' = 'contract'). Los cambios del esquema son aditivos. Kotlin nunca corre DDL ni la limpieza de RegisteredDevices y ConsultationHandoffs, y no sincroniza si falta _tpvc_meta.schema («Sincronice primero la PC»).",
        "Columnas derivadas ('derived': true, desde la revisión 12): cada equipo las calcula con sus propios datos después de cada guardado y de cada bajada, suben como información y nunca se aplican al bajar, salvo para dar el primer valor a una fila que se crea en ese momento (enseguida se recalculan). No entran al registro de cambios ni a la combinación por campo. Hoy: Patients.LastVisit y NextAppointmentDate, NextAppointmentTime, NextAppointmentReason y NextAppointmentNotes (el paso 8.20 agrega las existencias).",
        "Pacientes por campo (paso 8.22, revisión 12): al bajar una fila de Patients que ya existe aquí no se aplica el LWW por fila: se combina (contracts/golden/patient-merge.json). Cada equipo, al guardar, pone en FieldStampsJson la marca de la fila en cada campo que cambió por su significado y en cada renglón de una hoja que agregó, cambió (su texto canónico) o quitó (field-stamps.json); el alta solo pone la llave 'UpdatedAtUtc'. Cada versión se normaliza (field-stamps.json, 'state'): una marca nunca vale más que el UpdatedAtUtc de su fila, y su piso es el mayor entre la llave '#piso' y, en una escritura sin marcas (su UpdatedAtUtc es mayor que su llave 'UpdatedAtUtc' o no la tiene: una PC v1.19–v1.38, o la fila de antes de actualizar), su UpdatedAtUtc. Lo que una versión tiene vale al menos su piso (un campo, el orden de una hoja, cada renglón que tiene) y un renglón que no tiene cuenta como quitado en el mayor entre su piso y su propia marca de quitado: equivale al LWW de la fila completa. Guardar sobre una fila sin marcas deja su UpdatedAtUtc anterior como '#piso' y marca solo lo que cambió; la fila completa reescrita sin cambios («Volver a aplicar») sube el piso a la marca nueva. Por campo gana la marca mayor (sin marca ni piso, la mínima); marcas iguales y valores distintos: gana el texto canónico mayor en orden ordinal. Hojas por renglón (por RowId): un renglón vive si su marca es mayor que la de quitado (iguales: quitado); su contenido es el del lado con la marca mayor; el orden, el del lado con la marca de orden mayor (iguales: la lista de RowId vivos con el texto canónico mayor) y al final los vivos que solo tiene el otro lado, en su orden. El resultado lleva el piso mayor de los dos y no guarda las llaves conocidas que no lo rebasan; las desconocidas se conservan con la marca mayor. Los arreglos solo llevan renglones vivos: lo quitado vive en FieldStampsJson y se purga 90 días después de la marca más nueva del expediente (el piso incluido). Se compara por significado (contracts/golden/canonical-json.json): valores canónicos, hojas con todos sus campos conocidos menos los Id locales (ChargedLineId, ServiceId y, desde la revisión 14, el ChargedLineId de cada insumo de Supplies) y el estado normalizado sin 'UpdatedAtUtc'; dos filas iguales sin marcas (la primera sincronización después de actualizar) no suben nada. Si el resultado es otro que la versión de la nube, la fila queda con UpdatedAtUtc = max(aquí, nube) + 1 ms y 'UpdatedAtUtc' igual, y se anota para subir; si no, se queda la fila de la nube tal cual. La nube sigue con LWW por fila; lo que rechace se repara en la siguiente combinación. Guardar una hoja desde una copia escribe sus ediciones por renglón sobre la hoja guardada. Consulta entre equipos: la copia lleva PatientFieldStamps (el estado normalizado, con '#piso') y ProgressNoteUpdatedAtUtc, y quien la recibe exige tener cada marca o una más nueva contando su propio piso, salvo el par de un renglón quitado que ahí ya se purgaría (contracts/golden/handoff-accept.json).",
        "Existencias (paso 8.19, revisión 11): el libro de un lote es la suma del Delta de sus movimientos (BatchSyncId), y el de las piezas sin lote de un producto, la de sus movimientos sin lote; en una fila con Delta 0 cuenta la regla vieja (la 'note' de Delta). Una salida (venta, consumo, baja) descuenta por FEFO de los lotes abiertos con piezas, primero el que caduca antes (o el lote que se eligió), y deja un movimiento por lote más otro sin lote con lo que no alcanzó; en una venta cada uno lleva el SaleItemSyncId de su renglón y la venta LedgerVersion 1. Cancelar un ticket devuelve cada salida a su propio lote con un movimiento EntradaDevolucion de SyncId determinista: con LedgerVersion 1, uno por salida (las de cada renglón, por SaleItemSyncId), uuid5(tpvc, 'return:' + SyncId de la salida), y si la suma de |Delta| de las salidas de un renglón no da su Quantity no se cancela ('Sincronice primero'); con LedgerVersion 0, uno por producto con lo que suman sus renglones, al lote de la primera salida cuyo Reason empieza con 'Venta POS <Folio> - ' (o con el OriginalFolio) o sin lote, uuid5(tpvc, 'return:' + SyncId de la venta + ':' + SyncId del producto). Si la devolución ya existe, cuenta como hecha y no se vuelve a sumar: dos equipos que cancelan el mismo ticket escriben la misma fila (contracts/golden/uuid5.json). Cerrar un lote (ClosedAtUtc) escribe Delta = −libro del lote con su lote, con SyncId uuid5(tpvc, 'close:' + SyncId del lote), y le resta al producto lo que el lote tenía sin dejarlo en negativo; lo que falte para que el libro del producto baje lo mismo (un lote de antes de la revisión 11 puede mostrar otra cantidad que su libro) va en otro movimiento sin lote, con SyncId uuid5(tpvc, 'close-rest:' + SyncId del lote), la misma hora y el mismo motivo. Una parte cuyo SyncId ya existe (otro equipo cerró el mismo lote) no se vuelve a escribir ni a descontar. Los lotes no se borran; FEFO, las pantallas y las alertas solo toman lotes ni agotados ni cerrados (un lote cerrado al que una PC anterior le devolvió piezas sigue oculto). Las partes de una operación (mismo producto, tipo, Date, SaleItemSyncId, Reason y Responsible) se muestran en el Kardex como un renglón. Hasta el paso 8.20, Products.CurrentStock, ProductBatches.Quantity e IsDepleted se siguen guardando y viajando como valor, y quien escribe un movimiento los actualiza en la misma escritura.",
        "Hojas del paciente (paso 8.18, revisión 10; los insumos de un servicio, revisión 14; el principio activo de la receta, revisión 16): ActivePrescriptionJson, ActiveSuppliesJson y ActiveServicesJson son arreglos JSON de renglones (ver la 'note' de cada columna). Cada renglón lleva RowId (GUID en minúsculas): uno nuevo lo recibe al azar; uno que llega sin él (escrito por una PC anterior, que además lo borra al reescribir la hoja), con el GUID vacío o repetido en la misma hoja recibe al leerse uuid5(tpvc, 'sheetrow:<SyncId del paciente>:<columna>:<posición desde 0>:<nombre tal cual>'), y se guarda con la siguiente escritura (no hay relleno al arrancar). Los Id locales no viajan: un insumo cargado a la cuenta lleva ChargedLineKind ('producto' o 'libre', las palabras de la consulta entre equipos) y ChargedLineSyncId, y un servicio del catálogo ServiceSyncId; la PC sigue escribiendo ChargedLineId y ServiceId (sus Id) para las PC viejas y Kotlin no los escribe. El renglón de la cuenta se busca por 'libre' (el cargo libre de su nombre); un ChargedLineId negativo es un cargo libre en cualquier equipo, también en un producto (su Id sale de un nombre); luego el SyncId (el producto activo o, si no, el de este equipo aunque esté inactivo); un Id viejo positivo cuenta solo si ese producto o servicio local tiene el mismo nombre (en la PC); y si no, por nombre. Al recibir una consulta, los renglones con este formato que ella cargó (ChargedConsultationFolio) se traducen a los Id del equipo que la recibe (un producto, al renglón de la cuenta recibida con su SyncId); uno viejo conserva los Id de la PC anterior que lo escribió, que los usa tal cual si la consulta le regresa, salvo un insumo que aquí es un cargo libre que la cuenta recibida tiene (queda con ese Id negativo). Quien reescribe una hoja conserva los campos que no conoce de cada renglón. Insumos de un servicio (revisión 14): al cargar un servicio del catálogo, su renglón lleva Supplies con sus insumos por defecto por las unidades (un insumo por producto, sumando dos renglones por defecto del mismo producto, en el orden de la configuración, con el nombre y el precio del producto; [] en un cargo libre o sin insumos por defecto). Es lo que el renglón cargó y se ajusta solo para esa consulta: + / − y la X de un insumo, o agregar uno del inventario, cargan o descuentan solo la diferencia, con el mismo tope de existencias de la hoja de insumos; la configuración del servicio (ServiceDefaultProducts) nunca cambia. Cambiar las unidades: una más carga el servicio y los insumos por defecto de una unidad (se suman al insumo del mismo producto, por su SyncId, o entran al final), cada insumo en su renglón de la cuenta, el mismo que encuentran + / −, la X y quitar el renglón (en el equipo que recibió la consulta, un producto que ahí no está activo se cobra en el cargo libre al que se tradujo, nunca como el producto); una menos descuenta el servicio y los de una unidad, cada uno hasta lo que tenga la lista (en 0 sale de la lista); lo ajustado a mano se queda. Quitar el renglón descuenta el servicio por ChargedUnits y cada insumo de Supplies con sus piezas. Un renglón sin Supplies (revisión 13 o anterior) se descuenta como antes: el servicio y sus insumos por defecto de hoy por ChargedUnits. Mostrar los insumos de un renglón sin Supplies (lo que descontaría quitarlo) no lo cambia: esa lista pasa al renglón solo con el primer cambio de verdad (un insumo o sus unidades). Los renglones de una consulta anterior (otro ChargedConsultationFolio) nunca tocan la cuenta de la consulta en curso. Cada insumo busca su renglón de la cuenta con las mismas reglas que un insumo de la hoja (por 'libre', un ChargedLineId negativo, ChargedLineSyncId, el Id viejo con el mismo nombre y por nombre), y al recibir una consulta se traduce igual que ellos, aunque el servicio sea un renglón viejo o un cargo libre. Supplies es parte del renglón: cambiarlo cambia su texto canónico (cada insumo con todos sus campos conocidos menos ChargedLineId), así que marca el renglón y la combinación por renglón lo lleva con él. Un renglón de receta de marca lleva ActiveIngredient y ShowActiveIngredient (revisión 16, ver la 'note' de ActivePrescriptionJson): mostrar u ocultar el principio activo cambia MedicineName y ShowActiveIngredient en el mismo renglón (su texto canónico, que lleva los dos campos, null si faltan), así que también marca el renglón. Un renglón sin ellos (de antes, genérico o escrito a mano) no se reescribe. El producto de un renglón de receta se busca por su nombre con las tres formas de catalog-matching.json (el renglón de hoy con o sin el paréntesis y el de antes). Un equipo con ContractRevision menor que 16 (ActiveIngredientRevision) no reconoce el producto de los renglones con ActiveIngredient (al quitar uno dejaría su cargo en la cuenta): «Enviar a…» no le manda una consulta cuya receta tiene alguno, y lo dice. Vectores en contracts/golden/synced-json-columns.json.",
        "Cerca de generación (protocolo 3, paso 8.9): cada upsert, lápida y DELETE de la subida lleva AND (SELECT \"Value\" FROM _tpvc_meta WHERE \"Key\" = 'generation') IS ? con la generación del equipo como último argumento, y cada petición de subida y cada página de bajada termina con SELECT de _tpvc_meta.generation; si no es la del equipo, se detiene sin avanzar sus cursores (contracts/golden/lww.json).",
        "Generaciones (paso 8.9): _tpvc_meta.generation cambia solo cuando una PC reemplaza la nube con un respaldo; entonces escribe generationPrevious (la anterior, o 'none') y sube minClientProtocol a 3, y al terminar de subir escribe generationReady igual a la nueva. Un equipo que ve otra generación guarda lo que no había subido (cada fila con su protocolo y su revisión del contrato), no escribe ni baja nada mientras generationReady no sea la nueva, y después se vacía, baja todo y ofrece «Volver a aplicar N cambios» (cada fila se vuelve a escribir con marca nueva). Un equipo sin generación guardada adopta la de la nube solo si no existe generationPrevious; si existe, hace lo mismo que con otra generación.",
        "Equipos (paso 8.10): DeviceRegistry dice el rol, el código y la revocación de cada equipo; solo la escribe la PC y sus filas nunca se borran. El rol de un equipo Android sale de su fila de DeviceRegistry: sin fila (aún no aprobado) o con RevokedAtUtc, no baja datos ni recibe consultas. Una PC sin fila usa el DeviceRole de RegisteredDevices. _tpvc_meta.primaryDeviceId es la PC principal. Vincular (paso 8.14): el equipo Android recibe la URL y el token pegados o del QR de la PC (contracts/golden/link-qr.json), escribe su latido con Platform 'Android' y el rol que pide en DeviceRole, muestra los últimos 6 caracteres de su DeviceId en mayúsculas y espera: la PC lo aprueba con su fila de DeviceRegistry (código T o C) o lo rechaza con una fila ya revocada (código X, RevokedReason 'rechazado'). Quitar y perder (paso 8.15): con RevokedReason 'quitado' el equipo Android sube lo pendiente y luego borra su base, su credencial y sus borradores (si no puede subir, lo exporta a un archivo); con 'perdido' los borra sin subir nada. Una PC revocada no borra nada. Turso no anula un token solo: anular los tokens corta a todos, y _tpvc_meta.tokensRotatedAtUtc es la hora de la nube en que el primer equipo sincronizó con un token nuevo (un equipo que no se ha conectado desde entonces necesita el nuevo; no se mueve si ya hay una marca posterior a la última sincronización buena con el token anulado). HTTP 401 es un token anulado o de otra base; HTTP 400 con 'JWT' en el cuerpo es uno mal copiado (V3). Ninguno borra nada: el equipo deja de intentar solo hasta tener otro token y conserva lo pendiente; sin nada pendiente, un equipo Android se borra tras dos 401 con 10 minutos de diferencia, y el 400 no cuenta.",
        "Latido (paso 8.10): cada equipo escribe su fila de RegisteredDevices con un upsert de columnas explícitas (LastSeenUtc con la hora de la nube) y Platform ('Windows' o 'Android'), SyncProtocol, HandoffProtocol, ContractRevision, PendingCount y OldestPendingUtc (cambios sin subir) e InstanceNonce (aleatorio en cada arranque). Al final de cada subida escribe además LastPushUtc con la hora de la nube, en la misma instrucción. Un equipo recibe consultas si HandoffProtocol >= 1; si está vacío (PC v1.17–v1.26), si su AppVersion >= 1.17.0. Si el latido lee en su fila un InstanceNonce ajeno después de haber escrito el suyo, otra instalación usa su DeviceId: toma uno nuevo y vuelve a bajar todo.",
        "Membrete (paso 8.12): ClinicProfiles.CrestImageHash y HeaderImageHash son el SHA-256 (hex en minúsculas) del escudo y de la imagen del encabezado; '' = sin imagen; NULL = ninguna PC con v1.29.0 lo ha escrito (se usa la receta sin imagen). Los bytes viven en ClinicAssetChunks (trozos de 512 KiB, Part desde 0) y la fila de ClinicAssets (ByteCount, ChunkCount, MimeType) se escribe al final: sin ella, la imagen no está completa y se reintenta después. Un equipo baja una imagen solo cuando su hash cambia: lee la fila de ClinicAssets, luego los trozos por (Hash, Part), los une y revisa el tamaño y el SHA-256 antes de usarla. Solo la PC escribe estas tablas y borra las imágenes que el perfil ya no nombra.",
        "Empalmes (paso 8.23, revisión 13): dos citas (PatientAppointments) se empalman si tienen la misma Date (solo la fecha), ninguna tiene Status 'Cancelada' (una 'Atendida' sí cuenta), no son la misma (SyncId) y sus horarios [hora, hora + DurationMinutes) se cruzan: tocarse (una termina a las 10:30 y otra empieza a las 10:30) no es empalme. La hora se lee con la regla del resumen derivado (contracts/golden/patient-summary.json, 'time': H:mm, :ss y AM/PM opcionales; lo que no se lee, las 10:00) y una cita con DurationMinutes 0 o negativo dura 30 minutos. Solo se compara dentro de la misma fecha: una cita que pasa de la medianoche no se compara con las del día siguiente. Antes de agendar o cambiar una cita: la sincronización rápida y luego la pregunta 'Ya hay una cita a las 10:30 con … ¿Agendar de todos modos?'. Después de bajar, las citas que llegaron (de hoy en adelante) se revisan contra las de su día: la agenda marca 'Empalme' y se avisa. Lo que llegó cuenta como llegado aunque la sincronización se corte después de aplicarlo (ya pasó los cursores y no vuelve a bajar): se revisa en la siguiente que llegue a revisar. Los avisos son de cada equipo (no viajan) y se quedan hasta que el médico los descarta. Nunca se cancela una cita sola. La PC pasa una sola vez a 'HH:mm' las horas escritas a mano en v1.7–v1.13 que esta regla no lee y se leen sin adivinar ('5 pm', '4.30 pm', '10.30'; con la nube, después de bajar) y suben como una edición; lo que parece fecha o no se lee se queda como está. Vectores en contracts/golden/appointment-overlap.json.",
        "Duplicados (paso 8.23, revisión 13): dos expedientes pueden ser el mismo paciente si su nombre normalizado es igual (sin acentos ni diéresis, ñ = n, minúsculas, los espacios seguidos como uno; un guion o un punto cuentan) y además tienen la misma BirthDate (solo la fecha) o los mismos últimos 10 dígitos de Phone (solo dígitos; con menos de 10 no se compara). Solo cuentan los activos (IsActive) sin MergedIntoSyncId y con nombre, y nunca el mismo SyncId. Al crear un expediente, después de la sincronización rápida, se avisa (y se puede crear de todos modos); después de bajar, los pares en que está un expediente que llegó van en un aviso (en la PC, el banner de Pacientes), también si la sincronización se corta después de aplicarlo (se revisa en la siguiente). Nunca se une nada solo. Vectores en contracts/golden/duplicate-patients.json.",
        "Sincronización rápida (paso 8.23): antes de cobrar (punto de venta y terminar la consulta), antes de agendar o cambiar una cita y antes de crear un expediente (no al editarlo), con la nube activa, una sincronización normal que se espera 3 s como máximo; si no termina, sigue en segundo plano y lo que se hacía sigue con los datos locales. Sin red, con la sincronización en pausa (cuota agotada, freno de consumo, token rechazado, otra nube) o sin ninguna sincronización buena todavía no se espera nada. Nunca corre a la vez que otra (comparte el candado de la sincronización) y dos seguidas comparten la misma. Si no terminó a tiempo, o si no alcanzó a correr porque otra sincronización tiene el candado, mientras esa siga corriendo el folio (ticket o expediente) no espera otros 2 s a la nube: sale con la letra del equipo. Si el médico cancela lo que hacía mientras se espera (cierra el formulario o la agenda, cancela el cambio de horario), no se guarda nada. Sin conexión se cobra igual (folio con letra, regla «Folios»).",
        "Cadencia (paso 8.24, revisión 15): con la app al frente, cada equipo que recibe consultas hace la revisión en una sola petición, con las instrucciones 'check.*' de hrana/statements.json, literales: las consultas dirigidas a él con el WHERE exacto de 'check.incoming' (así SQLite usa el índice parcial IX_ConsultationHandoffs_Pending y lee ~0 filas), el estado de su envío pendiente ('check.outgoing', solo si tiene uno), y la marca de cambios: el LastPushUtc de cada uno de los otros equipos ('check.peerPush'). Cada 60 s agrega el latido ('heartbeat.readNonce' y 'heartbeat.write') y cada 10 min la lista de compañeros ('check.peers.*': un compañero es otro equipo que puede recibir consultas, PC o tablet, aprobado, sin quitar, con HandoffProtocol >= 1 y visto en los últimos 30 días). Va cada 4 s en pacientes y consulta si hay un compañero o un envío pendiente; si no, cada 30 s. Si el LastPushUtc de algún otro equipo es posterior a la hora de la nube de la última bajada buena de este (la marca de agua guardada + 5 s), sincroniza en ese momento; cada valor de LastPushUtc de cada equipo dispara a lo más una sincronización que corra, aunque falle (una marca en el futuro no hace un ciclo). Además, una descarga de seguridad 10 min después del último intento (y al volver al frente si la última buena fue hace más de 30 s, y al volver la red). Sin red, reintentos a los 30 s, 1, 2 y 5 min y luego cada 5 min, y la revisión se espacia igual, salvo en pacientes con un compañero o un envío pendiente, donde no pasa de 30 s; cualquier cosa que llegue a la nube (una sincronización buena, una instrucción de la consulta entre equipos) o volver al frente regresa la revisión a su paso enseguida. Los plazos se cuentan con un reloj monótono (elapsedRealtime en Android), nunca con la hora del equipo. La sincronización que pide la cadencia no detiene la revisión ni el latido: mientras corre, siguen a su paso y no se pide otra. Con la cuota agotada (BLOCKED), el freno de consumo, el token rechazado u otra nube no se dispara nada, y antes de la primera sincronización buena tampoco (la primera descarga la empieza quien abre la app). Toda sincronización comparte el candado de las demás: nunca dos a la vez. La limpieza de ConsultationHandoffs y RegisteredDevices (cada 6 h) es solo de la PC. El celular no revisa consultas: baja y escribe su latido al abrir y cada 120 s (§3.3 del plan).",
        "Formato congelado (paso 8.23): este archivo en la revisión 13 es la base de la tablet. Desde aquí solo se admiten cambios aditivos: tablas nuevas, columnas nuevas (en la nube todas aceptan nulo), referencias nuevas (opcionales en una tabla que ya existe: un equipo anterior escribe nulo ahí), valores nuevos de un enum (los que existen no cambian de número), una columna que pasa a derivada y un índice local que deja de ser único. Nunca se quita ni se renombra una tabla, columna o referencia, ni cambia su tipo, si acepta nulo, su timeKind, su alias, su principal o si es obligatoria, ni se agrega una referencia obligatoria o un índice único a una tabla que ya existe. GoldenVectorTests lo exige al regenerar.",
        "Resultados de laboratorio (paso 8.12b): LabResults es una tabla sincronizada más y solo lleva los datos. El archivo vive en LabBlobs (una fila por ContentSha256) y LabBlobChunks (trozos de 512 KiB, ChunkIndex desde 0). Subir (PC y tablet; el celular no): registrar la fila con ON CONFLICT DO NOTHING, preguntar qué trozos ya existen, subir los que faltan uno por petición, y el último en la misma transacción (batch con BEGIN/COMMIT) que el cierre, que pone CompletedAtUtc solo si ya están todos. Antes de subir se suma SizeBytes de LabBlobs sin EvictedAtUtc: si con el archivo pasa de 2,000,000,000 bytes, no se sube y queda en el equipo. Bajar: la fila de LabBlobs (sin CompletedAtUtc se sigue subiendo; con EvictedAtUtc ya no está en la nube, solo en la PC principal) y los trozos de 4 en 4 en orden; se revisan el tamaño y el SHA-256 antes de usarlo. Solo la PC principal escribe ArchivedAtUtc, EvictedAtUtc y OrphanSinceUtc y borra trozos o filas de LabBlobs. El SQL literal está en contracts/golden/lab-files.json."
    };

    /// <summary>schema.json listo para escribir: sangría de 2, saltos de línea \n y un salto al final.</summary>
    public static string BuildSchemaJson(IModel model) => ToStableJson(BuildSchema(SyncSchema.FromModel(model)));

    /// <summary>JSON con sangría de 2 y saltos \n (igual en Windows y Linux), sin escapar acentos ni "&lt;".</summary>
    public static string ToStableJson(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentSize = 2,
            NewLine = "\n",
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n";

    internal static JsonObject BuildSchema(SyncSchema schema)
    {
        var tables = new JsonArray();
        int order = 0;
        foreach (var map in schema.Tables) tables.Add(DescribeTable(map, ++order));

        return new JsonObject
        {
            ["contract"] = Revision,
            ["protocol"] = TursoSyncService.RemoteSchemaVersion,
            ["fingerprint"] = schema.Fingerprint,
            ["generatedBy"] = "tests/TPVC.Tests/GoldenVectorTests.cs desde SyncSchema (TPVC_WRITE_GOLDEN=1 lo regenera)",
            ["rules"] = new JsonArray(Rules.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            ["tables"] = tables,
            ["tombstones"] = new JsonObject
            {
                ["table"] = SyncSchema.TombstonesTable,
                ["key"] = new JsonArray("EntityType", "SyncId"),
                ["columns"] = new JsonArray(
                    SystemColumn("EntityType", "TEXT", false, "El 'entityType' de la tabla borrada (nombre de la clase de C#)."),
                    SystemColumn("SyncId", "TEXT", false, "SyncId del registro borrado."),
                    SystemColumn("DeletedAtUtc", "TEXT", false, "Marca del borrado, con la misma regla que UpdatedAtUtc.", TimeKind.Utc),
                    SystemColumn("SyncedAtUtc", "TEXT", false, "Hora de la nube al escribir la lápida.", TimeKind.Utc),
                    SystemColumn("OriginDeviceId", "TEXT", true, "DeviceId del equipo que borró.")),
                ["ddl"] = new JsonArray(SyncSchema.TombstoneStatements.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
            },
            ["otherTables"] = new JsonArray(
                OtherTable(SyncSchema.MetaTable, "Claves de la nube que escribe solo la PC: 'schema' (la huella), 'contract' (la revisión), la identidad y las generaciones (pasos 8.7 y 8.9) y 'primaryDeviceId' (la PC principal, paso 8.10).", SyncSchema.MetaStatement),
                OtherTable("ClinicProfiles", "Perfil del consultorio, una sola fila (Id = 1). Gana el UpdatedAtUtc más nuevo; solo lo edita la PC. Desde el paso 8.12 lleva el hash del escudo y de la imagen del encabezado.", SyncSchema.ClinicProfilesStatement),
                OtherTable(SyncSchema.ClinicAssetsTable, "Las imágenes del membrete por su SHA-256 (paso 8.12): una fila por imagen completa (nunca cambia). Solo la escribe la PC.", SyncSchema.ClinicAssetStatements[0]),
                OtherTable(SyncSchema.ClinicAssetChunksTable, "Los bytes de cada imagen en trozos de 512 KiB (paso 8.12). Solo los escribe la PC.", SyncSchema.ClinicAssetStatements[1]),
                OtherTable(SyncSchema.LabBlobsTable, "El archivo de cada resultado de laboratorio por su SHA-256 (paso 8.12b): tamaño, trozos, tipo y su estado (completo, archivado en la PC principal, desalojado de la nube o huérfano). Lo registran la PC y la tablet; el estado lo cambia solo la PC principal. Incluye el índice de LabResults.ContentSha256.",
                    SyncSchema.LabFileStatements[0], SyncSchema.LabFileStatements[2]),
                OtherTable(SyncSchema.LabBlobChunksTable, "Los bytes de cada resultado de laboratorio en trozos de 512 KiB (paso 8.12b). Los suben la PC y la tablet; solo la PC principal los borra.", SyncSchema.LabFileStatements[1]),
                OtherTable("RegisteredDevices", "Latido de cada equipo (LastSeenUtc y LastPushUtc con la hora de la nube). Desde el paso 8.10 también la plataforma, los protocolos, la revisión del contrato, los cambios sin subir y el InstanceNonce. No se replica; la PC borra cada 6 h los revocados y los que no se ven hace 60 días.", SyncSchema.RegisteredDevicesStatement),
                OtherTable(SyncSchema.DeviceRegistryTable, "Rol, código (P1, T1, C1…) y revocación de cada equipo (paso 8.10). Solo la escribe la PC; sus filas nunca se borran y un código nunca se reutiliza.",
                    SyncSchema.DeviceRegistryStatements),
                OtherTable(SyncSchema.FolioCountersTable, "Un consecutivo por serie de folios (paso 8.16): 'EXP-2026' (año) y 'TKT-20261009' (día). La crea la PC; la PC y la tablet toman el siguiente número con una sola instrucción ('folio.take' de hrana/statements.json). No se replica y reemplazar la nube con un respaldo no la toca.",
                    SyncSchema.FolioCountersStatement),
                OtherTable("ConsultationHandoffs", "Consulta entre equipos (docs/TURSO_SYNC_ARCHITECTURE.md §4.5). No se replica: cada equipo la consulta directamente. Desde la revisión 15 (paso 8.24) con el índice parcial IX_ConsultationHandoffs_Pending, que la revisión usa solo con el WHERE literal de 'check.incoming'.",
                    ConsultationHandoffService.HandoffTableDdl)),
            ["ddl"] = new JsonArray(schema.Statements.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
        };
    }

    private static JsonObject DescribeTable(TableMap map, int order)
    {
        var columns = new JsonArray
        {
            SystemColumn("SyncId", "TEXT", false, "Llave: GUID en minúsculas con guiones."),
            SystemColumn("UpdatedAtUtc", "TEXT", false, "Marca del equipo que escribió (decide qué versión gana).", TimeKind.Utc),
            SystemColumn("SyncedAtUtc", "TEXT", false, "Hora de la nube al escribir la fila; la usa la descarga.", TimeKind.Utc),
            SystemColumn("OriginDeviceId", "TEXT", true, "DeviceId del equipo que escribió; la descarga omite lo propio.")
        };

        foreach (var column in map.Columns) columns.Add(DescribeColumn(map, column));
        foreach (var reference in map.References)
        {
            columns.Add(new JsonObject
            {
                ["name"] = reference.RemoteColumn,
                ["role"] = "reference",
                ["remoteType"] = "TEXT",
                ["nullable"] = !reference.IsRequired,
                ["clrType"] = "Guid"
            });
        }

        var references = new JsonArray();
        foreach (var reference in map.References)
        {
            var principal = reference.PrincipalType;
            references.Add(new JsonObject
            {
                ["column"] = reference.RemoteColumn,
                ["localProperty"] = reference.Property.Name,
                ["principal"] = principal.Name,
                ["required"] = reference.IsRequired,
                ["kind"] = reference.DeleteBehavior == null ? "soft" : "foreignKey",
                ["onDelete"] = DescribeDelete(reference.DeleteBehavior)
            });
        }

        return new JsonObject
        {
            ["order"] = order,
            ["table"] = map.Table,
            ["entityType"] = map.ClrType.Name,
            ["columns"] = columns,
            ["references"] = references,
            ["unique"] = new JsonArray(map.UniqueColumns.Select(u => (JsonNode)new JsonArray(u.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray())).ToArray()),
            ["ddl"] = new JsonArray(SyncSchema.TableStatements(map).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
        };
    }

    private static JsonObject DescribeColumn(TableMap map, SyncColumn column)
    {
        var type = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;
        var result = new JsonObject
        {
            ["name"] = column.Name,
            ["role"] = "data",
            ["remoteType"] = column.RemoteType,
            ["nullable"] = column.IsNullable,
            ["clrType"] = ClrTypeName(type),
            ["derived"] = map.IsDerived(column.Name)
        };

        if (type.IsEnum)
        {
            var values = new JsonObject();
            foreach (var name in Enum.GetNames(type)) values[name] = Convert.ToInt64(Enum.Parse(type, name), System.Globalization.CultureInfo.InvariantCulture);
            result["enumType"] = type.Name;
            result["enumValues"] = values;
        }

        if (type == typeof(DateTime))
        {
            if (!TimeKinds.TryGetValue((map.ClrType, column.Property.Name), out var kind))
            {
                throw new InvalidOperationException(
                    $"{map.ClrType.Name}.{column.Property.Name} es una fecha sin timeKind: decláralo en SyncContract.TimeKinds (utc, clinicLocal o dateOnly).");
            }
            result["timeKind"] = KindName(kind);
        }

        if (!column.IsNullable)
        {
            // Lo que guarda un equipo cuando la columna llega nula, codificado como se manda a Turso
            var replacement = TursoSyncService.FromRemoteValue(null, column.ClrType, isNullable: false);
            result["nullValue"] = TursoClient.FormatArg(TursoSyncService.ToRemoteValue(replacement));
        }

        if (Aliases.TryGetValue((map.ClrType, column.Property.Name), out var aliasOf)) result["aliasOf"] = aliasOf;
        if (Notes.TryGetValue((map.ClrType, column.Property.Name), out var note)) result["note"] = note;
        return result;
    }

    private static JsonObject SystemColumn(string name, string remoteType, bool nullable, string note, TimeKind? timeKind = null)
    {
        var column = new JsonObject
        {
            ["name"] = name,
            ["role"] = "system",
            ["remoteType"] = remoteType,
            ["nullable"] = nullable
        };
        if (timeKind is { } kind) column["timeKind"] = KindName(kind);
        column["note"] = note;
        return column;
    }

    private static JsonObject OtherTable(string table, string purpose, params string[] ddl) => new()
    {
        ["table"] = table,
        ["replicated"] = false,
        ["purpose"] = purpose,
        ["ddl"] = new JsonArray(ddl.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray())
    };

    /// <summary>
    /// Qué pasa en la base local al borrar el principal: "cascade" borra los dependientes, "setNull" vacía la
    /// referencia, "restrict" no deja borrar mientras haya dependientes, "none" (referencia suave) la deja tal cual.
    /// ClientSetNull (lo que EF usa por omisión en una referencia opcional) vacía la referencia de los dependientes
    /// que la app tiene cargados y en SQLite queda como NO ACTION: se reporta como "restrict".
    /// </summary>
    private static string DescribeDelete(DeleteBehavior? behavior) => behavior switch
    {
        null => "none",
        DeleteBehavior.Cascade or DeleteBehavior.ClientCascade => "cascade",
        DeleteBehavior.SetNull => "setNull",
        _ => "restrict"
    };

    private static string ClrTypeName(Type type) => type switch
    {
        _ when type == typeof(string) => "string",
        _ when type == typeof(int) => "int",
        _ when type == typeof(long) => "long",
        _ when type == typeof(short) => "short",
        _ when type == typeof(byte) => "byte",
        _ when type == typeof(byte[]) => "byte[]",
        _ when type == typeof(bool) => "bool",
        _ when type == typeof(decimal) => "decimal",
        _ when type == typeof(double) => "double",
        _ when type == typeof(float) => "float",
        _ when type == typeof(DateTime) => "DateTime",
        _ when type == typeof(Guid) => "Guid",
        _ when type.IsEnum => "enum",
        _ => type.Name
    };
}
