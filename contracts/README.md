# Contrato de sincronización (PC ↔ app de Kotlin)

Lo que la app de Android tiene que cumplir para hablar con la nube de Turso igual que la PC: el protocolo v2 y, desde
v1.26.0 (paso 8.9), el **protocolo 3**, que agrega la cerca de generación a cada escritura (reglas en `sync/schema.json`,
vectores en `golden/lww.json`). Una nube que se reemplazó con un respaldo exige el 3 (`minClientProtocol`).
Plan: `docs/PLAN_FASE8_VARIOS_EQUIPOS.md` (paso 8.3 y §7.3); protocolo: `docs/TURSO_SYNC_ARCHITECTURE.md` §4.

**No se editan a mano.** Los genera `tests/TPVC.Tests/GoldenVectorTests.cs` desde el mismo código que usa la PC
(`Services/SyncSchema.cs`, `SyncContract.cs`, `SyncRules.cs`, `SyncIds.cs`, `TursoClient.FormatArg`/`ExtractValue` y
`TursoSyncService.ToRemoteValue`/`FromRemoteValue`). La prueba compara cada archivo con lo que sale del código:
si cambia el modelo de EF o una regla sin regenerarlos, la CI falla. Para regenerarlos:

```bash
TPVC_WRITE_GOLDEN=1 dotnet test tests/TPVC.Tests/TPVC.Tests.csproj --filter GoldenVectorTests
```

y revisar la diferencia antes de subirla: la app de Kotlin lee estos archivos.

## `sync/schema.json`

Las 14 tablas sincronizadas (13 hasta la revisión 6) **en el orden de subida** (los principales antes que sus dependientes), cada una con:

- `entityType`: el nombre de la clase de C#; es el `EntityType` de las lápidas.
- `columns`: nombre, tipo remoto, si acepta nulo y tipo de C#. Además, según la columna:
  - `timeKind` en las fechas: `utc`, `clinicLocal` (hora del consultorio, America/Mexico_City) o `dateOnly`.
  - `nullValue`: lo que se guarda si una columna obligatoria llega nula.
  - `enumValues` en los enums.
  - `aliasOf`: dos columnas que se escriben iguales.
  - `note`: lo que el equipo que escribe tiene que mantener.
  - `derived`: una columna que cada equipo calcula con sus datos: sube como información y nunca se aplica al bajar, salvo al crear la fila (desde la revisión 12: el resumen del expediente).
- `references`: la columna `<Referencia>SyncId`, su principal, si es obligatoria y qué pasa al borrar el principal (`cascade`, `setNull`, `restrict`, o `none` en una referencia suave).
- `unique`: columnas únicas en la base local (en la nube la única llave es `SyncId`).
- `ddl`: el `CREATE TABLE` y el índice de la nube.

También trae las lápidas (`tombstones`), las tablas que no se replican (`otherTables`), el DDL completo, las reglas
escritas (`rules`) y la huella (`fingerprint`), que la PC guarda en `_tpvc_meta.schema`.

**`contract`** es la revisión del contrato (`SyncContract.Revision`). La PC la escribe en `_tpvc_meta.contract`.
Los cambios del esquema solo pueden agregar (tablas, columnas o valores de un enum) y obligan a subir la revisión;
`GoldenVectorTests` lo exige al regenerar. Desde el paso 8.23 el archivo queda congelado como base de la tablet.

