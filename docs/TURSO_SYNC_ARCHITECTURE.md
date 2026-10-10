# Arquitectura de Sincronización Turso Cloud (TPVC)

## 1. Resumen Ejecutivo
TPVC es **local-first (offline-first)**: la base de la PC (`consultorio.db`) es la que se usa en el consultorio y funciona al 100% sin internet. Hoy (v1.19.0) la PC sincroniza en los dos sentidos con **Turso Cloud (libSQL)**: unos segundos después de cada guardado sube sus cambios y descarga los de otros equipos, así que dos PC pueden compartir la misma nube.

**Plan Fase 8 (decidido el 2026-10-09; todavía no implementado, ver `docs/PLAN_FASE8_VARIOS_EQUIPOS.md`):**
- Turso queda solo como **respaldo y recuperación** de la PC (§7). **Solo la PC habla con Turso.**
- La tablet y el celular (app Android en Kotlin nativo) no tendrán token de Turso: le pedirán todo a la PC por HTTPS en la red del consultorio, y la PC será el único equipo que escribe (plan §2 y §3).
- El modo actual de compartir la nube entre dos PC queda congelado como `SharedPcs`: sin trabajo nuevo y marcado como no recomendado (paso 8.2).

---

## 2. Plan gratuito de Turso: límites y uso estimado

