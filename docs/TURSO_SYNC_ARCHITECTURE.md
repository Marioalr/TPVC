# Arquitectura de Sincronización Turso Cloud (TPVC)

## 1. Resumen Ejecutivo
TPVC es **local-first (offline-first)**: la base de la PC (`consultorio.db`) es la que se usa en el consultorio y funciona al 100% sin internet. Hoy (v1.19.0) la PC sincroniza en los dos sentidos con **Turso Cloud (libSQL)**: unos segundos después de cada guardado sube sus cambios y descarga los de otros equipos, así que dos PC pueden compartir la misma nube.

**Plan Fase 8 (decidido el 2026-10-09; todavía no implementado, ver `docs/PLAN_FASE8_VARIOS_EQUIPOS.md`):**
- Los tres equipos (la PC, una tablet y un celular Android) sincronizan por Turso con este protocolo v2 (§4), cada uno sobre su propia base local. La tablet sube y baja; el celular solo baja.
- La app Android es Kotlin nativo: implementa por su cuenta el motor v2 y el contrato de la consulta entre equipos (§4.5), y los dos lados se comparan con los archivos de `contracts/`.
- La PC es la única que crea o migra el esquema remoto (§4.2) y la única que vincula y revoca equipos (§5).
- El borrador del mismo día, con la PC como servidor de la red local y Turso solo como respaldo, se descartó (plan §2.6).

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

La app todavía muestra los límites viejos: 1,000 millones de lecturas y 25 millones de escrituras en `TursoQuotaSummary` (`Models/TursoConfig.cs`), y además 9 GB y la leyenda "consume menos del 0.05%" en `TursoSyncViewModel.RefreshQuotaDisplay`. Se corrigen en el paso 8.2.

### Uso estimado (plan §3.2)
Supuestos: 30 días al mes; la PC abierta 10 h al día (8 en Pacientes); la tablet al frente 6 h (5 en pantallas de pacientes); el celular 0.5 h; ~150 sincronizaciones al día por guardados y ~400 filas cambiadas al día.

| Concepto | Hoy (v1.19.0, solo la PC) | Hoy, si los tres equipos hicieran lo mismo | Fase 8 (tres equipos, plan) | Proporción del plan gratuito (Fase 8) |
|---|---|---|---|---|
| Peticiones al día | ~8,700, más del 90% de la revisión de consultas de cada 4 s y el latido de cada 30 s (§4.5) | ~23,000 | ~12,800 (PC ~7.9k, tablet ~4.9k, celular ~30) | El plan no limita peticiones |
| Filas leídas al mes | 0.4M–4.2M | hasta ~12M | 2M–4.5M: cada revisión lee 4–11 filas (con el índice parcial de F2) y hay ~600 bajadas al día de ~25 filas | 0.4–0.9% de 500M |
| Filas escritas al mes | ~45k (el latido es 1,200 al día) | ~120k | ~50k: al día, 960 latidos (cada 60 s, solo PC y tablet), ~400 filas de datos y ~300 `LastPushUtc` | ~0.5% de 10M; hasta ~1.5% si Turso cuenta las entradas de índice (V10) |
| Peor caso: PC y tablet abiertas 24 h en Pacientes | — | — | ~45k peticiones al día; ~7M lecturas y ~120k escrituras al mes | 1.4% y 1.2% |
| Almacenamiento | Decenas de MB al año | Igual | Igual, más ~1 MB del membrete (`ClinicAssets`) | < 1% de 5 GB |
| Bases | 1 | 1 | 2: la del consultorio y la de vinculación, solas en su grupo (§6) | 2 de 100 |

**Costos de una sola vez (plan):**
- Vincular un equipo o hacer una «Sincronización completa» lee todo una vez: ~20–30k filas después del paso 8.6. Hoy cuesta ~N²/1000 por tabla (F1).
- La primera subida con el cursor nuevo (8.5) y «Reemplazar la nube» (8.9) escriben ~20k filas (0.2% del mes) en el peor caso. Reemplazar además lee la nube una vez para guardar lo que se perdería.
- La verificación de la primera subida completa (8.7) lee ~20k filas una vez.
- Cada `ALTER` de 8.19, 8.20, 8.22 y 8.23 cuesta ~20k lecturas y hasta ~20k escrituras; el índice parcial de 8.24 recorre `ConsultationHandoffs` una vez. Todo junto queda abajo del 1–2% de un mes.