| Revisión | Versión | Qué agregó |
|---|---|---|
| 1 | v1.20.1 | El contrato inicial (protocolo v2). |
| 2 | v1.21.0 | `Products.UnitCost` (costo por unidad, decimal en texto, `"0"` si falta) y `Products.CostExcludesVat` (entero 0/1: el costo no incluye IVA y se le suma 16 %). |
| 3 | v1.22.0 | `Products.SupplierCode` (texto, nulo si falta): la clave del producto en el pedido del proveedor; la escribe «Ingresar pedido» en la PC. |
| 4 | v1.25.0 | `PatientConsultations.LabOrderJson` (texto, `""` si falta): la orden de laboratorio de la consulta, `{"Studies":[{"Name","Area"}],"Indications"}` con acentos sin escapar; `""` si no se pidieron estudios. |
| 5 | v1.27.0 | Paso 8.10, en `otherTables` (no se replican): la tabla `DeviceRegistry` (rol, código y revocación de cada equipo; solo la escribe la PC) y las columnas nuevas del latido en `RegisteredDevices` (`Platform`, `SyncProtocol`, `HandoffProtocol`, `ContractRevision`, `LastPushUtc`, `PendingCount`, `OldestPendingUtc`, `InstanceNonce`, todas con nulo). Las reglas «Equipos» y «Latido» dicen qué escribe Kotlin. Ninguna tabla sincronizada cambia. |
| 6 | v1.29.0 | Paso 8.12, en `otherTables`: `ClinicProfiles.CrestImageHash` y `HeaderImageHash` (SHA-256 en hex con minúsculas; `''` sin imagen, `NULL` si ninguna PC lo escribió), y las tablas `ClinicAssets` (una fila por imagen completa) y `ClinicAssetChunks` (sus bytes en trozos de 512 KiB, `Content BLOB`). La regla «Membrete» dice cómo bajar una imagen. Ninguna tabla sincronizada cambia. |
| 7 | v1.30.0 | Paso 8.12b, los resultados de laboratorio: la tabla sincronizada `LabResults` (solo los datos: paciente con borrado en cascada, consulta opcional que queda en nulo, título, laboratorio, `ResultDate` solo fecha, notas, tipo, páginas, tamaño y `ContentSha256`), y en `otherTables` `LabBlobs` (una fila por archivo, por su SHA-256, con su estado) y `LabBlobChunks` (trozos de 512 KiB, `Data BLOB`), más el índice de la nube `IX_LabResults_ContentSha256`. La regla «Resultados de laboratorio» y `golden/lab-files.json` dicen cómo subir y bajar. |
| 8 | v1.34.0 | Paso 8.16, los folios desde la nube: en `otherTables` la tabla `FolioCounters` (`Series` como `EXP-2026` o `TKT-20261009`, `LastNumber`), y `Patients.RecordNumber` y `Sales.Folio` dejan de estar en `unique` (un folio repetido se conserva; ya no se renombra con `-R1`). La regla «Folios» dice cuándo se toma el número y cuándo lleva la letra del equipo; `golden/folios.json` y las instrucciones `folio.take` y `folio.raise` de `hrana/statements.json`. |
| 9 | v1.35.0 | Paso 8.17: `Patients.OriginalFolio` y `Sales.OriginalFolio` (texto, nulo si nunca llevó letra): el folio con letra que la PC principal regularizó. Solo la escribe la PC principal; la búsqueda la compara y el ticket reimpreso la muestra. La regla «Folios» dice cómo se regulariza. |
| 10 | v1.36.0 | Paso 8.18, el formato de las hojas del paciente (ninguna tabla ni columna cambia): en `Patients.ActivePrescriptionJson`, `ActiveSuppliesJson` y `ActiveServicesJson` cada renglón lleva `RowId`, un insumo cargado `ChargedLineKind` (`producto` o `libre`) y `ChargedLineSyncId`, y un servicio del catálogo `ServiceSyncId`; los Id locales (`ChargedLineId`, `ServiceId`) siguen solo para las PC anteriores. `PatientConsultations.PrescriptionsJson` puede traer el `RowId` del renglón de donde salió. La `note` de cada columna dice sus campos y la regla «Hojas del paciente» cómo se leen; `golden/synced-json-columns.json`. |
| 11 | v1.37.0 | Paso 8.19, el Kardex completo: `InventoryMovements.Delta` (entero con signo; `0` = fila anterior, que cuenta con la regla vieja de su tipo, ver su `note`) y la referencia opcional `SaleItemSyncId` (el renglón del ticket de una salida o de su devolución; se vacía si el renglón se borra), `MovementType.SaldoInicial = 8`, `Sales.LedgerVersion` (`1` en toda venta nueva, `0` si falta) y `ProductBatches.ClosedAtUtc` (`utc`, nulo = lote abierto; los lotes ya no se borran). Por la referencia nueva, `InventoryMovements` sube después de `Sales` y `SaleItems`. La regla «Existencias» dice cómo se descuenta por lote, cómo se cancela (dos reglas, con `SyncId` determinista) y cómo se cierra un lote (también con `SyncId` determinista); los `SyncId` de las devoluciones y del cierre van en `golden/uuid5.json`, y `golden/alerts.json` no avisa de un lote cerrado. |
| 12 | v1.39.0 | Paso 8.22, la combinación por campo en pacientes: `Patients.FieldStampsJson` (texto, `""` si falta: las marcas por campo y por renglón de las hojas y el piso `#piso` de la última escritura de la fila completa, ver su `note` y la regla), y `Patients.LastVisit`, `NextAppointmentDate`, `NextAppointmentTime`, `NextAppointmentReason` y `NextAppointmentNotes` pasan a `derived` (cada equipo las calcula; agendar ya no escribe el expediente). La regla «Pacientes por campo» dice cómo se marca, cómo se combina al bajar, cuándo sube el resultado y qué exige quien acepta una consulta; la de las columnas derivadas, cómo se tratan. Vectores `canonical-json`, `field-stamps`, `patient-merge`, `patient-summary` y `handoff-accept`. La consulta entre equipos agrega a la copia `PatientFieldStamps` y `ProgressNoteUpdatedAtUtc` (aditivo, `PayloadVersion` sigue en 1). |

