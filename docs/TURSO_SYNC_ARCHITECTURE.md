# Arquitectura de Sincronización Turso Cloud (TPVC)

## 1. Resumen Ejecutivo
TPVC es **local-first (offline-first)**: la base de la PC (`consultorio.db`) es la que se usa en el consultorio y funciona al 100% sin internet. Hoy (v1.20.0) la PC sincroniza en los dos sentidos con **Turso Cloud (libSQL)**: unos segundos después de cada guardado sube sus cambios y descarga los de otros equipos, así que dos PC pueden compartir la misma nube.

**Plan Fase 8 (decidido el 2026-10-09; hecho el paso 8.2 en v1.20.0 y el resto todavía no, ver `docs/PLAN_FASE8_VARIOS_EQUIPOS.md`):**
- Los tres equipos (la PC, una tablet y un celular Android) sincronizan por Turso con este protocolo v2 (§4), cada uno sobre su propia base local. La tablet sube y baja, también sin conexión; el celular baja todo y sube solo citas y entradas de mercancía (§3).
- La app Android es Kotlin nativo: implementa por su cuenta el motor v2 y el contrato de la consulta entre equipos (§4.5), y los dos lados se comparan con los archivos de `contracts/`.
- La PC es la única que crea o migra el esquema remoto (§4.2) y la única que vincula y revoca equipos (§5).
- El borrador del mismo día, con la PC como servidor de la red local y Turso solo como respaldo, se descartó (plan §2.6).
- Respuestas del médico del mismo día (plan §9): un solo consecutivo de folios desde Turso, con la letra del equipo solo sin conexión (§4.4); la tablet cobra sin internet; el celular agenda y registra entradas de mercancía (§3); y la cuenta no tiene token de la API, así que se usan tokens creados a mano (nivel C, §6); el QR, que es solo una facilidad, queda limitado a la red local (§5).

---

## 2. Plan gratuito de Turso: límites y uso estimado