Límites vigentes del plan gratuito (<https://turso.tech/pricing>, consultado el 2026-10-09):
- **Filas leídas al mes:** 500 millones.
- **Filas escritas al mes:** 10 millones.
- **Almacenamiento:** 5 GB.
- **Bases de datos:** 100.
- Además: 3 GB de sincronizaciones de réplicas al mes (TPVC no las usa) y restauración a un punto en el tiempo de hasta 1 día.

La app todavía muestra los límites viejos del plan gratuito en `TursoQuotaSummary` (`Models/TursoConfig.cs`) y en `TursoSyncViewModel.RefreshQuotaDisplay`. Se corrigen en el paso 8.2.

### Uso estimado (plan §2.3)
Supuestos: 10 h abierto, 25 pacientes al día y unos 300–600 registros cambiados al día.

| Concepto | Hoy (v1.19.0, solo la PC) | Fase 8, modo respaldo (plan) | Proporción del plan |
|---|---|---|---|
| Peticiones al día | ~8,700, más del 90% de la revisión de consultas entre equipos y el latido (~23,000 si los tres equipos hicieran lo mismo) | ~50–120 | El plan no limita peticiones |
| Filas escritas al mes | Solo el latido: 26k–36k por equipo, más los datos | ~9k–18k | 0.1–0.2% de 10M |
| Filas leídas al mes | Hasta millones con tres equipos | ~30k–60k (revisiones al insertar), más la verificación a pedido | < 0.02% de 500M |
| Almacenamiento | — | Decenas de MB al año | < 5% de 5 GB a 5 años |
| Bases | 1 | 1 (2 con el paso opcional 8.A) | de 100 |
| Equipos que hablan con Turso | La PC (y la tablet en el diseño anterior) | Solo la PC | — |

**Costos de una sola vez:** la primera subida con el cursor nuevo (paso 8.6) reescribe todo una vez, unas 20k filas (0.2% del mes). Reemplazar la copia de la nube después de restaurar cuesta lo mismo, y recuperar desde la nube lee todo una vez.

> El contador de la pantalla *Nube y dispositivos* (`RecordUsage`) es una estimación. La cifra real está en el panel de Turso.

---

## 3. Equipos y permisos

Tres equipos: la PC con Windows, una tablet Android y un celular Android, el 99% del tiempo en la misma red del consultorio. Hoy (v1.19.0) solo existe la app de la PC. El diseño anterior, con los tres equipos hablando con Turso, queda descartado. Topología del plan Fase 8:

```
 Tablet Android ──┐   HTTPS en la red    ┌───────────────────────────┐  solo subida  ┌─────────────┐
                  ├────────────────────► │ PC Windows (TPVC.exe)     │ ────────────► │ Turso Cloud │
 Celular Android ─┘   del consultorio    │ servidor y único escritor │               │ (respaldo)  │
                                         └───────────────────────────┘               └─────────────┘
```

### Matriz de permisos (`DevicePermissions`, `Models/TursoConfig.cs`)

| Permiso | PC (`WindowsDesktop`) | Tablet (`AndroidTablet`) | Celular (`AndroidMobileAdmin`) |
|---|:---:|:---:|:---:|
| `CanManageClinicalRecords`: editar expedientes y consulta | sí | sí | no |
| `CanPrescribeMedications`: recetar | sí | sí | no |
| `CanProcessSales`: cobrar y vender | sí | sí | no |
| `CanManageInventory`: editar inventario | sí | no (solo ve) | no (solo ve las alertas) |
| `CanViewDashboard`: resumen | sí | sí | sí |
| `CanViewCashCut`: corte de caja | sí | no (pregunta 4 del plan) | sí |
| `CanViewStockAlerts`: alertas de existencias y compra sugerida | sí | sí | sí |
| `CanConfigureSystem`: configuración, Excel, respaldos, Turso y vincular | sí | no | no |
| `CanViewClinicalRecords`: ver expedientes (nuevo, paso 8.17) | sí | sí | sí |
| `CanShareDocuments`: compartir receta y ticket (nuevo, paso 8.17) | sí | sí | sí |

- Si el celular puede agendar o mover citas, o solo verlas, está pendiente (pregunta 4 del plan).
- Si el celular ve todo el inventario o solo las alertas también está por confirmar: en el plan §3.5 las rutas de inventario piden `CanViewStockAlerts`, que el celular tiene (pregunta 4 del plan).
- Hoy los permisos son solo informativos: en la app solo se usa `GetRoleDescription`, dentro del paquete de vinculación (§5). Con la Fase 8 la PC los aplicará en cada ruta: el rol se elige en la PC, vive en `PairedDevices` y nunca se confía en lo que diga el equipo (paso 8.17).

---

## 4. Protocolo de Sincronización (versión 2, desde TPVC v1.9.0)

> Contrato entre TPVC y Turso. Implementación de referencia: `Services/TursoSyncService.cs` + `Services/TursoClient.cs`.
> Pruebas de punta a punta con un servidor Hrana simulado: `tests/TPVC.Tests/TursoSyncEndToEndTests.cs`.
>
> **Plan Fase 8:** el protocolo v2 se queda para el respaldo de la PC (§7) y para el modo `SharedPcs` entre dos PC, que queda congelado. Deja de ser el contrato de la app Android: la app en Kotlin hablará con la PC por la red (plan §3) y no necesita el formato de filas de Turso (`TursoClient.FormatArg`).

### 4.1 Transporte
- `POST {url}/v2/pipeline` con `Authorization: Bearer <token>` y cuerpo `{"requests":[{"type":"execute","stmt":{...}}, ..., {"type":"close"}]}`.
- Valores (`args`) según la especificación Hrana:
  - `{"type":"integer","value":"42"}` → **el entero va como texto**. Enviarlo como número JSON hace que Turso responda **HTTP 400** a todo el pipeline (era el error de v1.8.x).
  - `{"type":"float","value":1.5}`, `{"type":"text","value":"..."}`, `{"type":"null"}`.
- Cada sentencia de un pipeline se confirma por separado (no hay transacción, ni dentro de un lote ni entre lotes); todas las escrituras son *upserts* idempotentes, así que reintentar un lote es seguro.

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
- `RegisteredDevices (DeviceId, DeviceName, DeviceRole, AppVersion, LastSeenUtc)`: `DeviceRole` es el nombre del enum (`WindowsDesktop`, `AndroidTablet`, `AndroidMobileAdmin`) y `LastSeenUtc` la hora **del servidor**. Desde v1.17 cada equipo lo actualiza cada 30 s mientras revisa la tabla de consultas enviadas (y en cada primera sincronización de la sesión). En el modo `Backup` del plan, la PC se registrará una sola vez por arranque y sin latido (paso 8.2).
- Las tablas creadas por v1.8.x (con `Id` entero) se renombran a `legacy_v1_*`; no se borra nada.

### 4.3 Ciclo de sincronización
1. **Esquema:** se compara la huella en `_tpvc_meta`; solo si cambió se crean tablas / columnas nuevas (`ALTER TABLE ADD COLUMN`).
2. **Subida:** filas locales con `UpdatedAtUtc` posterior a la última subida (sin tope superior desde v1.17: una fila guardada durante la subida o con marca adelantada también sube; la nube descarta lo que no sea más nuevo), en orden de dependencias (categorías → productos → lotes → ...), en lotes de 80 sentencias:
   `INSERT ... SELECT ... WHERE NOT EXISTS (borrado en SyncTombstones con DeletedAtUtc >= UpdatedAtUtc) ON CONFLICT(SyncId) DO UPDATE SET ... WHERE excluded.UpdatedAtUtc > tabla.UpdatedAtUtc` (gana la versión más nueva y un registro ya borrado en otro equipo no se vuelve a crear). El borrado se reescribe solo si es más nuevo.
   Borrados: upsert en `SyncTombstones` + `DELETE ... WHERE SyncId = ? AND UpdatedAtUtc <= fechaDelBorrado`.
3. **Descarga:** se toma la marca del servidor (`now - 5 s`) y se leen filas con `SyncedAtUtc` mayor a la última marca guardada, en páginas de 500, **excluyendo las de este mismo dispositivo**. Las filas se aplican sin re-estampar `UpdatedAtUtc` (bandera `AppDbContext.ApplyingRemoteChanges`), traduciendo SyncId → Id local.
4. **Perfil del consultorio:** gana el `UpdatedAtUtc` más reciente.
5. Se guardan `LastPushedLocalUtc` y `LastPulledServerWatermark`.

Hoy, una fila cuya referencia obligatoria no tiene `SyncId` se omite en la subida con solo un aviso en la bitácora y no se reintenta. Y si el reloj de la PC se atrasa después de sincronizar, lo que se guarde antes de la siguiente sincronización queda con una marca anterior a `LastPushedLocalUtc` y no sube nunca (esa sincronización mueve el cursor a la hora atrasada); solo sube si se vuelve a editar o con «Sincronización completa».

**Plan Fase 8 (pasos 8.5 y 8.6):**
- El cursor de subida pasa de `LastPushedLocalUtc` a `LastBackedUpSeq`: la secuencia (`Seq`) de `SyncChangeLog`, el registro de cambios que llenan los triggers de SQLite, que no depende del reloj.
- Se sube la fila actual, o su borrado si el registro lo marca como borrado, incluidos los borrados en cascada (`SaleItems` al borrar una `Sale`).
- Una fila que no se puede subir detiene el cursor y la pantalla dice "N cambios sin respaldar".
- En el modo `Backup` no se descarga nada: no hay descarga (punto 3 de arriba) y el perfil del consultorio (punto 4) solo se sube. La descarga solo queda en `SharedPcs` y en "Recuperar desde la nube" (§7).

### 4.4 Garantías
- **Sin transacción:** `TursoClient.ExecuteBatchAsync` no es transaccional aunque su comentario lo diga (el comentario se corrige en el paso 8.2): cada sentencia del pipeline se confirma sola. La subida es idempotente, así que un lote que falla a medias se reintenta completo sin dañar nada.
- **Sin ciclos:** lo recibido conserva su marca y no vuelve a subirse ni a descargarse.
- **Relojes desfasados:** las descargas usan el reloj del servidor, no el del dispositivo. Desde v1.17 un cambio local siempre lleva una marca posterior a la que ya tenía la fila (`max(ahora, marca guardada + 1 ms)`), también en los borrados: una edición hecha después de recibir una fila de un equipo con el reloj adelantado ya no la descarta la nube.
- **Folios duplicados** (dos equipos crean `EXP-2026-005` sin conexión): el registro recibido se guarda como `EXP-2026-005-R1` en lugar de bloquear la sincronización. Solo pasa entre dos PC; con la Fase 8 la PC asignará todos los folios y nunca habrá `-R` (plan §4, A3).
- **Sincronización completa** (botón "Sincronización completa"): ignora las marcas y sube/descarga todo; útil al enlazar un equipo nuevo. Después de restaurar un respaldo regresa de la nube las filas más nuevas y deshace la restauración (§7). Con la Fase 8, restaurar y recuperar seguirán §7.

### 4.5 Consulta entre equipos (`ConsultationHandoffs`, desde v1.17)
Para pasar la consulta en curso de una PC a otra con **un solo dueño a la vez**. La tabla no se replica: cada equipo la consulta directamente (`Services/ConsultationHandoffService.cs`). Hoy funciona entre dos PC.

> **Plan Fase 8:** este camino queda solo para `SharedPcs` (dos PC). En el modo `Backup` la PC no revisará esta tabla ni mandará el latido (paso 8.2). Entre la PC y la tablet, la consulta pasará por la red del consultorio con los mismos estados y reglas, guardados en `LanHandoffs` (`lan.db`) y decididos por la PC con un candado (plan §3.9, paso 8.23).

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

Versión mínima para recibir: TPVC 1.17.0 (la hoja "Enviar a…" marca como incompatibles los equipos con `AppVersion` anterior). Esta regla queda solo para el camino de Turso entre dos PC; con la Fase 8, los equipos de la red se decidirán por la capacidad `handoff.v1` (paso 8.23).

---

## 5. Vinculación de la tablet y el celular

### Hoy (v1.19.0): paquete en claro (se retirará en el paso 8.17)
En *Configuración → Nube y dispositivos → Vincular tablet y celular*, "Generar código" abre una ventana con un JSON **en texto claro** (`TursoSettingsService.GenerateAndroidPairingPayload`) para copiar y pegar: URL de la base, **token completo de Turso**, rol, descripción del rol, consultorio y médico. No hay QR ni cifrado, y todavía no existe una app que lo lea.

Quien tenga ese texto puede leer y escribir toda la base de la nube, y el rol no se aplica en ningún lado. **No debe usarse para vincular.** El paso 8.17 retira `GenerateAndroidPairingPayload` y su ventana (`OpenPairingModal`).

### Plan Fase 8: QR por la red del consultorio (pasos 8.16 y 8.17; detalle en el plan §3.2 y §3.3)
1. En la PC: *Configuración → Nube y dispositivos → Vincular tablet o celular*. El médico elige el rol (tablet o celular) y la PC muestra un QR durante 10 min.
2. El QR lleva `tpvc://pair?v=1&s=<serverId>&h=<IP de la PC>…&n=<nombre de la PC>&p=47820&k=<SHA-256 del SPKI del certificado>&c=<código de un uso, 128 bits>&r=tablet|phone&x=<vence>`. **No lleva el token de Turso ni datos del consultorio.**
3. El equipo se conecta a la primera dirección que responda, comprueba que el certificado coincide con `k` y llama a `POST /api/v1/pair` con el código.
4. La PC pregunta "¿Vincular «Tablet Samsung» como Tablet?" con *Vincular* y *Rechazar*.
5. La PC responde con un `deviceId` y un secreto propio del equipo, y guarda solo el SHA-256 del secreto en `PairedDevices` (`lan.db`). Cada llamada lleva `Authorization: Bearer tpvc1.<deviceId>.<secret>`.
6. El código sirve una sola vez, vence en 10 min y se invalida tras 5 intentos fallidos. Se aceptan como máximo 5 vinculaciones por minuto.
7. **Quitar** un equipo en "Equipos vinculados" lo revoca: su siguiente llamada recibe `401 device_revoked` y la app borra su token, su caché y sus borradores.

La identidad de la PC se prueba con el certificado fijado más el `serverId`, nunca con la dirección IP.

---

## 6. Guía Rápida de Configuración en Turso

> El token de Turso **se queda en la PC**: se pega solo en TPVC de Windows, que lo guarda cifrado con DPAPI en `%LocalAppData%\TPVC\turso_config.json`. No lo copie a la tablet ni al celular (el paquete de vinculación de hoy lo lleva en claro, ver §5). Con la Fase 8 ellos no hablarán con Turso y el QR de vinculación tampoco lo llevará (§1 y §5).

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
```

### Opción B: Mediante Consola Web de Turso
1. Ingresar a [https://turso.tech](https://turso.tech) y registrarse gratis con GitHub o Google.
2. Hacer clic en **"Create database"**.
3. Nombrar la base de datos (ej. `tpvc-clinica`) y elegir la región más cercana (ej. `qro` Querétaro, México o `dfw` Dallas).
4. Copiar la **Database URL** y generar un **Auth Token** desde la pestaña *Settings > Tokens*.
5. Pegar ambos valores en TPVC Windows en **Configuración > Nube y dispositivos**.
6. Hacer clic en **"Probar conexión"** y posteriormente en **"Sincronización completa"**.

---

## 7. Respaldo y recuperación (plan Fase 8, pasos 8.2, 8.4, 8.6 y 8.7)

Nada de esta sección está implementado todavía. Hoy (v1.19.0) la nube se sincroniza en los dos sentidos (§4.3). Al restaurar un respaldo local se borran los cursores (`LastPushedLocalUtc`, `LastPulledServerWatermark`): la siguiente sincronización sube todo, pero la nube conserva las filas más nuevas que el respaldo (gana el `UpdatedAtUtc` mayor) y la descarga omite las que escribió esta misma PC. Con una sola PC, la base local queda como el respaldo y la nube conserva lo posterior: las dos copias quedan distintas, y una «Sincronización completa» regresaría esas filas y desharía la restauración. Lo que escribió otra PC sí regresa en la siguiente descarga (riesgo H6 del plan).

### Modo respaldo (`TursoConfig.CloudMode`, paso 8.2)
- **`Backup`**, el modo por defecto:
  - Solo sube: el primer cambio arma una subida a los 2 min; después, como máximo una cada 5 min mientras haya cambios, más una al cerrar con tope de 5 s.
  - No descarga.
  - No revisa `ConsultationHandoffs` ni manda el latido de 30 s.
  - Registra el equipo en `RegisteredDevices` una sola vez por arranque.
- **`SharedPcs`**: el comportamiento actual, congelado sin cambios. Solo sirve para dos PC y está marcado como no recomendado.
- **Migración:** queda en `Backup`, salvo que `RegisteredDevices` tenga otra PC con Windows vista en los últimos 30 días. Con el servidor del consultorio encendido, la nube siempre queda en `Backup`.
- **En pantalla:** "Uso de la nube: Solo respaldo (recomendado) / Compartir entre computadoras".
- **Cursor:** la secuencia de `SyncChangeLog` (pasos 8.5 y 8.6, ver §4.3).

### Verificar copia en la nube (paso 8.6)
`COUNT(*)` por tabla, local contra remota, en una sola petición. Corre a pedido y después de restaurar, no cada semana.

### Restaurar un respaldo local y reemplazar la copia de la nube (paso 8.7)
- Toda restauración (`DataImportService`) pone una `Generation` nueva (`DbMeta`) y reconstruye el registro de cambios.
- Con la nube activa, pregunta "Reemplazar la copia de la nube con este respaldo": borra la copia remota y sube todo. Si no hay red, queda pendiente (`TursoNeedsRebase`).
- En modo respaldo nunca se descarga, así que la nube ya no puede regresar filas posteriores al respaldo.

### Recuperar desde la nube (paso 8.7)
Para una PC nueva o cuando la PC se descompone. En una base vacía:
1. Usa la bandera `RestoreFromCloudPending` (paso 8.4), así que no siembra categorías, servicios ni pacientes demo.
2. Descarga en orden de dependencias.
3. Estaciona y reintenta las filas que llegan sin su principal.
4. Pone una generación nueva.

Después se vuelven a vincular la tablet y el celular (§5).

### Reglas comunes
- Cada reescritura de una sola vez lleva respaldo previo, una bandera idempotente y su renglón en la bitácora.
- La nube no guarda `lan.db` (equipos vinculados), las preferencias de cada equipo ni las imágenes del membrete (escudo y encabezado; opcional en el paso 8.E).
- La copia de Turso no es un historial: el plan gratuito solo restaura a un punto de hasta 1 día atrás. El historial son los respaldos locales diarios (`BackupService`, se conservan 14).
- **Opcional** (paso 8.A, solo si el médico lo pide): una segunda base de Turso solo con cifras (resumen, corte, alertas y compra sugerida), sin expedientes, para el celular fuera del consultorio.