**Dos hallazgos (plan §3.1):**
- **F1 · La descarga completa lee de más.** Con la consulta de página de hoy (`TursoSyncService.ReadPagesAsync`), SQLite sin `ANALYZE` (como una base nueva de Turso) ordena todo lo que queda de la tabla en cada página de 500: una tabla de 20k filas cuesta ~275k lecturas en lugar de ~20k. Las descargas incrementales no se ven afectadas. Se corrige anteponiendo `"SyncedAtUtc" >= ?` (paso 8.6). Falta confirmarlo en Turso real (V15).
- **F2 · La revisión de consultas lee de más.** `IX_ConsultationHandoffs_To (ToDeviceId, State)` solo se usa por `ToDeviceId`, así que cada revisión recorre todas las filas que se conservan para ese equipo. Un índice parcial lo baja a ~0 filas (paso 8.24, §4.5).

**Freno de consumo (paso 8.2).** El riesgo no está en el uso normal (margen de ~100×) sino en un ciclo descontrolado: repetir una descarga completa cada 4 s gasta ~540M lecturas en un día, se acaba el mes de los tres equipos y Turso responde `BLOCKED` a todo. Por eso la PC (y después la app) hará como máximo 2 descargas completas al día, con un tope diario de 3M lecturas y 30k escrituras estimadas; al pasarlo se pausa la sincronización automática ("Sincronización pausada: consumo anormal", con *Reanudar*). `BLOCKED` se traducirá a "Se agotó la cuota mensual de Turso", y la sincronización automática esperará 1 h.

> El contador de la pantalla *Nube y dispositivos* (`RecordUsage`) es una estimación. La cifra real está en el panel de Turso.

---

## 3. Equipos y permisos

Tres equipos: la PC con Windows, una tablet Android y un celular Android. El 99% del tiempo están en la misma red, pero la red local no se usa como canal: todo pasa por Turso. Hoy (v1.19.0) solo existe la app de la PC. Topología del plan Fase 8:

```
 ┌──────────────────────┐    ┌──────────────────────┐    ┌──────────────────────┐
 │ PC Windows (TPVC.exe)│    │ Tablet (Kotlin)      │    │ Celular (Kotlin)     │
 │ consultorio.db       │    │ Room + SQLCipher     │    │ Room + SQLCipher     │
 │ motor v2 (C#)        │    │ motor v2 (Kotlin)    │    │ motor v2, solo baja  │
 └──────────┬───────────┘    └──────────┬───────────┘    └──────────┬───────────┘
            │ sube y baja               │ sube y baja               │ solo baja
            ▼                           ▼                           ▼
 ┌──────────────────────────────────────────────────────────────────────────────┐
 │ Turso · base del consultorio (libSQL, Hrana v2 por HTTPS): 13 tablas         │
 │ sincronizadas, SyncTombstones, ClinicProfiles, ClinicAssets,                 │
 │ RegisteredDevices, DeviceRegistry, ConsultationHandoffs, _tpvc_meta          │
 ├──────────────────────────────────────────────────────────────────────────────┤
 │ Turso · base de vinculación (mismo grupo): DevicePairings, sin expedientes   │
 └──────────────────────────────────────────────────────────────────────────────┘
```

Papel de cada equipo (plan §2.2):
- **PC** (`WindowsDesktop`): escribe todo. Es la única que crea o migra el esquema remoto, escribe `DeviceRegistry` y `_tpvc_meta`, vincula y revoca equipos, y edita catálogo, inventario, perfil y membrete.
- **Tablet** (`AndroidTablet`): escribe pacientes, notas, citas, ventas, movimientos, lápidas, su latido y `ConsultationHandoffs`. El inventario lo ve, no lo edita.
- **Celular** (`AndroidMobileAdmin`): solo baja. No escribe nada, ni el latido, y no envía ni recibe consultas.

### Matriz de permisos (`DevicePermissions`, `Models/TursoConfig.cs`)

| Permiso | PC (`WindowsDesktop`) | Tablet (`AndroidTablet`) | Celular (`AndroidMobileAdmin`) | Quién lo hará cumplir (plan) |
|---|:---:|:---:|:---:|---|
| `CanManageClinicalRecords`: editar expedientes y consulta | sí | sí | no (pregunta 3 del plan) | Turso en el celular (token de solo lectura); la app en la tablet |
| `CanPrescribeMedications`: recetar | sí | sí | no | Igual |
| `CanProcessSales`: cobrar y vender | sí | sí | no | Igual |
| `CanManageInventory`: editar inventario y catálogo | sí | no (solo ve) | no (solo ve) | Turso si V1 (§6); si no, la app |
| `CanViewDashboard`: resumen | sí | sí | sí | — |
| `CanViewCashCut`: corte de caja | sí | no (oculto) | sí | La app |
| `CanViewStockAlerts`: alertas de existencias y compra sugerida | sí | sí | sí | — |
| `CanConfigureSystem`: configuración, Excel, respaldos, Turso, vincular, perfil y membrete | sí | no | no | Turso si V1 (sin `schema_*`); si no, la app |
| `CanViewClinicalRecords`: ver expedientes (nuevo, paso 8.10) | sí | sí | sí | La app |
| `CanShareDocuments`: compartir receta y ticket (nuevo, paso 8.10) | sí | sí | sí | La app |

