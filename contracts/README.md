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

Las 13 tablas sincronizadas **en el orden de subida** (los principales antes que sus dependientes), cada una con:

- `entityType`: el nombre de la clase de C#; es el `EntityType` de las lápidas.
- `columns`: nombre, tipo remoto, si acepta nulo y tipo de C#. Además, según la columna:
  - `timeKind` en las fechas: `utc`, `clinicLocal` (hora del consultorio, America/Mexico_City) o `dateOnly`.
  - `nullValue`: lo que se guarda si una columna obligatoria llega nula.
  - `enumValues` en los enums.
  - `aliasOf`: dos columnas que se escriben iguales.
  - `note`: lo que el equipo que escribe tiene que mantener.
  - `derived`: ninguna columna lo es en la revisión 1.
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

## `golden/`

Vectores con la forma `{rule, culture, today, now, contract, notes, cases: [{name, input, expected}]}`. Las fechas
van con el formato del protocolo (`yyyy-MM-dd HH:mm:ss.fffffff`); `notes` explica cada entrada.

| Archivo | Regla |
|---|---|
| `values.json` | Codificación Hrana de cada tipo (`encode`) y lectura de una fila (`decode`), con el nulo de una columna obligatoria. Un BLOB (`byte[]`) va en hexadecimal en `value` y `expected`; ver abajo. |
| `lww.json` | Qué versión gana: al bajar una fila o una lápida, lo que hace la nube con la subida (el SQL real de la PC, ejecutado en SQLite), qué hacer si la nube rechaza una subida (desde el paso 8.6) y la cerca de generación: con otra generación en la nube no se escribe nada (desde el paso 8.9). |
| `stamping.json` | La marca `UpdatedAtUtc` de un cambio local: `max(hora + desfase, marca guardada + 1 ms)`. |
| `uuid5.json` | UUIDv5 de RFC 4122, el espacio de nombres de TPVC, los SyncId del catálogo que siembra la PC (desde v1.20.6; Kotlin nunca siembra) y los SyncId derivados (un SyncId en mayúsculas da lo mismo). |

**BLOB.** Viaja como `{"type":"blob","base64":…}`, sin `value`. Según el código de libSQL, Turso responde el base64
estándar **sin el relleno `=`** y acepta los argumentos con o sin él (pendiente de ver en Turso real: V18 del plan). El
lector tiene que aceptar las dos formas: `java.util.Base64.getDecoder()` las acepta; `kotlin.io.encoding.Base64.Default`
exige el relleno salvo con `withPadding(Base64.PaddingOption.PRESENT_OPTIONAL)`. Un BLOB vacío no es nulo. Hoy ninguna
tabla sincronizada tiene BLOB (en `schema.json` sería `remoteType: BLOB`, `clrType: byte[]`); el primero llega con el
membrete en la nube (paso 8.12).

## Lo que viene

- `golden/` de los reportes, la receta y el ticket (paso 8.11) y de las reglas del negocio (8.26).
- `hrana/`: intercambios grabados por el servidor simulado, con el SQL literal de cada instrucción (8.13 y 8.25).
- `scenarios/`: escenarios de varios equipos (8.25).