## `golden/`

Vectores con la forma `{rule, culture, today, now, contract, notes, cases: [{name, input, expected}]}`. Las fechas
van con el formato del protocolo (`yyyy-MM-dd HH:mm:ss.fffffff`); `notes` explica cada entrada.

| Archivo | Regla |
|---|---|
| `values.json` | Codificación Hrana de cada tipo (`encode`) y lectura de una fila (`decode`), con el nulo de una columna obligatoria. Un BLOB (`byte[]`) va en hexadecimal en `value` y `expected`; ver abajo. |
| `lww.json` | Qué versión gana: al bajar una fila o una lápida, lo que hace la nube con la subida (el SQL real de la PC, ejecutado en SQLite), qué hacer si la nube rechaza una subida (desde el paso 8.6) y la cerca de generación: con otra generación en la nube no se escribe nada (desde el paso 8.9). |
| `stamping.json` | La marca `UpdatedAtUtc` de un cambio local: `max(hora + desfase, marca guardada + 1 ms)`. |
| `uuid5.json` | UUIDv5 de RFC 4122, el espacio de nombres de TPVC, los SyncId del catálogo que siembra la PC (desde v1.20.6; Kotlin nunca siembra), los SyncId derivados (un SyncId en mayúsculas da lo mismo) y, desde v1.37.0 (paso 8.19), los de las devoluciones al cancelar un ticket con las dos reglas de `Sales.LedgerVersion` y los de las dos partes del cierre de un lote. |
| `cash-cut.json` | Desde v1.28.0 (paso 8.11): el corte de caja de un día a partir de sus ventas (`Services/Rules/CashCutRules.cs`): formas de pago, canceladas y servicios contra productos. |
| `alerts.json` | Stock bajo y lotes vencidos o por vencer (30 días), más el estado del lote en Inventario (60 días), en UTC y en America/Mexico_City (`StockAlertRules`). |
| `suggested-purchase.json` | La compra sugerida: cantidad, costo (capturado, con IVA o estimado con el precio), motivo y orden con desempate es-MX (`SuggestedPurchaseRules`). |
| `prescription-sheet.json` | Lo que dice la receta a partir del paciente, los medicamentos y el perfil: edad, fecha, alergias, signos con unidades y medidas generales, en las dos zonas (`Models/PrescriptionSheet.cs`). |
| `prescription-html-fields.json` | Los valores de la receta en HTML (`data-field`), el papel y lo que se dibuja en cada lugar (`Services/Documents/PrescriptionHtmlBuilder.cs`): de aquí sale el PDF del celular. |
| `ticket-text.json` | El ticket en 42 columnas, renglón por renglón (`Services/Documents/TicketText.cs`); la PC imprime los mismos textos. |
| `folios.json` | Desde v1.34.0 (paso 8.16; los folios con letra que se regularizan, desde v1.35.0): cómo se arma cada folio (`Services/Folios/FolioFactory.cs`): la serie (EXP por año, TKT por día), qué folios cuentan para su número (los que llevan letra o `-R1` no), con la nube apagada (el mayor local + 1), con conexión (el número de `FolioCounters`), sin conexión (la letra del equipo y el mayor entre el número guardado fuera de la base y el local), qué es un código de equipo y CNS con y sin conexión. |
| `link-qr.json` | Desde v1.32.0 (paso 8.14): el texto del QR con que la tablet y el celular reciben la URL y el token (`tpvc-link:1:` + base64url de `{"u","t","n"}`), con los textos que no se deben leer (otro prefijo, otra versión, dañado, sin token). Después el equipo espera su fila de `DeviceRegistry` (regla «Equipos» de `schema.json`). |
| `synced-json-columns.json` | Desde v1.36.0 (paso 8.18): las hojas del paciente (`Services/Rules/PatientSheetRows.cs`): el `RowId` determinista de un renglón que llega sin él, lo que la PC escribe al leer y guardar una hoja sin cambios (los campos que no conoce se quedan), lo que escribe al cargar un insumo o un servicio a la cuenta, y con qué renglón de la cuenta se cargó cada uno (por SyncId, cargo libre o nombre; los casos `pcOnly` dependen del Id local de la PC). |
| `canonical-json.json` | Desde v1.39.0 (paso 8.22): la forma canónica con que se compara por significado (`Services/Rules/CanonicalJson.cs` y `PatientSheetCanonical.cs`): JSON con llaves ordenadas, escape mínimo y números sin ceros de más; el valor canónico de cada tipo de columna; y una hoja del paciente con todos sus campos conocidos menos los Id locales. Comparar el texto tal cual haría que la PC y Kotlin subieran sin fin. |
| `field-stamps.json` | Desde v1.39.0 (paso 8.22): las marcas por campo de `Patients.FieldStampsJson` (`Services/Rules/PatientFieldStamps.cs`): qué marca un guardado (campos, renglones agregados, cambiados y quitados, el orden de la hoja), la purga de los quitados a los 90 días, cuándo una fila es una escritura sin marcas y la marca efectiva. |
| `patient-merge.json` | Desde v1.39.0 (paso 8.22): la combinación al bajar un expediente que ya existe (`Services/Rules/PatientMerge.cs`): por campo, escritura sin marcas, desempate, hojas por renglón y su orden, llaves desconocidas, purga, y si el resultado sube (con qué marca) o se queda la fila de la nube. |
| `patient-summary.json` | Desde v1.39.0 (paso 8.22): el resumen derivado del expediente (`Services/Rules/PatientSummaryRules.cs`): la próxima cita en orden clínico (en UTC y en America/Mexico_City), la última visita y cómo se lee la hora de una cita. |
| `handoff-accept.json` | Desde v1.39.0 (paso 8.22): qué le falta a un equipo para tomar una consulta recibida (`HandoffPayloadCodec.CheckArrival`): la versión del expediente, sus marcas por campo y la nota. |
| `lab-files.json` | Desde v1.30.0 (paso 8.12b): cómo se parte un archivo de laboratorio en trozos (con el SHA-256 de cada uno, para un contenido que Kotlin genera igual), el SQL literal de registrar, subir un trozo, cerrar, bajar, medir, archivar, desalojar y limpiar huérfanos (`TursoSyncService.LabFiles.cs`) y los topes: 2 GB en la nube, aviso en 1.6, desalojo hasta 1.8, 10 MB por archivo, fotos de 2000 px en JPEG de calidad 80 y el freno de bytes de Android (300 MB de bajada y 150 de subida al día). |