Límites vigentes del plan gratuito (<https://turso.tech/pricing>, consultado el 2026-10-09):
- **Filas leídas al mes:** 500 millones.
- **Filas escritas al mes:** 10 millones.
- **Almacenamiento:** 5 GB.
- **Bases de datos:** 100.
- Además: 3 GB de sincronizaciones de réplicas al mes (TPVC no las usa) y restauración a un punto en el tiempo de hasta 1 día.

Cómo cuenta Turso (<https://docs.turso.tech/help/usage-and-billing>):
- Cada fila recorrida cuenta como leída; una instrucción que no toca filas cuenta como 1.
- `ALTER TABLE` y `CREATE INDEX` recorren toda la tabla (una lectura por fila), y `ALTER` puede escribir cada fila.
- Al pasar un límite, toda instrucción falla con el código `BLOCKED`, también la consulta entre equipos.

Desde v1.20.0 la app muestra estos límites (`TursoQuotaSummary`, `Models/TursoConfig.cs`) y, en *Nube y dispositivos*, el consumo estimado del mes y del día frente a los topes del freno. Hasta v1.19.0 mostraba límites viejos (1,000 millones de lecturas, 25 millones de escrituras y 9 GB) y la leyenda "consume menos del 0.05%".

### Uso estimado (plan §3.2)
Supuestos: 30 días al mes; la PC abierta 10 h al día (8 en Pacientes); la tablet al frente 6 h (5 en pantallas de pacientes); el celular 0.5 h; ~150 sincronizaciones al día por guardados y ~400 filas cambiadas al día.

| Concepto | Hoy (v1.19.0, solo la PC) | Hoy, si los tres equipos hicieran lo mismo | Fase 8 (tres equipos, plan) | Proporción del plan gratuito (Fase 8) |
|---|---|---|---|---|
| Peticiones al día | ~8,700, más del 90% de la revisión de consultas de cada 4 s y el latido de cada 30 s (§4.5) | ~23,000 | ~12,800 (PC ~7.9k, tablet ~4.9k, celular ~40) | El plan no limita peticiones |
| Filas leídas al mes | 0.4M–4.2M | hasta ~12M | 2M–4.5M: cada revisión lee 4–11 filas (con el índice parcial de F2) y hay ~600 bajadas al día de ~25 filas | 0.4–0.9% de 500M |
| Filas escritas al mes | ~45k (el latido es 1,200 al día) | ~120k | ~50k: al día, ~975 latidos (960 de la PC y la tablet, cada 60 s, y ~15 del celular), ~400 filas de datos y ~300 `LastPushUtc` | ~0.5% de 10M; hasta ~1.5% si Turso cuenta las entradas de índice (V10) |
| Peor caso: PC y tablet abiertas 24 h en Pacientes | — | — | ~45k peticiones al día; ~7M lecturas y ~120k escrituras al mes | 1.4% y 1.2% |
| Almacenamiento | Decenas de MB al año | Igual | Igual, más ~1 MB del membrete (`ClinicAssets`) | < 1% de 5 GB |
| Bases | 1 | 1 | 1, sola en su grupo (§6) | 1 de 100 |

**Costos de una sola vez (plan):**
- Vincular un equipo o hacer una «Sincronización completa» lee todo una vez: ~20–30k filas después del paso 8.6. Hoy cuesta ~N²/1000 por tabla (F1).
- La primera subida con el cursor nuevo (8.5) y «Reemplazar la nube» (8.9) escriben ~20k filas (0.2% del mes) en el peor caso. Reemplazar además lee la nube una vez para guardar lo que se perdería.
- La verificación de la primera subida completa (8.7) lee ~20k filas una vez.
- Cada `ALTER` de 8.17, 8.19, 8.20, 8.22 y 8.23 cuesta ~20k lecturas y hasta ~20k escrituras; el índice parcial de 8.24 recorre `ConsultationHandoffs` una vez. Todo junto queda abajo del 1–2% de un mes.

**Dos hallazgos (plan §3.1):**
- **F1 · La descarga completa lee de más.** Con la consulta de página que se usó hasta v1.20.3, SQLite sin `ANALYZE` (como una base nueva de Turso) ordena todo lo que queda de la tabla en cada página de 500: una tabla de 20k filas cuesta ~275k lecturas en lugar de ~20k. Las descargas incrementales no se ven afectadas. Se corrige anteponiendo `"SyncedAtUtc" >= ?` (paso 8.6). Confirmado en Turso real (V15, §6): 26,666 filas leídas en la primera página de 500 con la consulta de hoy y ~2,000 con la corregida.
- **F2 · La revisión de consultas lee de más.** `IX_ConsultationHandoffs_To (ToDeviceId, State)` solo se usa por `ToDeviceId`, así que cada revisión recorre todas las filas que se conservan para ese equipo. Un índice parcial lo baja a ~0 filas (paso 8.24, §4.5). Confirmado en Turso real (V15): 2,006 filas leídas sin él y 6 con él.

**Freno de consumo (paso 8.2, desde v1.20.0 en la PC).** El riesgo no está en el uso normal (margen de ~100×) sino en un ciclo descontrolado: repetir una descarga completa cada 4 s gasta ~540M lecturas en un día, se acaba el mes de los tres equipos y Turso responde `BLOCKED` a todo. Por eso la PC (y después la app) hace como máximo 2 descargas completas al día, con un tope diario de 3M lecturas y 30k escrituras estimadas (`TursoConsumptionBrake`, `Models/TursoConfig.cs`):
- Una descarga completa es la «Sincronización completa» o la primera sincronización (sin marca de agua); se revisa antes de subir nada y se cuenta al empezar a bajar.
- Los topes del día suman lo de la sincronización y lo de la revisión de consultas entre equipos (`AddExternalUsage`). El día es el del consultorio (hora local) y se reinicia a medianoche.
- Al pasar un tope se pausa la sincronización automática (sincronizar tras guardar, al abrir, y la revisión de consultas): estado "Sincronización pausada: consumo anormal" y aviso con *Reanudar* en *Nube y dispositivos*. La pausa se guarda en `turso_config.json` y sigue al reabrir el programa. Lo que pide el médico ("Sincronizar ahora", enviar una consulta) sigue funcionando; otra descarga completa espera a *Reanudar*, que vuelve a contar el día desde cero.
- `BLOCKED` se traduce a "Se agotó la cuota mensual de Turso" (`TursoException.QuotaExhaustedCode`): la sincronización automática y la revisión de consultas esperan 1 h y luego se reintenta sola. Una base archivada por el plan gratuito (10 días sin uso) se traduce con las instrucciones para reactivarla (`turso group unarchive <grupo>`).

> El contador de la pantalla *Nube y dispositivos* (`RecordUsage`) es una estimación. La cifra real está en el panel de Turso.

---

## 3. Equipos y permisos

Tres equipos: la PC con Windows, una tablet Android y un celular Android. El 99% del tiempo están en la misma red, pero la red local no se usa como canal de datos: todo pasa por Turso. La única conexión local es la vinculación por QR, de 10 min y un solo uso (§5). Hoy (v1.19.0) solo existe la app de la PC. Topología del plan Fase 8:

```
 ┌──────────────────────┐    ┌──────────────────────┐    ┌──────────────────────┐
 │ PC Windows (TPVC.exe)│    │ Tablet (Kotlin)      │    │ Celular (Kotlin)     │
 │ consultorio.db       │    │ Room + SQLCipher     │    │ Room + SQLCipher     │
 │ motor v2 (C#)        │    │ motor v2 (Kotlin)    │    │ motor v2 (Kotlin)    │
 └──────────┬───────────┘    └──────────┬───────────┘    └──────────┬───────────┘
            │ sube y baja               │ sube y baja               │ sube y baja
            ▼                           ▼                           ▼
 ┌──────────────────────────────────────────────────────────────────────────────┐
 │ Turso · base del consultorio, sola en su grupo (libSQL, Hrana v2 por HTTPS): │
 │ 13 tablas sincronizadas, SyncTombstones, ClinicProfiles, ClinicAssets,       │
 │ RegisteredDevices, DeviceRegistry, ConsultationHandoffs, FolioCounters,      │
 │ _tpvc_meta                                                                   │
 └──────────────────────────────────────────────────────────────────────────────┘
  Solo al vincular, una vez: la PC entrega la credencial por la red local (§5).
```

Papel de cada equipo (plan §2.2):
- **PC** (`WindowsDesktop`): escribe todo. Es la única que crea o migra el esquema remoto, escribe `DeviceRegistry` y `_tpvc_meta`, vincula y revoca equipos, edita catálogo, inventario, perfil y membrete, y regulariza los folios hechos sin conexión (solo la PC principal).
- **Tablet** (`AndroidTablet`): escribe pacientes, notas, citas, ventas (también sin conexión), movimientos, lápidas, su latido, `FolioCounters` y `ConsultationHandoffs`. El inventario lo ve, no lo edita.
- **Celular** (`AndroidMobileAdmin`): ve expedientes, agenda citas, revisa inventario y registra entradas de mercancía. Escribe citas, lotes y movimientos de entrada (con sus lápidas), y su latido solo con la app al frente; nada clínico, ni recetas ni ventas, y no envía ni recibe consultas. Su primera versión solo baja (app K4); lo que escribe llega en K4b.

### Matriz de permisos (`DevicePermissions`, `Models/TursoConfig.cs`)

| Permiso | PC (`WindowsDesktop`) | Tablet (`AndroidTablet`) | Celular (`AndroidMobileAdmin`) |
|---|:---:|:---:|:---:|
| `CanManageClinicalRecords`: editar expedientes y consulta | sí | sí | no |
| `CanPrescribeMedications`: recetar | sí | sí | no |
| `CanProcessSales`: cobrar y vender, también sin conexión | sí | sí | no |
| `CanManageInventory`: editar inventario y catálogo (productos, precios, conteos, cerrar lotes) | sí | no (solo ve) | no (solo ve) |
| `CanViewDashboard`: resumen | sí | sí | sí |
| `CanViewCashCut`: corte de caja | sí | no (oculto) | sí (supuesto por confirmar, plan §9) |
| `CanViewStockAlerts`: alertas de existencias y compra sugerida | sí | sí | sí |
| `CanConfigureSystem`: configuración, Excel, respaldos, Turso, vincular, perfil y membrete | sí | no | no |
| `CanViewClinicalRecords`: ver expedientes (nuevo, paso 8.10) | sí | sí | sí |
| `CanShareDocuments`: compartir receta y ticket (nuevo, paso 8.10) | sí | sí | sí |
| `CanScheduleAppointments`: agendar citas (nuevo, paso 8.10) | sí | sí | sí |
| `CanRegisterStockEntries`: registrar entradas de mercancía, lote y movimiento de entrada (nuevo, paso 8.10) | sí | no | sí |

- Hoy los permisos son solo informativos: la PC no los usa (el paquete de vinculación que mostraba `GetRoleDescription` se retiró en v1.20.0, §5). El rol se escribe en `RegisteredDevices` con cada latido.
- **Desde v1.20.0 (paso 8.2)** el rol de la PC queda fijo en `WindowsDesktop`: se quitó el selector y una PC guardada con otro rol se corrige al abrir. **Plan Fase 8:** el de la tablet y el celular sale de `DeviceRegistry` (paso 8.10, §4.2), la autoridad sobre el rol, el código de folios y la revocación de cada equipo. Solo la PC la escribe y sus filas nunca se borran. La app de Kotlin oculta pantallas y bloquea escrituras según el rol que lee ahí.
- **Todo lo de esta tabla lo hace cumplir la app.** Con el nivel C (§6) la tablet y el celular usan un mismo token `full-access`: Turso no distingue roles. El corte de caja de la tablet solo se oculta en su interfaz.
- La consulta entre equipos es solo para la PC y la tablet: `GetLinkedDevicesAsync` ya excluye el celular.

---

## 4. Protocolo de Sincronización (versión 2, desde TPVC v1.9.0)

> Esta sección es el contrato que debe implementar **cualquier** cliente: la PC hoy y, con la Fase 8, la app Android en Kotlin.
> Implementación de referencia: `Services/TursoSyncService.cs` + `Services/TursoClient.cs`.
> Pruebas de punta a punta con un servidor Hrana simulado: `tests/TPVC.Tests/TursoSyncEndToEndTests.cs`.
>
> **Contrato de los dos motores (desde v1.20.1, paso 8.3):** el de la PC (C#) y el de Kotlin se fijan en `contracts/sync/schema.json` (revisión 1; revisión 2 desde v1.21.0, con el costo del producto, ver la tabla de `contracts/README.md`), generado desde el mismo mapeo que usa el motor (`Services/SyncSchema.cs`), con vectores en `contracts/golden/` (`values`, `lww`, `stamping`, `uuid5`; ver `contracts/README.md`). Una prueba (`GoldenVectorTests`) compara los archivos con el código: un cambio sin regenerarlos rompe la CI. **Plan Fase 8:** intercambios grabados en `contracts/hrana/` (pasos 8.13 y 8.25). Los cambios son aditivos. Cuando cambie el significado de las existencias (paso 8.20), la nube se marca `protocol = 3` para que un equipo que no conoce las reglas nuevas se detenga.

### 4.1 Transporte
- `POST {url}/v2/pipeline` con `Authorization: Bearer <token>` y cuerpo `{"requests":[{"type":"execute","stmt":{...}}, ..., {"type":"close"}]}`.
- Valores (`args`) según la especificación Hrana:
  - `{"type":"integer","value":"42"}` → **el entero va como texto**. Enviarlo como número JSON hace que Turso responda **HTTP 400** a todo el pipeline (era el error de v1.8.x).
  - `{"type":"float","value":1.5}`, `{"type":"text","value":"..."}`, `{"type":"null"}`.
  - `{"type":"blob","base64":"iVBORw"}` → un BLOB **no trae `value`**: sus bytes van en `base64` (alfabeto estándar, con `+` y `/`). Según el código de libSQL (`libsql-hrana/src/proto.rs`), Turso lo responde **sin el relleno `=`** (`STANDARD_NO_PAD`) y al recibir quita los `=` antes de decodificar, así que acepta las dos formas. La PC manda con relleno; `TursoClient.ExtractValue` regresa `byte[]` con o sin relleno (`TursoClient.DecodeBase64`: `Convert.FromBase64String` solo rechaza lo que manda Turso), un BLOB vacío como `byte[0]` y no `null` (en SQLite `X''` no es nulo), y un base64 inválido lanza `TursoException`. Antes se leía `null`, pero ninguna tabla sincronizada tenía BLOB; los usarán el membrete en la nube (8.12) y los resultados de laboratorio. Una columna `byte[]` del modelo es `BLOB` en la nube. Falta verlo en Turso real (V18, §6); el servidor simulado ya responde sin relleno.
- Cada sentencia de un pipeline se confirma por separado (no hay transacción, ni dentro de un lote ni entre lotes); todas las escrituras son *upserts* idempotentes, así que reintentar un lote es seguro. Una petición `batch` con `BEGIN`/`COMMIT` (y `ROLLBACK` si un paso falla) sí es atómica en Turso (V14, §6); la usa el paso 8.6.

### 4.2 Esquema remoto
Se genera automáticamente desde el modelo de EF Core: **cada entidad `ISyncEntity` tiene una tabla con el mismo nombre y TODAS sus columnas** (no un subconjunto), más:

| Columna | Significado |
|---|---|
| `SyncId TEXT PRIMARY KEY` | GUID en minúsculas con guiones (`Guid.ToString("D")`). Los `Id` enteros locales **no** se suben. |
| `UpdatedAtUtc TEXT` | Hora UTC del dispositivo que hizo el último cambio, formato `yyyy-MM-dd HH:mm:ss.fffffff`. |
| `SyncedAtUtc TEXT` | La asigna el **servidor** (`strftime('%Y-%m-%d %H:%M:%f','now')`) en cada escritura. Base de las descargas. |
| `OriginDeviceId TEXT` | Dispositivo que escribió la versión vigente. |
| `<Referencia>SyncId TEXT` | Cada llave foránea viaja como SyncId: `ProductId → ProductSyncId`, `PatientRecordId → PatientRecordSyncId`, `CategoryId → CategorySyncId`, `SaleItem.ProductId → ProductSyncId`, etc. |

- Tipos: enteros/bool/enums → `INTEGER`; double → `REAL`; texto, `decimal` (exacto), fechas y GUID → `TEXT`.
- Fechas de negocio (consulta, caducidad, cita) se guardan **sin conversión de zona horaria** (hora local del consultorio).
- Tablas especiales: `SyncTombstones (EntityType, SyncId, DeletedAtUtc, SyncedAtUtc, OriginDeviceId)`, `ClinicProfiles` (fila Id = 1), `RegisteredDevices`, `ConsultationHandoffs` (consulta entre equipos, sección 4.5; no se replica), `_tpvc_meta` (huella del esquema).
- `RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc)`: `DeviceRole` es el nombre del enum (`WindowsDesktop`, `AndroidTablet`, `AndroidMobileAdmin`) y `LastSeenUtc` la hora **del servidor**. Desde v1.17 cada equipo lo actualiza cada 30 s mientras revisa la tabla de consultas enviadas (y en cada primera sincronización de la sesión).
- Las tablas creadas por v1.8.x (con `Id` entero) se renombran a `legacy_v1_*`; no se borra nada.

**Plan Fase 8 (plan §2.3):**
- **Solo una PC con Windows crea o migra el esquema remoto** (paso 8.10). Hoy cualquier cliente corre DDL: `EnsureRemoteSchemaAsync` empieza con `CREATE TABLE IF NOT EXISTS _tpvc_meta` y puede renombrar tablas a `legacy_v1_*`, y la consulta entre equipos corre su `CREATE` en cada proceso nuevo (`ConsultationHandoffService.EnsureTablesAsync`). Kotlin nunca ejecutará DDL y se negará a sincronizar si falta `_tpvc_meta.schema` ("Sincronice primero la PC").
- **Tablas nuevas:** `DeviceRegistry` (paso 8.10; solo la PC; rol, código de folios y revocación de cada equipo), `ClinicAssets` (escudo y encabezado del membrete, paso 8.12) y `FolioCounters` (`Series` PK, como `EXP-2026` o `TKT-20261009`, y `LastNumber`; paso 8.16, §4.4). `FolioCounters` la crea la PC y la incrementan la PC y la tablet con una sola instrucción; no es tabla sincronizada (sin `SyncId`), sus filas nunca se borran y "Reemplazar la nube" no la toca (§7). No hay base de vinculación: la credencial se entrega por la red local (§5).
- **Columnas nuevas, todas aceptan nulo:**
  - En tablas que no salen del modelo de EF, con `ALTER` explícito incluido en la huella: `RegisteredDevices.Platform`, `SyncProtocol`, `HandoffProtocol`, `ContractRevision`, `LastPushUtc`, `PendingCount`, `OldestPendingUtc` e `InstanceNonce` (8.10); `ClinicProfiles.CrestImageHash` y `HeaderImageHash` (8.12).
  - En tablas sincronizadas: `Patients.OriginalFolio` y `Sales.OriginalFolio` (8.17); `InventoryMovements.Delta` y `SaleItemSyncId`, `Sales.LedgerVersion` y `ProductBatches.ClosedAtUtc` (8.19); `InventoryMovements.CountedQuantity` (8.20); `Patients.FieldStampsJson` (8.22); `Patients.MergedIntoSyncId` (8.23). Cada `ALTER` recorre su tabla una vez (§2).
- **Claves de `_tpvc_meta`** (solo las escribe la PC): `clinicId` (8.7), `generation` y `generationReady` (8.7, 8.9; §7), `protocol` y `minClientProtocol` (2 desde 8.7, 3 desde 8.20), `contract` (revisión aditiva de `schema.json`; la escribe la PC desde v1.20.1, junto a `schema`), `manifest` (8.7), `fullUploadAtUtc` (fecha de la primera subida completa verificada, 8.7), `stampsRepairedAtUtc` (la reparación única de las marcas en el futuro, 8.7) y `primaryDeviceId` (la PC principal, 8.10: la única que regulariza folios, 8.17, y crea saldos iniciales, 8.20). Desde v1.20.5 la PC escribe `clinicId`, `protocol`, `minClientProtocol`, `manifest`, `generation`, `generationReady` y `fullUploadAtUtc` al terminar una «Sincronización completa» con reporte limpio, y `stampsRepairedAtUtc` en su primera sincronización.
- **Fechas:** `schema.json` marca el `timeKind` de cada columna (desde v1.20.1): `utc` (`UpdatedAtUtc`, `SyncedAtUtc`), `clinicLocal` (fechas del negocio en la hora del consultorio, `America/Mexico_City`, que hoy salen de `DateTime.Now`) o `dateOnly` (`BirthDate`, fecha de la cita).

### 4.3 Ciclo de sincronización
1. **Esquema:** se compara la huella en `_tpvc_meta`; solo si cambió se crean tablas / columnas nuevas (`ALTER TABLE ADD COLUMN`).
1b. **Hora e identidad de la nube** (desde v1.20.5): la primera instrucción regresa la hora del servidor, la marca de agua y las claves de `_tpvc_meta`. Antes de escribir o aplicar nada:
   - si `minClientProtocol` es mayor que el del programa, se detiene ("actualice TPVC");
   - si la nube tiene un `clinicId` distinto del de `DbMeta`, se detiene ("esta nube es de otra base"). En *Nube y dispositivos* se ofrecen «Unirse a esta nube», «Esta PC ya usaba esta nube» y «Reemplazar con la nube» (§4.3a);
   - guarda el desfase del reloj en `DbMeta` (lo usan las marcas y los triggers; aviso si pasa de 2 min);
   - repara las marcas locales más de 2 min en el futuro, y las de la nube una sola vez.

   Si el modelo conoce columnas que la última bajada de una tabla no tenía, esa tabla se baja completa antes de subir. Cuando no hay nada que subir, esta instrucción es el primer paso de la petición de la bajada.
2. **Subida:** lo anotado en el registro de cambios local (`SyncChangeLog`) después del último `Seq` subido (desde v1.20.3; antes, las filas con `UpdatedAtUtc` posterior a la última subida). Cada fila anotada que todavía existe sube completa, en orden de dependencias (categorías → productos → lotes → ...), en lotes de 80 sentencias; desde v1.20.4 cada lote es una transacción (`batch` con `BEGIN`/`COMMIT`, V14) y no parte una operación: la lápida con su `DELETE`, y una fila con las que le pertenecen (referencia obligatoria con borrado en cascada: la venta con sus renglones, el producto con sus lotes y movimientos, el paciente con sus consultas y citas):
   `INSERT ... SELECT ... WHERE NOT EXISTS (borrado en SyncTombstones con DeletedAtUtc >= UpdatedAtUtc) ON CONFLICT(SyncId) DO UPDATE SET ... WHERE excluded.UpdatedAtUtc > tabla.UpdatedAtUtc` (gana la versión más nueva y un registro ya borrado en otro equipo no se vuelve a crear). El borrado se reescribe solo si es más nuevo.
   Borrados: lo anotado que ya no existe aquí sube como su lápida más reciente: upsert en `SyncTombstones` + `DELETE ... WHERE SyncId = ? AND UpdatedAtUtc <= fechaDelBorrado`.
   **Desempate** (desde v1.20.4): si un upsert afecta 0 filas, se vuelve a leer esa fila de la nube (y su lápida) en una sola petición y se aplica aquí aunque las marcas empaten, salvo que la local se haya vuelto a editar (`SyncRules.AfterRejectedUpsert` y `ServerRowAppliesAfterReject`, vectores de `lww.json`). Así dos equipos que guardaron con la misma marca terminan iguales. No se hace en la primera subida, en la que casi todo afecta 0 filas.
3. **Descarga** (desde v1.20.4): cada tabla tiene su cursor (`SyncedAtUtc`, `SyncId`) en `DbMeta` (`PullCursor:<tabla>`), que se guarda en la misma transacción que aplica cada página, sin pasar de la marca de agua del servidor (`now - 5 s`): un corte a media descarga sigue donde iba. Las páginas se piden con `"SyncedAtUtc" >= ? AND ("SyncedAtUtc" > ? OR ("SyncedAtUtc" = ? AND "SyncId" > ?))`, **excluyendo las de este mismo dispositivo** (salvo con `DbMeta.CloudDownloadPending`, §4.3a), de 100 filas en Patients y PatientConsultations y de 500 en las demás; si la respuesta es demasiado grande o tarda demasiado, la página se reduce a la mitad. La bajada incremental es **una sola petición**, una transacción de lectura (una sola foto): la marca de agua, la primera página de las 13 tablas y de las lápidas, y el perfil; solo se piden más páginas de las tablas que llenaron la suya. La descarga completa va una tabla por petición. Las filas se aplican sin re-estampar `UpdatedAtUtc` (bandera `AppDbContext.ApplyingRemoteChanges`) y con los triggers del registro apagados (`SyncApplyState`, desde v1.20.3), traduciendo SyncId → Id local.
4. **Perfil del consultorio:** gana el `UpdatedAtUtc` más reciente (viene en la misma petición que la bajada).
5. Se guarda `LastPulledServerWatermark` (al actualizar desde v1.20.3, sirve de cursor de todas las tablas la primera vez). El cursor de subida (`DbMeta.LastPushedSeq`, dentro de la base) se guarda en cuanto termina la subida.

Hasta v1.20.1, una fila cuya referencia obligatoria no tenía `SyncId` se omitía en la subida con solo un aviso en la bitácora y no se reintenta. En la descarga pasa lo mismo con la fila que llega sin su principal obligatorio; una referencia **opcional** cuyo principal todavía no existe en este equipo se guarda en nulo y no se corrige después (la marca local ya es igual a la remota), y un borrado remoto que no se puede aplicar solo se anota. Y hasta v1.20.2, si el reloj de la PC se atrasaba después de sincronizar, lo que se guardara antes de la siguiente sincronización quedaba con una marca anterior a `LastPushedLocalUtc` y no subía nunca (esa sincronización movía el cursor a la hora atrasada); solo subía si se volvía a editar o con «Sincronización completa». Los borrados en cascada que hacía SQLite tampoco dejaban lápida.

### 4.3a Siembra, unirse y recuperar (desde v1.20.6, paso 8.8)
- **Siembra:** el programa ya no siembra datos demo al arrancar (`Initialize(db, seedDemoData: false)`); solo salen del asistente inicial. Una base nueva siembra las 5 categorías y los servicios SERV-001..005 con `SyncId` fijos, UUIDv5 en el espacio de TPVC (`SyncIds.Seeded`: `Category:<nombre>`, `ClinicalService:<código>`, `ServiceDefaultProduct:<código del servicio>|<código del producto>`; vectores en `contracts/golden/uuid5.json`), y con `UpdatedAtUtc = 2000-01-01`. Dos equipos nuevos suben el mismo catálogo sin duplicarlo, y cualquier edición o borrado real le gana a una siembra repetida. Kotlin nunca siembra.
- **«Unirse a esta nube»** (en una PC nueva o descompuesta es «Recuperar desde la nube»): exige que la nube tenga `clinicId` y que la base esté intacta (ninguna fila en las tablas que sincronizan, salvo las categorías y los servicios sembrados, reconocidos por nombre y código). Respalda (`consultorio-…-antes-unirse-a-la-nube.db`) y, en una transacción con los triggers apagados, vacía las tablas, las lápidas, las filas apartadas y el registro de cambios; lleva `LastPushedSeq` al último `Seq`; pone en `DbMeta` `CloudJoined = 1` (ya nunca siembra, ni a media descarga ni desde el asistente), el `clinicId` y `CloudDownloadPending = 1`; quita los cursores de bajada, `PullColumns:*` y la marca de agua de la configuración. La sincronización siguiente baja todo **sin omitir las filas que subió este mismo `DeviceId`** mientras `CloudDownloadPending` esté puesta, y la quita al terminar. Nada sube: ni lápidas ni el catálogo borrado.
- **«Reemplazar con la nube»:** el mismo camino con cualquier base (teclear CONFIRMAR; respaldo `…-antes-reemplazar-con-la-nube.db`), y un `DbInstanceId` nuevo. Lo que no había subido se pierde (queda en el respaldo).

**Plan Fase 8 (pasos 8.4–8.8, hechos en v1.20.2 a v1.20.6):**
- **Filas apartadas** (`SyncParkedRows`, local, paso 8.4, desde v1.20.2): lo que antes se omitía se aparta, se reintenta en cada sincronización y se cuenta en pantalla ("N cambios sin sincronizar"). Incluye la fila que trae una referencia opcional que todavía no existe; se aplica en nulo solo si su principal tiene lápida (aquí o en la nube); una fila cuyo principal obligatorio tiene lápida se descarta. Lo apartado al bajar se reintenta al final de cada descarga, así que lo que llega en la misma sincronización se aplica en ella.
- **Cursor por secuencia** (paso 8.5, desde v1.20.3): `SyncChangeLog`, un registro de cambios local que llenan triggers de SQLite (alta, edición y borrado en las 13 tablas) y que también ve los borrados en cascada. La subida va por `Seq > LastPushedSeq`, guardado en `DbMeta` (dentro de `consultorio.db`) junto a un `DbInstanceId`, y ya no depende del reloj. Las lápidas salen de los mismos triggers, con la marca de siempre (`max(ahora + desfase, guardada + 1 ms)`, vectores de `stamping.json`). Lo recibido no se anota (`SyncApplyState`). La primera vez (y con «Sincronización completa» o una base restaurada) sube todo una vez, de forma idempotente.
- **Descarga eficiente** (paso 8.6, desde v1.20.4): consulta con `"SyncedAtUtc" >= ?` adelante (F1); bajada incremental en una sola petición y una sola foto (una sincronización sin cambios pasó de 16 peticiones a 1); descarga completa con una tabla por petición; página de 100 en Patients y PatientConsultations y de 500 en las demás, que se reduce sola; **el cursor de cada tabla se guarda con cada página**, así un corte continúa donde iba. Si un upsert afecta 0 filas, se vuelve a leer la fila del servidor y se aplica aunque las marcas empaten. La subida va en transacciones que no parten una operación (V14).
- **Hora del servidor e identidad** (paso 8.7, desde v1.20.5): la primera instrucción de cada sincronización regresa la hora del servidor, `generation`, `protocol`, `minClientProtocol` y `clinicId`. Las marcas locales salen de `max(UtcNow + desfase, guardada + 1 ms)` (`ISyncClock`), con aviso si el desfase pasa de 2 min, y las marcas locales con más de 2 min en el futuro se vuelven a marcar antes de subir (las de la nube, una vez). Una base con otro `clinicId` no sincroniza. Si el modelo conoce columnas nuevas, la tabla se baja completa antes de subir sin pisar el valor de la nube. La «Sincronización completa» da un reporte (filas apartadas, conteo local contra remoto, tablas `legacy_v1_*`) y solo si sale limpio escribe `clinicId` y `fullUploadAtUtc`, sin el cual no se podrá vincular (A6).
- **Siembra segura, unirse y recuperar** (paso 8.8, desde v1.20.6): catálogo sembrado con `SyncId` fijos y marca del año 2000, datos demo solo desde el asistente, y «Unirse a esta nube» / «Reemplazar con la nube» con `CloudJoined` (§4.3a).

### 4.4 Garantías
- **Transacciones:** `TursoClient.ExecuteBatchAsync` no es transaccional: cada sentencia del pipeline se confirma sola. Desde v1.20.4 la subida y la bajada incremental usan `TursoClient.TransactionAsync`, un `batch` con `BEGIN`/`COMMIT` y `ROLLBACK` si un paso falla, que en Turso es atómico (V14): un lote de la subida se aplica completo o nada, y la bajada lee una sola foto. Todo sigue siendo idempotente, así que un lote que falla se reintenta completo sin dañar nada.
- **Sin ciclos:** lo recibido conserva su marca y no vuelve a subirse ni a descargarse.
- **Relojes desfasados:** las descargas usan el reloj del servidor, no el del dispositivo. Desde v1.17 un cambio local siempre lleva una marca posterior a la que ya tenía la fila (`max(ahora, marca guardada + 1 ms)`), también en los borrados: una edición hecha después de recibir una fila de un equipo con el reloj adelantado ya no la descarta la nube. Desde v1.20.5 "ahora" es la hora de este equipo más su desfase con la nube, y una marca más de 2 min en el futuro se repara con la hora del servidor (aquí en cada sincronización; en la nube, una vez).
- **Folios duplicados** (dos equipos crean `EXP-2026-005` sin conexión): el registro recibido se guarda como `EXP-2026-005-R1` en lugar de bloquear la sincronización. El `-R` solo existe en el equipo que recibe y no se sube, así que el mismo registro queda con dos folios. **Plan Fase 8 (pasos 8.16 y 8.17, respuesta 1 del médico):** un solo consecutivo por serie. Con conexión, cualquier equipo toma el siguiente EXP o TKT de `FolioCounters` con una sola instrucción atómica (`INSERT … ON CONFLICT(Series) DO UPDATE SET LastNumber = LastNumber + 1 RETURNING LastNumber`; V16, §6). Sin conexión, el folio lleva el código del equipo de `DeviceRegistry` (`EXP-2026-T1-001`, `TKT-20261009-T1-0001`; la PC, `P1`) y sube así. La PC principal lo regulariza en segundo plano, como máximo una vez al día, con la nube al día y sin consulta ni venta en curso: le da el siguiente número del contador y guarda el folio con letra en `Patients.OriginalFolio` o `Sales.OriginalFolio`, que la búsqueda también compara. El cambio sube por el ciclo normal. CNS sigue saliendo de la hora: sin conexión lleva la letra y nunca se regulariza. Los folios que ya existen no cambian. `RecordNumber` y `Folio` pasan de índice único a índice normal y el `-R` deja de aplicarse. Las filas viejas con `-R` se quedan, y un folio repetido aparece en un aviso de la PC.
- **Sincronización completa** (botón "Sincronización completa"): ignora las marcas y sube/descarga todo; útil al enlazar un equipo nuevo. Después de restaurar un respaldo regresa de la nube las filas más nuevas y deshace la restauración (§7). Con la Fase 8, restaurar y recuperar seguirán §7.

**Plan Fase 8, garantías nuevas cuando escriban varios equipos:**
- **Pacientes por campo** (pasos 8.21 y 8.22): guardar solo lo que cambió y, al bajar, combinar por campo con `Patients.FieldStampsJson`, y las hojas `Active*Json` por renglón con su `RowId`. La nube sigue con LWW por fila.
- **Existencias como libro de movimientos** (pasos 8.19 y 8.20): `Delta` con signo, un movimiento por lote, devoluciones con `SyncId` determinista, lotes cerrados en lugar de borrados y conteos como ancla. `Products.CurrentStock`, `ProductBatches.Quantity` e `IsDepleted` pasan a ser derivadas: suben como información y nunca se aplican al bajar.
- **Cerca de generación** (paso 8.9): cada escritura de la subida lleva la condición de que `_tpvc_meta.generation` siga siendo la misma (§7).

### 4.5 Consulta entre equipos (`ConsultationHandoffs`, desde v1.17)
Para pasar la consulta en curso de un equipo a otro (PC ↔ PC, y PC ↔ tablet con la Fase 8) con **un solo dueño a la vez**. La tabla no se replica: cada equipo la consulta directamente (`Services/ConsultationHandoffService.cs`). Hoy funciona entre dos PC.

```sql
CREATE TABLE IF NOT EXISTS ConsultationHandoffs (
  Id TEXT PRIMARY KEY NOT NULL,                 -- GUID "D" que genera quien envía
  FromDeviceId TEXT NOT NULL, FromDeviceName TEXT NOT NULL,
  ToDeviceId TEXT NOT NULL,   ToDeviceName TEXT NOT NULL,
  PatientSyncId TEXT NOT NULL, PatientName TEXT NOT NULL, Folio TEXT NOT NULL,
  Payload TEXT NOT NULL,                        -- copia de la consulta (JSON); '' cuando ya no hace falta
  PayloadVersion INTEGER NOT NULL,              -- 1
  State TEXT NOT NULL,                          -- 'enviada' | 'aceptada' | 'cancelada'
  AcceptToken TEXT,                             -- ficha del intento que la tomó
  SenderAckAtUtc TEXT,                          -- quien envió ya vio el final
  CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL);   -- hora del servidor
CREATE INDEX IF NOT EXISTS IX_ConsultationHandoffs_To ON ConsultationHandoffs (ToDeviceId, State);
```

Reglas (cada transición es **una sola instrucción condicionada**; las tuberías Hrana no son transacciones):
1. **Enviar:** quien envía sube primero lo pendiente (sincronización), deja su consulta en solo lectura con una marca guardada en su borrador local y hace `INSERT ... State='enviada'`.
2. **Revisar:** quien espera consulta cada pocos segundos (4 s en Pacientes, 30 s fuera) `WHERE ToDeviceId = ? AND (State='enviada' OR (State='aceptada' AND Payload<>''))` y, cada 30 s, actualiza su `LastSeenUtc` (escrito con la hora del servidor). La revisión no lee la hora del servidor: solo la lee `GetLinkedDevicesAsync`, para saber qué equipos están en línea en "Enviar a…".
3. **Tomarla:** quien recibe descarga lo último (el expediente debe tener al menos la versión `PatientUpdatedAtUtc` de la copia y, si la hay, la nota de avance `ProgressNoteSyncId`), guarda en su equipo una ficha aleatoria y hace `UPDATE ... SET State='aceptada', AcceptToken=? WHERE Id=? AND ToDeviceId=? AND State='enviada'`. Una fila afectada = es suya. Si la respuesta se pierde, `SELECT State, AcceptToken` dice si su ficha ganó. La copia se borra (`Payload=''`) **solo después** de guardarla en el equipo; mientras tanto, una fila `aceptada` con copia y con la ficha propia se vuelve a ofrecer para abrirla.
4. **Recuperar / envío dudoso:** quien envió hace en una sola petición `INSERT ... State='cancelada' ON CONFLICT(Id) DO UPDATE SET State='cancelada', Payload='' WHERE State='enviada'` y luego `SELECT State`: `aceptada` → se cierra en este equipo (sin descartar ni tocar el expediente); cualquier otra cosa → vuelve a ser editable. Un `INSERT` atrasado ya no puede revivir el envío. Un error de red al enviar (aunque la instrucción haya llegado) se resuelve así antes de volver a editar.
5. **Limpieza:** solo filas terminadas, sin copia y con `SenderAckAtUtc` de hace más de 7 días (o de más de 365 días).

Copia (`Payload`): el `ConsultationDraft` del guardado automático en JSON (nombres PascalCase) con identificadores que valen en cualquier equipo: `PatientSyncId`, `PatientUpdatedAtUtc`, `ProgressNoteSyncId`, `AttendingAppointmentSyncId`, `FeeServiceSyncId`; `PatientId = 0`. Cada cargo lleva `LineKind` (`producto` | `servicio` | `libre`), `CatalogSyncId` y `Code`/`Name`/`UnitPrice`/`Quantity`; `ProductId` va en 0 (quien recibe lo traduce a su Id local: por `CatalogSyncId`, o mismo código y nombre activos; si no existe, cargo libre sin descontar existencias). Fechas de la consulta en hora local del consultorio.

Versión mínima para recibir: TPVC 1.17.0 (la hoja "Enviar a…" marca como incompatibles los equipos con `AppVersion` anterior). El celular no aparece en "Enviar a…".

**Plan Fase 8:**
- La tablet implementará este mismo contrato en Kotlin (app K8): mismas instrucciones y en el mismo orden. El celular no envía ni recibe: aunque escribe su latido, su rol lo deja fuera de la lista.
- **Compatibilidad** (paso 8.10): un equipo puede recibir si `RegisteredDevices.HandoffProtocol >= 1`. Si esa columna está vacía (PC v1.17–v1.19), sigue la regla de `AppVersion`. Así la app Android puede numerar sus versiones desde 1.0.0. Los equipos revocados en `DeviceRegistry` se excluyen.
- **Marcas por campo en la copia** (paso 8.22): hoy aceptar solo compara el `UpdatedAtUtc` de la fila del paciente. La copia llevará además las marcas por campo del paciente y la de la nota, y quien recibe exigirá que cada marca local sea mayor o igual ("Intente de nuevo en unos segundos"). Es un campo aditivo: `PayloadVersion` sigue en 1.
- **Cadencia** (paso 8.24, `Services/SyncScheduler.cs`): la misma revisión lee `RegisteredDevices.LastPushUtc` de los otros equipos y, si es posterior a la última descarga, sincroniza en ese momento; más una descarga de seguridad cada 10 min. El latido pasa a 60 s. La revisión rápida (4 s, en Pacientes) solo corre con un envío pendiente o con un equipo capaz de recibir consultas visto en los últimos 30 días; si no, cada 30 s.
- **Índice parcial** (F2, paso 8.24): `(ToDeviceId) WHERE State='enviada' OR (State='aceptada' AND Payload<>'')`. SQLite solo lo usa si el `WHERE` repite esa condición, así que el SQL de la revisión va literal en `contracts/hrana/`.
- Solo la PC corre el `CREATE` de esta tabla y su limpieza (paso 8.10).

---

## 5. Vinculación de la tablet y el celular

### Hasta v1.19.0: paquete en claro (retirado en v1.20.0, paso 8.2)
Hasta v1.19.0, en *Configuración → Nube y dispositivos → Vincular tablet y celular*, "Generar código" abría una ventana con un JSON **en texto claro** (`TursoSettingsService.GenerateAndroidPairingPayload`) para copiar y pegar: URL de la base, **token completo de Turso**, rol, descripción del rol, consultorio y médico. No había QR ni cifrado, no vencía, y ninguna app lo leía. "Copiar código" lo ponía en el portapapeles, donde pudo quedar en el historial de Windows.

Quien tenga ese texto puede leer y escribir toda la base de la nube. v1.20.0 retiró `GenerateAndroidPairingPayload`, su ventana (`OpenPairingModal`) y `CopyPairingPayload`; los botones dicen "Disponible con la vinculación por QR". Si alguna vez se copió ese código, conviene rotar el token de la PC (§6). Desde v1.20.0 la URL de Turso además solo se acepta con `https://` o `libsql://` (`http://` solo para `localhost`), porque el token viaja en cada petición.

### Plan Fase 8: QR en la red local (paso 8.14; detalle en el plan §4.4 y §4.5)
El QR es solo una facilidad y queda limitado a la red del consultorio (respuesta 4 del médico). No hay base de vinculación en Turso ni token en el QR. La PC solo ofrece vincular si su sincronización está sana, existe `_tpvc_meta.fullUploadAtUtc` (paso 8.7), ya se pegó el "token para tablet y celular" (§6) y hay menos de 3 equipos activos. La PC y el equipo deben estar en el mismo Wi-Fi.
1. **El médico la inicia** en *Nube y dispositivos → Equipos vinculados → Vincular tablet o celular* y elige el rol. La PC crea un `PairingId` de 16 bytes y un secreto de 128 bits, reserva un código de equipo (`T1`, `C1`…: el mayor de `DeviceRegistry` + 1) y abre un `TcpListener` (`System.Net.Sockets`) en un puerto al azar, **solo en sus direcciones privadas** (RFC 1918 y de enlace local) y solo mientras dura la ventana, 10 min como máximo. No pide administrador ni `urlacl`, ni usa Kestrel. La primera vez, el Firewall de Windows puede preguntar si permite a TPVC en redes privadas.
2. **El QR** es `tpvc-pair:1:` + base64url(JSON) con las direcciones y el puerto de la PC, el `PairingId`, el secreto, el rol, el código de equipo, el nombre del consultorio y el vencimiento. **Ningún token.** El mismo texto pegado sirve para que otra PC con Windows se una ("Unirse con código", código `P2`…).
3. **El equipo se presenta** (se niega si no tiene bloqueo de pantalla seguro): genera su propio `DeviceId`, se conecta a la PC y manda el `PairingId`, su `DeviceId`, nombre, modelo y un nonce con un HMAC-SHA256 que prueba que tiene el secreto. El secreto nunca viaja. La PC atiende un solo equipo por ventana; tres HMAC inválidos la cierran. Los dos muestran un código de confirmación de 6 dígitos sacado del secreto y del mensaje del equipo. Las llaves salen de HKDF-SHA256(secreto, sal = `PairingId`).
4. **El médico confirma en la PC**, que pregunta "¿Vincular «Galaxy Tab S9» como Tablet? Código 482 913". Con *No*, la PC cierra la ventana sin mandar nada.
5. **Con *Sí*, la PC** inserta la fila en `DeviceRegistry`, manda AES-256-GCM de {URL de la base del consultorio, token para tablet y celular, rol, código y `clinicId`} y cierra el `TcpListener`.
6. **El equipo termina:** descifra la credencial, la guarda envuelta con el Android Keystore y empieza su primera descarga.

**Respaldo** si falla la red local (aislamiento de clientes del Wi-Fi, Firewall): en el equipo se teclean la URL y el token para tablet y celular; el equipo escribe su latido y la PC pregunta si lo vincula, con los últimos 6 caracteres de su `DeviceId` en las dos pantallas.

Quien fotografía el QR no obtiene nada fuera de la red local o después de los 10 min. Dentro de la ventana, a lo más compite por la vinculación, y el médico vería un nombre o un código de confirmación que no coincide. Quien escucha el Wi-Fi sin el QR solo ve el HMAC y la credencial cifrada.

### Plan Fase 8: quitar y equipo perdido (paso 8.15; plan §4.7)
- **Quitar:** la PC muestra antes los cambios pendientes del equipo (`PendingCount` y `OldestPendingUtc` de su latido). El equipo sube lo pendiente, o lo exporta a un archivo, y luego borra su base, su credencial y sus borradores.
- **Perdí este equipo:** revocar en `DeviceRegistry` (`RevokedAtUtc`, motivo "perdido"; si el equipo lee su fila revocada, se borra sin subir) y "Cortar acceso ahora", marcado por defecto. Sin token de la API la PC no puede rotar sola, así que guía al médico: rotar los tokens de **todo el grupo** en el panel de Turso o con `turso db tokens invalidate` (Turso no revoca tokens uno por uno), crear el token nuevo de la PC y el de tablet y celular, pegarlos en la PC y volver a vincular los demás equipos con un QR nuevo.
- **Ante un 401** (con el nivel C no hay vencimientos: un 401 es una rotación): un equipo con cambios pendientes se bloquea, muestra "Vuelva a vincular" y los conserva; uno sin nada pendiente se borra solo tras dos 401 con 10 min de diferencia.

---

## 6. Guía Rápida de Configuración en Turso

> **El token de la PC no sale de la PC.** Se pega solo en TPVC de Windows, que lo guarda cifrado con DPAPI en `%LocalAppData%\TPVC\turso_config.json`. No lo copie a la tablet ni al celular (el paquete de vinculación que lo llevaba en claro se retiró en v1.20.0, ver §5). Con la Fase 8 la tablet y el celular usarán otro token, el "token para tablet y celular", que la PC les entrega cifrado al vincularlos en la red local (§5), nunca en el QR.

**Plan Fase 8 (credenciales, plan §4.3): nivel C.** La cuenta de Turso no tiene token de la API de la plataforma (respuesta 4 del médico), así que la PC no puede crear ni rotar tokens.
- Dos tokens creados a mano en el panel o con la CLI, `full-access` y sin vencimiento: el de la PC (como hoy) y un "token para tablet y celular", que se pega una vez en la PC (cifrado con DPAPI) y que en el equipo queda envuelto con el Android Keystore.
- La base del consultorio va **sola en su grupo** de Turso: la única forma de cortar un token es rotar las llaves del grupo (`turso db tokens invalidate`), y eso corta a todas las bases del grupo. Después de rotar se crean y pegan los dos tokens nuevos en la PC y se vuelven a vincular los demás equipos (§5).
- **El costo, dicho claro:** cada equipo guarda un token `full-access` sobre todos los expedientes. Los límites por rol los aplica la app (§3), así que quien saque el token de un equipo puede hacer todo hasta la rotación. Por eso la app usa SQLCipher, la credencial envuelta con el Keystore y bloqueo de pantalla obligatorio.
- Las pruebas de tokens y de rotación se hacen en otro grupo o en otra cuenta, **nunca en el grupo del consultorio**: una rotación ahí corta al médico.

### Opción A: Mediante Turso CLI
```bash
# 1. Instalar Turso CLI
curl -sSfL https://get.tur.so/install.sh | bash

# 2. Iniciar sesión gratuita
turso auth signup # o turso auth login

# 3. Crear base de datos para la clínica
turso db create tpvc-clinica --location qro # o la región más cercana

# 4. Obtener URL de conexión
turso db show tpvc-clinica --url
# Ejemplo: libsql://tpvc-clinica-usuario.turso.io

# 5. Generar token de autenticación
turso db tokens create tpvc-clinica

# 6. (Plan Fase 8, paso 8.14; todavía no se usa) Segundo token, el "token para tablet y celular", para pegarlo en la PC
turso db tokens create tpvc-clinica

# Cortar acceso (plan Fase 8, paso 8.15): rota los tokens de todo el grupo; después se crean y pegan los dos nuevos
turso db tokens invalidate tpvc-clinica
```

### Opción B: Mediante Consola Web de Turso
1. Ingresar a [https://turso.tech](https://turso.tech) y registrarse gratis con GitHub o Google.
2. Hacer clic en **"Create database"**.
3. Nombrar la base de datos (ej. `tpvc-clinica`) y elegir la región más cercana (ej. `qro` Querétaro, México o `dfw` Dallas).
4. Copiar la **Database URL** y generar un **Auth Token** desde la pestaña *Settings > Tokens*.
5. Pegar ambos valores en TPVC Windows en **Configuración > Nube y dispositivos**.
6. Hacer clic en **"Probar conexión"** y posteriormente en **"Sincronización completa"**.
7. (Plan Fase 8, paso 8.14; todavía no se usa) Generar un segundo token, el "token para tablet y celular", y pegarlo en TPVC Windows. Nunca se pega directo en la tablet ni en el celular, salvo en el respaldo a mano cuando la vinculación por la red local no funciona (§5).

### Comprobaciones en una base de prueba real (plan §4.2)
Se hacen antes del paso indicado, siempre en un grupo o una cuenta de prueba. El resultado se anota aquí.

| # | Qué se comprueba | Antes de | Resultado |
|---|---|---|---|
| V3 | Qué responde Hrana a un token rotado o desconocido (HTTP 401 o un código Hrana) | 8.14 | Pendiente |
| V4 | Cuánto dura la interrupción al rotar | 8.15 | Pendiente |
| V7 | Texto y estado HTTP de `BLOCKED` | 8.2 | Pendiente. v1.20.0 salió sin comprobarlo: reconoce `BLOCKED` como código de error Hrana o en el cuerpo de una respuesta HTTP de error (también "Operation was blocked"). La documentación de Turso solo dice que la instrucción falla con el código `BLOCKED` |
| V9 | Si el plan gratuito archiva una base sin uso y qué error da | 8.2 | Pendiente. Según la documentación de Turso, el plan gratuito archiva tras 10 días sin uso y se reactiva con `turso group unarchive <grupo>`; falta el error que recibe el cliente. v1.20.0 lo reconoce por la palabra "archived" en el error |
| V10 | Si las entradas de índice cuentan como filas escritas | 8.2 | Pendiente. No cambia el código: solo el margen de §2 (~0.5% o ~1.5% del mes) |
| V11 | Si el plan gratuito tiene bitácora de auditoría, y de cuántos días | 8.15 | Pendiente |
| V12 | El tope de tamaño de una respuesta Hrana | 8.6 | Comprobado el 2026-10-10 (`scripts/verify-turso-hrana.ps1`): una instrucción devolvió 64 MB sin error; no se encontró tope. El tamaño de página lo deciden la memoria y el tiempo de cada equipo, no Turso |
| V14 | Si una petición `batch` de Hrana con `BEGIN`/`COMMIT` es atómica en Turso | 8.6 | Comprobado el 2026-10-10: sí. Un `batch` con `BEGIN`, dos `INSERT` (el segundo falla por `NOT NULL`), `COMMIT` si todo salió bien y `ROLLBACK` si no, deja 0 filas; sin error deja las 2 |
| V15 | `EXPLAIN QUERY PLAN` de F1 y F2 en Turso real (libSQL no es SQLite 3.53) | 8.6 | Comprobado el 2026-10-10 con 20k filas sintéticas. F1: la consulta de hoy usa `MULTI-INDEX OR` y ordena con un B-tree temporal; la primera página lee 26,666 filas y una de en medio 13,335 para devolver 500. Con `"SyncedAtUtc" >= ?` adelante usa el índice en orden (`SEARCH … (SyncedAtUtc>?)`, solo ordena el último término) y cada página lee ~2,000, sin importar el tamaño de la tabla. F2: la revisión lee 2,006 filas sin el índice parcial y 6 con él; Turso lo usa con la consulta tal como está |
| V16 | Que `INSERT … ON CONFLICT DO UPDATE SET LastNumber = LastNumber + 1 RETURNING LastNumber` corra por Hrana como una sola instrucción atómica y regrese el número, también con dos equipos a la vez. Si no: leer el número n y `UPDATE … SET LastNumber = n + 1 WHERE Series = ? AND LastNumber = n`, que se reintenta si afecta 0 filas | 8.16 | Pendiente |

| V18 | BLOB con y sin relleno de base64, y la forma en que regresa | 8.12 | Pendiente. Según el código de libSQL, Turso responde el base64 sin relleno `=` y acepta los argumentos con o sin él (§4.1); la PC lee las dos formas y el servidor simulado responde sin relleno. Falta correr `scripts/verify-turso-hrana.ps1 -Only V18` (~10 filas escritas): lee BLOB escritos en el SQL, manda argumentos con y sin relleno, uno vacío y uno inválido, y revisa los bytes guardados |

V1, V2, V5, V6, V8 y V13 se retiraron: solo servían al nivel A o a la base de vinculación.

---

## 7. Respaldo y recuperación

**Hoy (v1.19.0).** Al restaurar un respaldo local se borran los cursores (`LastPulledServerWatermark` y el de subida: `LastPushedLocalUtc` hasta v1.20.2, `DbMeta.LastPushedSeq` de la base restaurada desde v1.20.3; `DataImportService`): la siguiente sincronización sube todo, pero la nube conserva las filas más nuevas que el respaldo (gana el `UpdatedAtUtc` mayor) y la descarga omite las que escribió esta misma PC. Con una sola PC, la base local queda como el respaldo y la nube conserva lo posterior: las dos copias quedan distintas, y una «Sincronización completa» regresaría esas filas y desharía la restauración. Lo que escribió otra PC sí regresa en la siguiente descarga (riesgo H6 del plan).

La copia de Turso no es un historial: el plan gratuito solo restaura a un punto de hasta 1 día atrás. El historial son los respaldos locales diarios (`BackupService`, se conservan 14).

Lo que sigue es el plan Fase 8 y **todavía no está implementado**, salvo lo marcado como hecho.

### Generación de la nube (pasos 8.7 y 8.9)
- `_tpvc_meta` guarda `clinicId` (se escribe una sola vez), `generation` (cambia al reemplazar la nube con un respaldo) y `generationReady` (la generación cuya subida completa ya terminó). Cada equipo guarda los suyos en `DbMeta`.
- **Cerca de generación:** cada upsert, lápida y `DELETE` de la subida lleva `AND (SELECT "Value" FROM _tpvc_meta WHERE "Key"='generation') = ?`, y cada petición termina leyendo la generación. Si cambió, el equipo se detiene. Sin esto, los trozos de 80 instrucciones que otro equipo seguía subiendo caerían en la nube restaurada.
- Una base con datos propios no sincroniza con una nube de otro `clinicId`: solo ofrece "Unirse", "Reemplazar con la nube" o "Esta PC ya usaba esta nube" (paso 8.7).

### Restaurar con la nube activa (paso 8.9)
La restauración (`DataImportService`) pregunta:
- **"Reemplazar la nube con este respaldo":**
  1. Antes de borrar, baja de la nube las filas que no están en el respaldo o son más nuevas, muestra los conteos por tabla ("se perderán 12 notas y 30 ventas de la tablet") y las guarda en un archivo `.db`.
  2. Guarda "reemplazo en curso" en `DbMeta`; si TPVC se cierra, lo continúa al abrir.
  3. Escribe la generación nueva, borra las tablas sincronizadas y las lápidas de la nube (no `FolioCounters`), y sube todo (~0.2% del mes) con el reporte del paso 8.7.
  4. Al final escribe `generationReady` con la generación nueva y un `fullUploadAtUtc` nuevo.
- **"Desconectar esta PC de la nube".**

**Los demás equipos**, al ver otra generación, dejan de subir y guardan lo que no habían subido. Solo cuando `generationReady` es igual a la generación nueva borran y vuelven a bajar todo, una sola vez, y nadie baja una nube a medio reemplazar. Después ofrecen "Volver a aplicar N cambios", que inserta esas filas con marca nueva. El archivo del paso 1 se puede volver a aplicar igual.

### Unirse y recuperar desde la nube (paso 8.8, hecho en v1.20.6; detalle en §4.3a)
- **"Unirse a una nube existente"**, para un equipo con la base intacta (sin pacientes, consultas, citas, ventas, movimientos, lotes, productos, plantillas ni reglas de dosis, y sin más catálogo del que siembra): respalda, borra el catálogo local sin lápidas, pone `CloudJoined` y el `clinicId` en `DbMeta`, baja todo sin omitir sus propias filas y lleva el cursor al `Seq` actual. `CloudJoined` apaga toda siembra para siempre. Si la base no está intacta, ofrece "Reemplazar con la nube" (teclear CONFIRMAR, con respaldo antes).
- **"Recuperar desde la nube"** es la misma descarga, a una base vacía: una PC nueva o descompuesta. Después se marca "Esta PC reemplaza a la principal" (paso 8.10).
- Los pacientes demo solo salen del asistente, y el catálogo que siembra una base nueva lleva `SyncId` fijos (UUIDv5), así que dos siembras no se duplican.

### Reglas comunes
- El consecutivo de cada serie EXP y TKT vive en `FolioCounters`, en Turso, y "Reemplazar la nube" no lo toca. El último número de cada serie **con letra** (los folios hechos sin conexión) se guarda **fuera de la base** (`folios.json` en `%LocalAppData%\TPVC`; en Android, un archivo que no se borra al volver a bajar todo; paso 8.16). Así restaurar un respaldo o reemplazar la nube no repite un folio ya impreso.
- Las operaciones que reescriben la base o la nube (pasos 8.5, 8.8, 8.9 y 8.20) hacen un respaldo antes. El del 8.5 (desde v1.20.3) se hace al instalar los triggers por primera vez en una base con datos: `backups/consultorio-…-antes-registro-de-cambios.db`. Los del 8.8 (desde v1.20.6): `…-antes-unirse-a-la-nube.db` y `…-antes-reemplazar-con-la-nube.db`.
- El membrete viaja en `ClinicAssets` (paso 8.12). Las preferencias de cada equipo (`HideConsultationTimer`) no se sincronizan.