- Hoy los permisos son solo informativos: en la app solo se usa `GetRoleDescription`, dentro del paquete de vinculación (§5). El rol lo elige cada equipo en un selector de *Nube y dispositivos* y se escribe en `RegisteredDevices`.
- **Plan Fase 8:** el rol de la PC queda fijo en `WindowsDesktop` (paso 8.2). El de la tablet y el celular sale de `DeviceRegistry` (paso 8.10, §4.2), la autoridad sobre el rol, el código de folios, la revocación y la renovación del token de cada equipo. Solo la PC la escribe y sus filas nunca se borran. La app de Kotlin oculta pantallas según el rol que lee ahí.
- Turso hace cumplir el token de solo lectura del celular. Los límites de la tablet (sin esquema, sin catálogo ni configuración) solo los hace cumplir Turso si acepta permisos por tabla (V1); si no, los aplica la app (paso 8.14). La tablet necesita leer todas las tablas sincronizadas (sus propios tickets están en `Sales`), así que el corte de caja solo se oculta en su interfaz.
- La consulta entre equipos es solo para la PC y la tablet: `GetLinkedDevicesAsync` ya excluye el celular.
- Pregunta 3 del plan, solo para el celular: si solo consulta, su token es de solo lectura y Turso lo hace cumplir. Si debe agendar, necesita escribir citas y expedientes (agendar también escribe en el expediente) y se pierde esa garantía.

---

## 4. Protocolo de Sincronización (versión 2, desde TPVC v1.9.0)