Todo redondeo lleva un caso de punto medio y cada archivo dice en `notes` cuál regla de redondeo usa (decimal al par con `Math.Round`; hacia arriba en el IVA y en el formato F2/N2). Lo que depende de "hoy" se genera en UTC y en America/Mexico_City con un instante en que el día cambia; los desempates por nombre llevan acentos (Collator es-MX).

**BLOB.** Viaja como `{"type":"blob","base64":…}`, sin `value`. Turso responde el base64 estándar **sin el relleno `=`**
(también el vacío, `""`) y acepta los argumentos con o sin él (comprobado en Turso real el 2026-10-11, V18 del plan). El
lector tiene que aceptar las dos formas: `java.util.Base64.getDecoder()` las acepta; `kotlin.io.encoding.Base64.Default`
exige el relleno salvo con `withPadding(Base64.PaddingOption.PRESENT_OPTIONAL)`. Un argumento que no sea base64 válido
hace fallar **toda la petición** con HTTP 400. Un BLOB vacío no es nulo. Ninguna tabla sincronizada tiene BLOB (en
`schema.json` sería `remoteType: BLOB`, `clrType: byte[]`); los BLOB están en `ClinicAssetChunks.Content`, del membrete
en la nube (desde v1.29.0, paso 8.12), y en `LabBlobChunks.Data`, de los resultados de laboratorio (desde v1.30.0, paso
8.12b). Turso no comprime las respuestas (V21): bajar un trozo cuesta el +33% del base64.

## `hrana/`

Desde v1.31.0 (paso 8.13, kit de conformidad de lectura): los intercambios de un equipo que **lee** la nube, grabados
por el servidor simulado (`FakeHranaServer`) en JSON Lines, y `statements.json` con cada instrucción de lectura literal
(la identidad, la generación, el perfil, la página de cada tabla, el latido, el registro y el membrete). Qué hay en cada
archivo, los marcadores (`<time>`, `<id:N>`, `<nonce:N>`, `<appVersion>`) y cómo levantar el simulador por HTTP para la CI
de Kotlin: `docs/FAKE_HRANA_SPEC.md`. Se regeneran con `TPVC_WRITE_GOLDEN=1`, igual que `golden/`; la huella del esquema
aparece literal, así que cambian con cada revisión del contrato.

## Lo que viene

- `golden/` de las reglas del negocio que faltan (8.26).
- `hrana/` de escritura: los intercambios de la subida (8.25).
- `scenarios/`: escenarios de varios equipos (8.25).