> Esta sección es el contrato que debe implementar **cualquier** cliente: la PC hoy y, con la Fase 8, la app Android en Kotlin.
> Implementación de referencia: `Services/TursoSyncService.cs` + `Services/TursoClient.cs`.
> Pruebas de punta a punta con un servidor Hrana simulado: `tests/TPVC.Tests/TursoSyncEndToEndTests.cs`.
>
> **Plan Fase 8:** el contrato de los dos motores, el de la PC (C#) y el de Kotlin, se fija en `contracts/sync/schema.json` (paso 8.3), generado desde `GetTableMaps`, con vectores en `contracts/golden/` e intercambios grabados en `contracts/hrana/` (pasos 8.13 y 8.25). Los cambios son aditivos. Cuando cambie el significado de las existencias (paso 8.20), la nube se marca `protocol = 3` para que un equipo que no conoce las reglas nuevas se detenga.

### 4.1 Transporte
- `POST {url}/v2/pipeline` con `Authorization: Bearer <token>` y cuerpo `{"requests":[{"type":"execute","stmt":{...}}, ..., {"type":"close"}]}`.
- Valores (`args`) según la especificación Hrana:
  - `{"type":"integer","value":"42"}` → **el entero va como texto**. Enviarlo como número JSON hace que Turso responda **HTTP 400** a todo el pipeline (era el error de v1.8.x).
  - `{"type":"float","value":1.5}`, `{"type":"text","value":"..."}`, `{"type":"null"}`.
- Cada sentencia de un pipeline se confirma por separado (no hay transacción, ni dentro de un lote ni entre lotes); todas las escrituras son *upserts* idempotentes, así que reintentar un lote es seguro. Si una petición con `BEGIN`/`COMMIT` es atómica en Turso se comprueba antes del paso 8.6 (V14, §6).

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
- **Tablas nuevas:** `DeviceRegistry` (paso 8.10; solo la PC; rol, código de folios, revocación y renovación de cada equipo), `ClinicAssets` (escudo y encabezado del membrete, paso 8.12) y `DevicePairings` en una **base de vinculación aparte**, sin datos clínicos (paso 8.15, §5).
- **Columnas nuevas, todas aceptan nulo:**
  - En tablas que no salen del modelo de EF, con `ALTER` explícito incluido en la huella: `RegisteredDevices.Platform`, `SyncProtocol`, `HandoffProtocol`, `ContractRevision`, `LastPushUtc`, `PendingCount`, `OldestPendingUtc` e `InstanceNonce` (8.10); `ClinicProfiles.CrestImageHash` y `HeaderImageHash` (8.12).
  - En tablas sincronizadas: `InventoryMovements.Delta` y `SaleItemSyncId`, `Sales.LedgerVersion` y `ProductBatches.ClosedAtUtc` (8.19); `InventoryMovements.CountedQuantity` (8.20); `Patients.FieldStampsJson` (8.22); `Patients.MergedIntoSyncId` (8.23). Cada `ALTER` recorre su tabla una vez (§2).
- **Claves de `_tpvc_meta`** (solo las escribe la PC): `clinicId` (8.7), `generation` y `generationReady` (8.7, 8.9; §7), `protocol` y `minClientProtocol` (2 desde 8.7, 3 desde 8.20), `contract` (revisión aditiva de `schema.json`, 8.3), `manifest` (8.7), `fullUploadAtUtc` (fecha de la primera subida completa verificada, 8.7) y `primaryDeviceId` (la PC principal, 8.10).
- **Fechas:** `schema.json` marca el `timeKind` de cada columna (8.3): `utc` (`UpdatedAtUtc`, `SyncedAtUtc`), `clinicLocal` (fechas del negocio en la hora del consultorio, `America/Mexico_City`, que hoy salen de `DateTime.Now`) o `dateOnly` (`BirthDate`, fecha de la cita).

### 4.3 Ciclo de sincronización
1. **Esquema:** se compara la huella en `_tpvc_meta`; solo si cambió se crean tablas / columnas nuevas (`ALTER TABLE ADD COLUMN`).
2. **Subida:** filas locales con `UpdatedAtUtc` posterior a la última subida (sin tope superior desde v1.17: una fila guardada durante la subida o con marca adelantada también sube; la nube descarta lo que no sea más nuevo), en orden de dependencias (categorías → productos → lotes → ...), en lotes de 80 sentencias:
   `INSERT ... SELECT ... WHERE NOT EXISTS (borrado en SyncTombstones con DeletedAtUtc >= UpdatedAtUtc) ON CONFLICT(SyncId) DO UPDATE SET ... WHERE excluded.UpdatedAtUtc > tabla.UpdatedAtUtc` (gana la versión más nueva y un registro ya borrado en otro equipo no se vuelve a crear). El borrado se reescribe solo si es más nuevo.
   Borrados: upsert en `SyncTombstones` + `DELETE ... WHERE SyncId = ? AND UpdatedAtUtc <= fechaDelBorrado`.
3. **Descarga:** se toma la marca del servidor (`now - 5 s`) y se leen filas con `SyncedAtUtc` mayor a la última marca guardada, en páginas de 500, **excluyendo las de este mismo dispositivo**. Las filas se aplican sin re-estampar `UpdatedAtUtc` (bandera `AppDbContext.ApplyingRemoteChanges`), traduciendo SyncId → Id local.
4. **Perfil del consultorio:** gana el `UpdatedAtUtc` más reciente.
5. Se guardan `LastPushedLocalUtc` y `LastPulledServerWatermark`.

Hoy, una fila cuya referencia obligatoria no tiene `SyncId` se omite en la subida con solo un aviso en la bitácora y no se reintenta. En la descarga pasa lo mismo con la fila que llega sin su principal obligatorio; una referencia **opcional** cuyo principal todavía no existe en este equipo se guarda en nulo y no se corrige después (la marca local ya es igual a la remota), y un borrado remoto que no se puede aplicar solo se anota. Y si el reloj de la PC se atrasa después de sincronizar, lo que se guarde antes de la siguiente sincronización queda con una marca anterior a `LastPushedLocalUtc` y no sube nunca (esa sincronización mueve el cursor a la hora atrasada); solo sube si se vuelve a editar o con «Sincronización completa».

**Plan Fase 8 (pasos 8.4–8.7):**
- **Filas apartadas** (`SyncParkedRows`, local, paso 8.4): lo que hoy se omite se aparta, se reintenta en cada sincronización y se cuenta en pantalla ("N cambios sin sincronizar"). Incluye la fila que trae una referencia opcional que todavía no existe; se aplica en nulo solo si su principal tiene lápida.
- **Cursor por secuencia** (paso 8.5): `SyncChangeLog`, un registro de cambios que llenan triggers de SQLite y que también ve los borrados en cascada. La subida va por `Seq > LastPushedSeq`, guardado en `DbMeta` junto a un `DbInstanceId`, y ya no depende del reloj. Las lápidas salen de los mismos triggers. La primera vez sube todo una vez, de forma idempotente.
- **Descarga eficiente** (paso 8.6): consulta con `"SyncedAtUtc" >= ?` adelante (F1); bajada incremental en una sola petición; descarga completa con una tabla por petición; página de 100 en Patients y PatientConsultations y de 500 en las demás, que se reduce sola; **el cursor de cada tabla se guarda después de cada página**, así un corte continúa donde iba. Si un upsert afecta 0 filas, se vuelve a leer la fila del servidor y se aplica aunque las marcas empaten.
- **Hora del servidor** (paso 8.7): la primera instrucción de cada sincronización regresa la hora del servidor, `generation`, `protocol`, `minClientProtocol` y `clinicId`. Las marcas locales salen de `max(UtcNow + desfase, guardada + 1 ms)`, con aviso si el desfase pasa de 2 min, y lo pendiente con marca en el futuro se vuelve a marcar antes de subir. La «Sincronización completa» da un reporte (filas apartadas, conteo local contra remoto, tablas `legacy_v1_*`) y solo si sale limpio escribe `fullUploadAtUtc`, sin el cual no se puede vincular (A6).

### 4.4 Garantías
- **Sin transacción:** `TursoClient.ExecuteBatchAsync` no es transaccional aunque su comentario lo diga (el comentario se corrige en el paso 8.2): cada sentencia del pipeline se confirma sola. La subida es idempotente, así que un lote que falla a medias se reintenta completo sin dañar nada.
- **Sin ciclos:** lo recibido conserva su marca y no vuelve a subirse ni a descargarse.
- **Relojes desfasados:** las descargas usan el reloj del servidor, no el del dispositivo. Desde v1.17 un cambio local siempre lleva una marca posterior a la que ya tenía la fila (`max(ahora, marca guardada + 1 ms)`), también en los borrados: una edición hecha después de recibir una fila de un equipo con el reloj adelantado ya no la descarta la nube.
- **Folios duplicados** (dos equipos crean `EXP-2026-005` sin conexión): el registro recibido se guarda como `EXP-2026-005-R1` en lugar de bloquear la sincronización. El `-R` solo existe en el equipo que recibe y no se sube, así que el mismo registro queda con dos folios. **Plan Fase 8 (paso 8.17, pregunta 1):** cada equipo lleva su código en el folio (`EXP-2026-T1-001`, `TKT-20261009-T1-0001`; la PC principal no lleva código y sus folios no cambian), `RecordNumber` y `Folio` pasan de índice único a índice normal y el `-R` deja de aplicarse. Las filas viejas con `-R` se quedan, y un folio repetido aparece en un aviso de la PC.
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
- La tablet implementará este mismo contrato en Kotlin (app K8): mismas instrucciones y en el mismo orden. El celular no envía ni recibe.
- **Compatibilidad** (paso 8.10): un equipo puede recibir si `RegisteredDevices.HandoffProtocol >= 1`. Si esa columna está vacía (PC v1.17–v1.19), sigue la regla de `AppVersion`. Así la app Android puede numerar sus versiones desde 1.0.0. Los equipos revocados en `DeviceRegistry` se excluyen.
- **Marcas por campo en la copia** (paso 8.22): hoy aceptar solo compara el `UpdatedAtUtc` de la fila del paciente. La copia llevará además las marcas por campo del paciente y la de la nota, y quien recibe exigirá que cada marca local sea mayor o igual ("Intente de nuevo en unos segundos"). Es un campo aditivo: `PayloadVersion` sigue en 1.
- **Cadencia** (paso 8.24, `Services/SyncScheduler.cs`): la misma revisión lee `RegisteredDevices.LastPushUtc` de los otros equipos y, si es posterior a la última descarga, sincroniza en ese momento; más una descarga de seguridad cada 10 min. El latido pasa a 60 s. La revisión rápida (4 s, en Pacientes) solo corre con un envío pendiente o con un equipo capaz de recibir consultas visto en los últimos 30 días; si no, cada 30 s.
- **Índice parcial** (F2, paso 8.24): `(ToDeviceId) WHERE State='enviada' OR (State='aceptada' AND Payload<>'')`. SQLite solo lo usa si el `WHERE` repite esa condición, así que el SQL de la revisión va literal en `contracts/hrana/`.
- Solo la PC corre el `CREATE` de esta tabla y su limpieza (paso 8.10).

---

## 5. Vinculación de la tablet y el celular

### Hoy (v1.19.0): paquete en claro (se retira en el paso 8.2)
En *Configuración → Nube y dispositivos → Vincular tablet y celular*, "Generar código" abre una ventana con un JSON **en texto claro** (`TursoSettingsService.GenerateAndroidPairingPayload`) para copiar y pegar: URL de la base, **token completo de Turso**, rol, descripción del rol, consultorio y médico. No hay QR ni cifrado, no vence, y todavía no existe una app que lo lea. "Copiar código" lo pone en el portapapeles, donde puede quedar en el historial de Windows.

Quien tenga ese texto puede leer y escribir toda la base de la nube, y el rol no se aplica en ningún lado. **No debe usarse para vincular.** El paso 8.2 retira `GenerateAndroidPairingPayload`, su ventana (`OpenPairingModal`) y `CopyPairingPayload`.

### Plan Fase 8: QR con código y base de vinculación (paso 8.15; detalle en el plan §4.4 y §4.5)
La PC solo ofrece vincular si su sincronización está sana, existe `_tpvc_meta.fullUploadAtUtc` (paso 8.7) y hay menos de 3 equipos activos.
1. **El médico la inicia** en *Nube y dispositivos → Equipos vinculados → Vincular tablet o celular*. La PC crea un `PairingId` aleatorio, reserva un código de equipo (`T1`, `C1`…: el mayor de `DeviceRegistry` + 1), crea un token de 15 min de la base de vinculación (nivel A, §6) o toma el fijo (nivel C), e inserta en `DevicePairings` una fila `State='esperando'` que vence en 15 min, con la hora del servidor.
2. **El QR** es `tpvc-pair:1:` + base64url(sal | nonce | AES-256-GCM(k, JSON)), con k = PBKDF2-SHA256(código de 10 dígitos, sal, 200k iteraciones). El JSON lleva la URL y el token de la base de vinculación, el `PairingId`, el `clinicId`, el rol, el código de equipo, el nombre del consultorio y el vencimiento. **Ningún token de la base del consultorio.** El código de 10 dígitos aparece debajo del QR, en dos grupos de cinco. El mismo texto pegado sirve para que otra PC con Windows se una ("Unirse con código", código `P2`…).
3. **El equipo reclama la fila** (se niega si no tiene bloqueo de pantalla seguro): lee el QR, pide el código, genera su propio `DeviceId` y un par de llaves P-256, y corre en la base de vinculación `UPDATE DevicePairings SET State='reclamada', … WHERE PairingId=? AND State='esperando' AND ExpiresAtUtc > <hora del servidor>`. Una fila afectada = ganó; cero = "Este código ya se usó". Muestra un código de confirmación de 6 dígitos, sacado de SHA-256(`PairingId` ‖ llave pública).
4. **El médico confirma en la PC**, que pregunta "¿Vincular «Galaxy Tab S9» como Tablet? Código 482 913". Con *No*, la fila queda `cancelada`.
5. **Con *Sí*, la PC** crea el token del equipo (nivel A) o toma el del rol (nivel C), inserta la fila en `DeviceRegistry` con la llave pública del equipo y deja la credencial (URL de la base del consultorio, token, vencimiento, rol, código y `clinicId`) **sellada para esa llave**: ECDH P-256, HKDF-SHA256 y AES-256-GCM.
6. **El equipo termina:** descifra la credencial, la guarda envuelta con el Android Keystore y cierra la fila; la PC la borra.

Quien fotografía el QR, aun con el código, solo obtiene la base de vinculación, sin datos clínicos (V13). A lo más puede competir por el reclamo, y el médico vería un nombre o un código de confirmación extraño. El token de datos nunca está en el QR.

### Plan Fase 8: renovación, quitar y equipo perdido (paso 8.16; plan §4.7)
- **Renovación** (nivel A): cuando al token de un equipo le quedan menos de 30 días, la PC crea uno nuevo, lo sella con `DeviceRegistry.PublicKey` y lo deja en `DeviceRegistry.RenewedCredential`. El equipo lo recoge en su siguiente sincronización. En el nivel C los tokens no vencen.
- **Quitar:** la PC muestra antes los cambios pendientes del equipo (`PendingCount` y `OldestPendingUtc` de su latido). El equipo sube lo pendiente, o lo exporta a un archivo, y luego borra su base, su credencial y sus borradores.
- **Perdí este equipo:** revocar (`RevokedAtUtc`, motivo "perdido") y "Cortar acceso ahora", que rota las llaves de **todo el grupo** porque Turso no revoca tokens uno por uno. Los demás equipos reciben 401 y muestran "Vuelva a vincular" sin perder sus cambios pendientes. Un token vencido nunca borra nada.

---

## 6. Guía Rápida de Configuración en Turso

> **El token de la PC no sale de la PC.** Se pega solo en TPVC de Windows, que lo guarda cifrado con DPAPI en `%LocalAppData%\TPVC\turso_config.json`. No lo copie a la tablet ni al celular (el paquete de vinculación de hoy lo lleva en claro, ver §5). Con la Fase 8 cada equipo recibirá su propio token, sellado para su llave al vincularse (§5), nunca en el QR.

**Plan Fase 8 (credenciales, plan §4.3; pregunta 4 del plan):**
- La base del consultorio y la de vinculación van **solas en su grupo** de Turso: la única forma de cortar un token es rotar las llaves del grupo, y eso corta a todas las bases del grupo.
- **Nivel A** (recomendado): un token de la API de la plataforma limitado al grupo, solo con `db:mint-token` y `db:rotate-creds`, cifrado con DPAPI en la PC. Con él la PC crea su propio token `full-access` de 90 días, el de 15 min del QR y el de cada equipo (180 días; `read-only` en el celular), y los renueva.
- **Nivel C** (si no hay token de la API): tokens creados a mano en el panel, sin vencimiento: el de la PC (como hoy), uno por rol y uno fijo de la base de vinculación. Solo se cortan rotando el grupo.
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

# 6. (Plan Fase 8, paso 8.15; todavía no se usa) Base de vinculación, en el mismo grupo que la del consultorio
turso db create tpvc-vinculacion --group <grupo de tpvc-clinica>
```

### Opción B: Mediante Consola Web de Turso
1. Ingresar a [https://turso.tech](https://turso.tech) y registrarse gratis con GitHub o Google.
2. Hacer clic en **"Create database"**.
3. Nombrar la base de datos (ej. `tpvc-clinica`) y elegir la región más cercana (ej. `qro` Querétaro, México o `dfw` Dallas).
4. Copiar la **Database URL** y generar un **Auth Token** desde la pestaña *Settings > Tokens*.
5. Pegar ambos valores en TPVC Windows en **Configuración > Nube y dispositivos**.
6. Hacer clic en **"Probar conexión"** y posteriormente en **"Sincronización completa"**.
7. (Plan Fase 8, paso 8.15; todavía no se usa) Crear en el mismo grupo una segunda base, `tpvc-vinculacion`, para la vinculación por QR. La tabla `DevicePairings` la crea la PC.

### Comprobaciones en una base de prueba real (plan §4.2)
Se hacen antes del paso indicado, siempre en un grupo o una cuenta de prueba. El resultado se anota aquí.

| # | Qué se comprueba | Antes de | Resultado |
|---|---|---|---|
| V1 | Si la API de la plataforma acepta permisos por tabla, y con qué campo | 8.14 | Pendiente |
| V2 | Si en el plan gratuito funciona un token de grupo con solo `db:mint-token` y `db:rotate-creds` (nivel A o C) | 8.14 | Pendiente |
| V3 | Qué responde Hrana a una instrucción prohibida y a un token vencido o rotado; si un token de solo lectura rechaza `CREATE TABLE IF NOT EXISTS` sobre una tabla que ya existe | 8.14 | Pendiente |
| V4 | Cuánto dura la interrupción al rotar | 8.16 | Pendiente |
| V5 | El largo real de un token (tamaño del QR) | 8.15 | Pendiente |
| V6 | Si el cliente puede leer el `exp` del JWT (vencido o revocado) | 8.14 | Pendiente |
| V7 | Texto y estado HTTP de `BLOCKED` | 8.2 | Pendiente |
| V8 | Cuántos grupos permite el plan gratuito | 8.14 | Pendiente |
| V9 | Si el plan gratuito archiva una base sin uso y qué error da | 8.2 | Pendiente |
| V10 | Si las entradas de índice cuentan como filas escritas | 8.2 | Pendiente |
| V11 | Si el plan gratuito tiene bitácora de auditoría, y de cuántos días | 8.16 | Pendiente |
| V12 | El tope de tamaño de una respuesta Hrana | 8.6 | Pendiente |
| V13 | Que un token de la base de vinculación no abra la base del consultorio del mismo grupo | 8.15 | Pendiente |
| V14 | Si una petición `batch` de Hrana con `BEGIN`/`COMMIT` es atómica en Turso | 8.6 | Pendiente |
| V15 | `EXPLAIN QUERY PLAN` de F1 y F2 en Turso real (libSQL no es SQLite 3.53) | 8.6 | Pendiente |

---

## 7. Respaldo y recuperación

**Hoy (v1.19.0).** Al restaurar un respaldo local se borran los cursores (`LastPushedLocalUtc`, `LastPulledServerWatermark`; `DataImportService`): la siguiente sincronización sube todo, pero la nube conserva las filas más nuevas que el respaldo (gana el `UpdatedAtUtc` mayor) y la descarga omite las que escribió esta misma PC. Con una sola PC, la base local queda como el respaldo y la nube conserva lo posterior: las dos copias quedan distintas, y una «Sincronización completa» regresaría esas filas y desharía la restauración. Lo que escribió otra PC sí regresa en la siguiente descarga (riesgo H6 del plan).

La copia de Turso no es un historial: el plan gratuito solo restaura a un punto de hasta 1 día atrás. El historial son los respaldos locales diarios (`BackupService`, se conservan 14).

Lo que sigue es el plan Fase 8 y **todavía no está implementado**.

### Generación de la nube (pasos 8.7 y 8.9)
- `_tpvc_meta` guarda `clinicId` (se escribe una sola vez), `generation` (cambia al reemplazar la nube con un respaldo) y `generationReady` (la generación cuya subida completa ya terminó). Cada equipo guarda los suyos en `DbMeta`.
- **Cerca de generación:** cada upsert, lápida y `DELETE` de la subida lleva `AND (SELECT "Value" FROM _tpvc_meta WHERE "Key"='generation') = ?`, y cada petición termina leyendo la generación. Si cambió, el equipo se detiene. Sin esto, los trozos de 80 instrucciones que otro equipo seguía subiendo caerían en la nube restaurada.
- Una base con datos propios no sincroniza con una nube de otro `clinicId`: solo ofrece "Unirse", "Reemplazar con la nube" o "Esta PC ya usaba esta nube" (paso 8.7).

### Restaurar con la nube activa (paso 8.9)
La restauración (`DataImportService`) pregunta:
- **"Reemplazar la nube con este respaldo":**
  1. Antes de borrar, baja de la nube las filas que no están en el respaldo o son más nuevas, muestra los conteos por tabla ("se perderán 12 notas y 30 ventas de la tablet") y las guarda en un archivo `.db`.
  2. Guarda "reemplazo en curso" en `DbMeta`; si TPVC se cierra, lo continúa al abrir.
  3. Escribe la generación nueva, borra las tablas sincronizadas y las lápidas de la nube, y sube todo (~0.2% del mes) con el reporte del paso 8.7.
  4. Al final escribe `generationReady` con la generación nueva y un `fullUploadAtUtc` nuevo.
- **"Desconectar esta PC de la nube".**

**Los demás equipos**, al ver otra generación, dejan de subir y guardan lo que no habían subido. Solo cuando `generationReady` es igual a la generación nueva borran y vuelven a bajar todo, una sola vez, y nadie baja una nube a medio reemplazar. Después ofrecen "Volver a aplicar N cambios", que inserta esas filas con marca nueva. El archivo del paso 1 se puede volver a aplicar igual.

### Unirse y recuperar desde la nube (paso 8.8)
- **"Unirse a una nube existente"**, para un equipo con la base intacta (sin pacientes, consultas, citas, ventas, movimientos, lotes, productos, plantillas ni reglas de dosis, y sin más catálogo del que siembra): respalda, borra el catálogo local sin lápidas, pone `CloudJoined` y el `clinicId` en `DbMeta`, baja todo sin omitir sus propias filas y lleva el cursor al `Seq` actual. `CloudJoined` apaga toda siembra para siempre. Si la base no está intacta, ofrece "Reemplazar con la nube" (teclear CONFIRMAR, con respaldo antes).
- **"Recuperar desde la nube"** es la misma descarga, a una base vacía: una PC nueva o descompuesta. Después se marca "Esta PC reemplaza a la principal" (paso 8.10).
- Los pacientes demo solo salen del asistente, y el catálogo que siembra una base nueva lleva `SyncId` fijos (UUIDv5), así que dos siembras no se duplican.

### Reglas comunes
- El último número de cada serie de folios EXP y TKT se guarda **fuera de la base** (`folios.json` en `%LocalAppData%\TPVC`, paso 8.17), para que restaurar un respaldo o reemplazar la nube no repita un folio ya impreso.
- Las operaciones que reescriben la base o la nube (pasos 8.5, 8.8, 8.9 y 8.20) hacen un respaldo antes.
- El membrete viaja en `ClinicAssets` (paso 8.12). Las preferencias de cada equipo (`HideConsultationTimer`) no se sincronizan.
