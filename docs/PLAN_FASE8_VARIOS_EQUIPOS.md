# Fase 8 · Datos listos para tres equipos con Turso

Estado al 2026-10-09 (TPVC v1.19.0, rama `fase-8/datos-varios-equipos`). **Por ahora es solo el plan.** Nada de lo que describe está implementado. Aquí se fijan la arquitectura, el contrato que tendrán que cumplir la PC y la app en Kotlin, y el orden de los pasos. El código viene después, un paso por versión. Cada versión deja la PC funcionando sola y trae pruebas que corren en Linux con el servidor Turso simulado (`FakeHranaServer`, en `tests/TPVC.Tests/TursoSyncEndToEndTests.cs`), sin equipos Android ni una base real de Turso. Para que eso funcione en la CI hace falta el paso 8.2: hoy el proyecto de pruebas es `net10.0-windows` y no arranca en Linux sin el runtime de escritorio.

Esta versión **reemplaza el borrador del mismo día** que se publicó en este archivo (commit `0d9e12f`). Ese borrador convertía la PC en servidor de la red local y en la única que escribe, y dejaba Turso solo como respaldo. El médico lo descartó el 2026-10-09 después de ver que el uso real de Turso cabe de sobra en el plan gratuito (§3). Esta versión ya trae las correcciones de dos revisiones técnicas.

**En una frase:** cada equipo (PC, tablet y celular) trabaja sobre su propia base y se sincroniza por Turso con el protocolo v2, como ya lo hace la PC. La Fase 8 cierra los huecos que hoy perderían o duplicarían datos cuando escriban dos equipos, y deja listo lo que la app en Kotlin necesita antes de su primera pantalla.

---

## 1. Decisiones del médico y supuestos

| # | Decisión (2026-10-09) | Qué implica |
|---|---|---|
| 1 | Tres equipos: la PC con Windows, una tablet Android y un celular Android. El 99% del tiempo están en la misma red, pero **la red local no se usa como canal**. | No hay servidor en la PC ni conexión directa entre equipos. Todo pasa por Turso. |
| 2 | **Se conserva la arquitectura original.** Cada equipo tiene su SQLite local y se sincroniza por Turso (plan gratuito) con el protocolo v2. La consulta pasa entre equipos por `ConsultationHandoffs` en Turso. | Varios equipos escriben, así que los bloqueantes A1–A6 y los riesgos H1–H11 se resuelven en los datos (§5). |
| 3 | La app de la tablet y del celular es **Kotlin nativo**, no .NET MAUI. | En Android no se reutiliza código C#. El código de la PC es la referencia. Kotlin implementa por su cuenta el motor del protocolo v2 y el contrato de la consulta entre equipos, y los dos lados se comparan con archivos de conformidad (`contracts/`). |
| 4 | La Fase 8 tiene dos partes: datos seguros con tres equipos escribiendo por Turso, y todo lo que va antes de las pantallas en Kotlin. Por ahora solo se escriben el plan y la documentación. | El código va después, una versión por paso (§6). |
| 5 | La receta impresa no lleva línea de firma (ya decidido). | La receta de Android sale del mismo modelo que la de la PC. |

**Pedidos del médico que cubre este plan:**
- Vincular la tablet leyendo un código QR (paso 8.15).
- Compartir la receta y el ticket en PDF desde donde se vean (pasos 8.11 y 8.12; app K4 en el celular y K7 en la tablet).
- Continuar una consulta en otro equipo. Entre dos PC ya funciona desde v1.17; la tablet implementará el mismo contrato (app K8).

**Reglas de la consulta que se conservan en la tablet:** la consulta nunca manda al POS, "Se surte aquí" viene marcado por defecto y no existe "terminar sin cobrar".

**Supuestos**
- La PC funciona completa por sí sola, siempre. Con la nube apagada, TPVC se comporta como en v1.19.
- Un solo médico, como máximo tres equipos activos. Si hay otra PC con Windows conectada a la misma base, el paso 8.20 no avanza hasta actualizarla o marcarla como retirada.
- Sin internet, cada equipo sigue trabajando sobre su base y sus cambios esperan. Las revisiones que dependen de los demás (existencias, folios, citas) se hacen con los datos locales (H11, §5).
- No hay servidor de notificaciones. La tablet ve una consulta enviada en unos segundos si la app está al frente, o en ~1 s al abrirla, pero no recibe aviso con la pantalla apagada. Para tenerlo haría falta un servicio nuevo, como Firebase (opcional 8.C).
- Las actualizaciones de la app Android pueden llegar tarde, por Play Store (prueba interna) o por un APK instalado a mano. Por eso los cambios del contrato son aditivos, y una app vieja pasa a solo lectura con "Actualice la app".
- Las fechas del negocio (consulta, cita, venta, corte) se guardan en la hora del consultorio (`America/Mexico_City`), sin zona. `UpdatedAtUtc` y `SyncedAtUtc` siempre están en UTC. El contrato marca cada columna (8.3).

---

## 2. Arquitectura

### 2.1 Cada equipo con su base, Turso como punto de encuentro

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

- El **protocolo v2** sigue siendo el de hoy (`Services/TursoSyncService.cs`, `Services/TursoClient.cs`, `docs/TURSO_SYNC_ARCHITECTURE.md` §4):
  - una fila remota por registro, con llave `SyncId`;
  - LWW por `UpdatedAtUtc`;
  - marca de agua del servidor (`SyncedAtUtc`) para bajar;
  - `OriginDeviceId` para omitir lo propio;
  - lápidas para los borrados.

  La Fase 8 le agrega columnas y reglas, siempre de forma aditiva. Cuando cambia el significado de las existencias (paso 8.20), la nube se marca `protocol = 3` para que un equipo que no conoce las reglas nuevas se detenga.
- **Turso no ejecuta reglas del negocio.** Solo guarda filas y decide de uno en uno los upserts condicionados. Las reglas (libro de existencias, combinación de pacientes, folios) viven en cada motor y se comprueban con vectores compartidos.

### 2.2 Papel de cada equipo

| | PC (`WindowsDesktop`) | Tablet (`AndroidTablet`) | Celular (`AndroidMobileAdmin`) |
|---|---|---|---|
| Base local | `consultorio.db` (EF Core) | Room + SQLCipher, con llaves por `SyncId` | Igual que la tablet |
| Escribe en Turso | Todo | Pacientes, notas, citas, ventas, movimientos, lápidas, su latido y `ConsultationHandoffs` | Nada (token de solo lectura; pregunta 3) |
| Crea o migra el esquema remoto | **Sí, solo la PC** | Nunca | Nunca |
| Vincula y revoca equipos, escribe `DeviceRegistry` y `_tpvc_meta` | Sí | No | No |
| Edita catálogo, inventario, perfil y membrete | Sí | No (inventario solo lectura) | No |
| Consulta entre equipos | Envía y recibe | Envía y recibe | No (excluido como hoy, `ConsultationHandoffService.cs:181`) |
| Latido en `RegisteredDevices` | Sí | Sí, solo con la app al frente | No |
| Folios | La PC principal, sin código (como hoy); otra PC, `P2`… | `T1`… | No crea folios |
| Siembra datos | Solo en una base nueva (8.8) | Nunca | Nunca |

### 2.3 Esquema remoto: dueño y piezas nuevas

- **Solo una PC con Windows crea o migra el esquema remoto.** Hoy cualquier cliente corre DDL:
  - `EnsureRemoteSchemaAsync` empieza con `CREATE TABLE IF NOT EXISTS _tpvc_meta` (`TursoSyncService.cs:494-506`) y puede renombrar tablas a `legacy_v1_*`;
  - `EnsureTablesAsync` de la consulta entre equipos corre `CREATE` en cada proceso nuevo (`ConsultationHandoffService.cs:97-103`).

  Kotlin nunca ejecuta DDL, nunca corre la limpieza de `ConsultationHandoffs` y se niega a sincronizar si falta `_tpvc_meta.schema` ("Sincronice primero la PC").
- **Tablas nuevas:**

  | Tabla | Dónde y quién escribe | Para qué | Paso |
  |---|---|---|---|
  | `DeviceRegistry` (`DeviceId` PK, `DeviceCode` único, `Role`, `DisplayName`, `Model`, `PairedAtUtc`, `RevokedAtUtc`, `RevokedReason`, `TokenExpiresAtUtc`, `PublicKey`, `RenewedCredential`) | Base del consultorio; solo la PC | Autoridad sobre el rol, el código de folios, la revocación y la renovación del token de cada equipo. Sus filas nunca se borran. | 8.10, 8.16 |
  | `DevicePairings` (buzón de vinculación) | **Base de vinculación aparte**; la PC, y el equipo solo reclama y cierra su fila | Vinculación por QR (§4.4). Así el QR nunca lleva un token de la base del consultorio. | 8.15 |
  | `ClinicAssets` (escudo y encabezado del membrete) | Base del consultorio; solo la PC | La receta de Android lleva el mismo membrete. | 8.12 |

- **Columnas nuevas en tablas que no salen del modelo de EF.** Llevan `ALTER` explícito, revisado con `pragma_table_info` e incluido en la huella, porque el ciclo de columnas nuevas solo cubre las tablas de `GetTableMaps`. Todas aceptan nulo:
  - `RegisteredDevices`: `Platform`, `SyncProtocol`, `HandoffProtocol`, `ContractRevision`, `LastPushUtc`, `PendingCount`, `OldestPendingUtc`, `InstanceNonce` (8.10).
  - `ClinicProfiles`: `CrestImageHash`, `HeaderImageHash` (8.12).
- **Columnas nuevas en tablas sincronizadas** (salen del modelo y viajan por el ciclo normal): `InventoryMovements.Delta` y `SaleItemSyncId`, `Sales.LedgerVersion`, `ProductBatches.ClosedAtUtc` (8.19); `InventoryMovements.CountedQuantity` (8.20); `Patients.FieldStampsJson` (8.22); `Patients.MergedIntoSyncId` (8.23). Cada `ALTER` recorre su tabla en Turso una vez (§3.2).
- **Claves de `_tpvc_meta`** (solo las escribe la PC):

  | Clave | Valor | Paso |
  |---|---|---|
  | `schema` | Huella del DDL (ya existe) | — |
  | `clinicId` | GUID de la clínica, se escribe una sola vez | 8.7 |
  | `generation` | GUID que cambia al reemplazar la nube con un respaldo | 8.7, 8.9 |
  | `generationReady` | La generación cuya subida completa ya terminó; nadie vuelve a bajar todo antes de verla | 8.9 |
  | `protocol` / `minClientProtocol` | 2 a partir de 8.7; 3 a partir de 8.20 | 8.7, 8.20 |
  | `contract` | Revisión aditiva de `contracts/sync/schema.json` | 8.3 |
  | `manifest` | Tablas, columnas y tipos (el mismo `schema.json`) | 8.7 |
  | `fullUploadAtUtc` | Fecha de la primera subida completa verificada (A6) | 8.7 |
  | `primaryDeviceId` | La PC principal, la que lleva el código de folio vacío | 8.10 |

- **`DbMeta` local** (dentro de `consultorio.db`): `clinicId`, `generation`, `DbInstanceId`, `LastPushedSeq`, desfase del reloj, revisión del contrato con la que se bajó cada tabla, `StockLedgerVersion`, `CloudJoined` y "reemplazo en curso". El último número de cada serie de folios vive **fuera** de la base (8.17), para que sobreviva a una restauración.

### 2.4 Consulta entre equipos

- Se conserva el contrato de `ConsultationHandoffs` (`docs/TURSO_SYNC_ARCHITECTURE.md` §4.5):
  - estados `enviada`, `aceptada` y `cancelada`;
  - `AcceptToken` para que gane un solo equipo;
  - recuperar con la instrucción con cerca;
  - la copia se borra solo después de guardarla.

  Kotlin lo implementa tal cual (app K8). La copia de trabajo es el `ConsultationDraft` v1. Desde 8.22 la copia lleva además las marcas por campo del paciente y la marca de la nota, y quien la recibe exige tenerlas (campo aditivo, `PayloadVersion` sigue en 1).
- Para recibir una consulta, un equipo cuenta como compatible si `RegisteredDevices.HandoffProtocol >= 1`. Si esa columna está vacía (PC v1.17–v1.19), sigue la regla de hoy: `AppVersion >= 1.17.0` (`ConsultationHandoffService.ToDevice`). Así la app Android puede numerar sus versiones desde 1.0.0 (paso 8.10).
- El celular no envía ni recibe consultas.

### 2.5 Decisiones técnicas de este plan

| Tema | Opciones | Decisión | Por qué |
|---|---|---|---|
| Aviso de que otro equipo subió algo | (a) una clave `changed` en `_tpvc_meta`; (b) una clave por equipo en `_tpvc_meta`; (c) bajar cada 60 s | Columna `RegisteredDevices.LastPushUtc`, escrita al final de cada subida con la hora del servidor y leída dentro de la revisión de consultas, más una descarga de seguridad cada 10 min | La tablet y el celular no escriben `_tpvc_meta`. La marca de uno mismo no dispara una descarga inútil. Cuesta unas 3–6 filas leídas por revisión. |
| Identidad del equipo al vincular | La PC genera el `DeviceId` y lo pone en el QR, o el equipo genera el suyo | El equipo genera su `DeviceId`; la PC solo asigna el código de folios | La descarga omite las filas propias (`TursoSyncService.cs:771`): dos instalaciones con el mismo `DeviceId` nunca verían las filas de la otra. Por eso el `DeviceId` nunca viaja en un QR ni en un respaldo, y la PC detecta un identificador clonado (8.10). |
| Dónde vive el código de folios | `RegisteredDevices.DeviceCode` o una tabla aparte | `DeviceRegistry.DeviceCode` con índice único | `DeviceRegistry` solo la escribe la PC, así que un equipo no puede cambiarse el código. |
| Formato del código y del folio | `EXP-2026-T001`, o códigos `T1`/`C1` | Código de letra y número (`T1`, `C1`, `P2`), y folio con guion: `EXP-2026-T1-001` | No se confunde cuando llegue `T10`, y los lectores actuales de folios de la PC lo ignoran (§5, A3). |
| Folios repetidos | (a) el `-R1` de hoy al recibir; (b) índice normal y aviso | (b) | El `-R` renombra solo en el equipo que recibe, sin marca y sin subirlo: el mismo paciente queda con dos folios. Con códigos de equipo solo se repiten filas viejas o números tecleados a mano. |
| Compatibilidad entre versiones | Lista de capacidades (`handoff.v1`) o números | Columnas enteras `SyncProtocol` y `HandoffProtocol`, más `Platform` y `ContractRevision` | Se comparan sin interpretar texto. |
| Cursor de subida | Registro de cambios con triggers (`Seq`), o regresar el cursor de reloj cuando la hora se atrasa | Registro de cambios (`SyncChangeLog`) | No depende del reloj y además registra los borrados en cascada. |
| Tablas en Kotlin | Traducir `SyncId` a un Id local, o usar `SyncId` como llave sin llaves foráneas | `SyncId` como llave, sin llaves foráneas | No hay traducción y nunca se pierde una fila que llega antes que su principal. Los borrados copian las reglas en cascada, restringir o vaciar del contrato (8.3). |
| Columnas de existencias derivadas | No subirlas, o subirlas solo como información | Viajan como información y nunca se aplican al bajar | El formato no cambia y la copia en Turso sigue siendo legible. |
| Cantidades que fija el usuario (baja de lote, conteo) | (a) un movimiento con la diferencia contra el libro; (b) un conteo que sirve de ancla | (b) | Con (a), una venta hecha sin conexión antes del conteo, que llega después, se descuenta dos veces. |
| Hojas del paciente (`Active*Json`) | (a) un solo campo, gana la hoja completa; (b) renglones con `RowId` que se combinan | (b) | Las hojas también se editan fuera de la consulta (`SuppliesSheetViewModel.cs:204-216`), así que la PC y la tablet pueden tocar la misma hoja. |
| Cargos libres en las hojas | Copiar el hash de .NET en Kotlin, o una clave estable | Ninguna de las dos: el renglón lleva la clase `libre` y el nombre, y cada equipo calcula su Id local con `FreeChargeId` | Ese Id solo vive en la cuenta del equipo que lo calcula; entre equipos viajan la clase y el nombre. |
| Rellenar `ChargedLineSyncId` en hojas existentes | Al arrancar, o no | No | Las filas recibidas de otra PC traen los Id del otro equipo, y un relleno a ciegas las mapearía mal. Se actualizan en su siguiente guardado. |
| Número de protocolo | Quedarse en 2 con una revisión, o subir a 3 | `protocol` sube a 3 en 8.20; `contract` cuenta las revisiones aditivas | En 8.20 cambia el significado de las existencias. La tablet empieza directamente en 3. |
| Credenciales | Token completo compartido (hoy), o tokens por equipo | Tokens por equipo (nivel A) o por rol (nivel C). El flujo del QR es el mismo en los dos | §4.3 |
| Buzón de vinculación | (a) en la base del consultorio, con un token de 15 min en el QR; (b) en una base de vinculación aparte | (b) | Un código corto se adivina sin conexión con una GPU (§4.5), y sin V1 el token de (a) sería `full-access` sobre todos los expedientes. La base aparte cuesta 1 de las 100 gratis. |
| PDF para compartir | (a) WebView + `PrintDocumentAdapter`; (b) `PdfDocument` dibujado | (b), y WebView solo para *Imprimir* | `PrintDocumentAdapter` no da un PDF sin el diálogo de impresión: sus callbacks tienen constructores privados del paquete. |
| Siembra en un equipo que se une | `SyncId` fijos, o unirse sin sembrar | Las dos: "Unirse a una nube existente" es la solución; los `SyncId` fijos son un seguro para instalaciones nuevas | Las nubes que ya existen tienen `SyncId` aleatorios. |
| Orden de los pasos | (a) primero todo lo de varios equipos que escriben; (b) primero lo que necesita el celular | (b): etapas 0–2, luego el celular, luego etapas 3–4 | El celular solo baja y no necesita 8.17–8.26. Sale meses antes y prueba en equipos reales lo más incierto: Hrana, la vinculación, la criptografía y el PDF. |

### 2.6 Alternativas descartadas

| Alternativa | Por qué no |
|---|---|
| **La PC como servidor en la red local** y única que escribe, con Turso solo de respaldo (borrador `0d9e12f`) | Descartada por el médico el 2026-10-09. Hoy la PC hace ~8.7k peticiones al día, más del 90% por la revisión de cada 4 s y el latido de cada 30 s. Con tres equipos haciendo lo mismo serían ~23k al día: ~1% del límite de escrituras y ~2.5% del de lecturas, muy dentro del plan gratuito. A cambio, el servidor local pedía Kestrel, certificado, firewall, que la PC nunca se durmiera, y una tablet que no cobra con la PC apagada. |
| .NET MAUI reutilizando el C# | El médico eligió Kotlin. |
| Android sin base local, leyendo y escribiendo directo en Turso | Sin internet no funcionaría, y cada pantalla sería una ida y vuelta por internet. |
| Cambiar el protocolo v2 por la sincronización propia de libSQL/Turso | Habría que rehacer el motor que ya funciona en la PC, y no resuelve A1–A4: son reglas del negocio, no del transporte. |
| El token completo de la PC compartido con todos los equipos (el paquete de hoy) | No se puede revocar por equipo, el rol no se aplica, y viaja en claro y por el portapapeles (§4.1). |

---

## 3. Uso de Turso con tres equipos y cadencia

### 3.1 Límites vigentes del plan gratuito

Al mes, según <https://turso.tech/pricing> (consultado el 2026-10-09): 500M filas leídas, 10M filas escritas, 5 GB, 100 bases y 3 GB de sincronización de réplicas (TPVC no la usa). Restauración a un punto en el tiempo de hasta 1 día. Según <https://docs.turso.tech/help/usage-and-billing>:
- Turso cobra como lectura cada fila recorrida; una instrucción que no toca filas cuenta como 1.
- `ALTER TABLE` y `CREATE INDEX` recorren toda la tabla (una lectura por fila), y `ALTER` puede escribir cada fila.
- Al pasar el límite, toda instrucción falla con el código `BLOCKED`, también la consulta entre equipos.

La app todavía muestra los límites viejos (`TursoQuotaSummary`, `Models/TursoConfig.cs:138-139`; `TursoSyncViewModel.RefreshQuotaDisplay`, :175-180). Se corrigen en 8.2.

**Dos hallazgos que entran en el plan:**
- **F1 · La descarga completa lee muchísimo de más.** Medido con SQLite 3.53, parámetros enlazados y sin `ANALYZE`, como en una base nueva de Turso. Con la consulta de `TursoSyncService.cs:775`, el planificador usa "MULTI-INDEX OR" y ordena todo lo que queda de la tabla en cada página de 500. Una tabla de 20k filas cuesta ~275k lecturas en lugar de ~20k. Las descargas incrementales no se ven afectadas. Se corrige anteponiendo `"SyncedAtUtc" >= ? AND (…)` (paso 8.6). Falta confirmar el plan de consulta en Turso real (V15).
- **F2 · La revisión de consultas lee de más.** `IX_ConsultationHandoffs_To (ToDeviceId, State)` solo se usa por `ToDeviceId`, así que cada revisión recorre todas las filas que se conservan para ese equipo (hasta 7 o 365 días). Un índice parcial `(ToDeviceId) WHERE State='enviada' OR (State='aceptada' AND Payload<>'')` lo baja a ~0 filas sin cambiar la consulta (paso 8.24). SQLite solo lo usa si el `WHERE` de la consulta repite esa condición, así que el SQL va literal en el contrato (8.13).

### 3.2 Uso estimado

Supuestos:
- 30 días al mes.
- La PC abierta 10 h al día, 8 de ellas en Pacientes.
- La tablet al frente 6 h al día, 5 de ellas en pantallas de pacientes.
- El celular abierto 0.5 h al día.
- ~150 sincronizaciones al día por guardados y ~400 filas cambiadas al día.

| Concepto | Hoy (v1.19, solo la PC) | Hoy, si los tres equipos hicieran lo mismo | Fase 8 (tres equipos, plan) | Proporción del plan gratuito (Fase 8) |
|---|---|---|---|---|
| Peticiones al día | ~8,700 | ~23,000 | ~12,800 (PC ~7.9k, tablet ~4.9k, celular ~30) | El plan no limita peticiones |
| Filas leídas al mes | 0.4M–4.2M | hasta ~12M | 2M–4.5M: cada revisión lee 4–11 filas (con el índice parcial) y hay ~600 bajadas al día de ~25 filas | 0.4–0.9% de 500M |
| Filas escritas al mes | ~45k (el latido es 1,200 al día) | ~120k | ~50k: al día, 960 latidos (cada 60 s, solo PC y tablet), ~400 filas de datos y ~300 `LastPushUtc` | ~0.5% de 10M; hasta ~1.5% si Turso cuenta las entradas de índice (V10) |
| Peor caso: PC y tablet abiertas 24 h en Pacientes | — | — | ~45k peticiones al día; ~7M lecturas y ~120k escrituras al mes | 1.4% y 1.2% |
| Almacenamiento | Decenas de MB al año | Igual | Igual, más ~1 MB del membrete (`ClinicAssets`) | < 1% de 5 GB |
| Bases | 1 | 1 | 2: la del consultorio y la de vinculación, solas en su grupo (§4.3) | 2 de 100 |

**Costos de una sola vez:**
- Vincular un equipo o hacer una "Sincronización completa" lee todo una vez: ~20–30k filas después de 8.6. Hoy cuesta ~N²/1000 por tabla.
- La primera subida con el cursor nuevo (8.5) y "Reemplazar la nube" (8.9) escriben ~20k filas (0.2% del mes) en el peor caso. Reemplazar además lee la nube una vez para guardar lo que se perdería.
- La verificación de la subida completa (8.7) lee ~20k filas una vez.
- Los `ALTER` de 8.19, 8.20, 8.22 y 8.23 cuestan ~20k lecturas y hasta ~20k escrituras cada uno; el índice parcial de 8.24 recorre `ConsultationHandoffs` una vez. Todo junto queda abajo del 1–2% de un mes.

El contador de la app (`RecordUsage`) sigue siendo una estimación. La cifra real está en el panel de Turso.

**El riesgo no está en el uso normal (el margen es de ~100×) sino en un ciclo descontrolado.** Un error que repita una descarga completa (~25k filas) cada 4 s gasta ~540M lecturas en un día: se acaba el mes de los tres equipos y Turso responde `BLOCKED` a todo. Lo mismo con un ciclo que vuelve a escribir en cada pasada (una combinación de pacientes que rebota, un eco de los triggers). Por eso: cursor por página (8.6), freno de consumo y traducción de `BLOCKED` (8.2), serialización canónica en la combinación (8.22) y pruebas de un día simulado (8.24).

### 3.3 Cadencia elegida

| Equipo | Momento | Qué corre | Cada cuánto |
|---|---|---|---|
| PC (`Services/SyncScheduler.cs`, sin WPF, con `TimeProvider`) | Siempre, con la nube activa | Sincronizar después de guardar | 4 s después del último cambio (como hoy) |
| | | **Revisión**, en una sola petición: consultas recibidas, estado del envío propio, `LastPushUtc` de los otros equipos, latido cada 60 s (hoy 30 s) y limpieza cada 6 h | 4 s en Pacientes si hay un envío pendiente o un equipo capaz de recibir consultas visto en los últimos 30 días; si no, 30 s |
| | | Si `LastPushUtc` de otro equipo es posterior a la última descarga | Sincronización en segundo plano en ese momento |
| | | Descarga de seguridad | Cada 10 min; al activar la ventana si la última sincronización buena fue hace más de 30 s; al volver de la suspensión o de un corte de red |
| | Sin red | Reintentos | 30 s, 1 min, 2 min y 5 min |
| | Cuota agotada (`BLOCKED`) o freno de consumo | Se pausa la sincronización automática | Reintento cada 1 h (`BLOCKED`) o hasta *Reanudar* (freno) |
| Tablet | Al abrir la app (`ProcessLifecycleOwner` ON_START) | Latido, sincronización y revisión, en ese momento | Una consulta enviada aparece en ~1 s |
| | Al frente | Revisión con la misma marca de cambios | 4 s en pacientes y consulta; 30 s en otras pantallas |
| | | Descarga de seguridad / sincronizar tras guardar | 10 min / 4 s |
| | | Sin red (`registerDefaultNetworkCallback`) | Se detiene y sincroniza cuando vuelve la red |
| | Al pasar a segundo plano (ON_STOP) con cambios sin subir | Sube en ese momento desde el proceso, que Android deja vivo unos segundos. De respaldo, un trabajo único `tpvc-push` en WorkManager: normal (no expedito), red conectada, reintento creciente desde 30 s | Una vez |
| | En segundo plano | Descarga incremental periódica (red conectada, batería no baja). Nunca la primera descarga completa. Sin revisión ni latido, así que la PC la muestra como "la verá al abrir Pacientes" | Cada 60 min |
| Celular | Al abrir y al frente | Solo descarga, sin revisión ni latido | Al abrir y cada 120 s |
| | En segundo plano | Descarga incremental periódica | Cada 3 h, con cualquier red |

Lo que ve el médico:
- Lo capturado en la tablet aparece en la PC en ≤ 8 s si la PC está en Pacientes, o en ≤ 35 s en otra pantalla.
- Lo de la PC aparece en la tablet al frente en 4–30 s.
- El celular se pone al día al abrirlo.

**Transporte en Android:** un solo cliente OkHttp para toda la app, con HTTP/2, conexiones vivas al menos 5 min, gzip y 30 s de espera (20 s en la consulta entre equipos). Es lo mismo que hace `SharedHttpClient` (`TursoClient.cs:44-51`). Sin reusar la conexión, cada revisión de 4 s pagaría un saludo TLS nuevo.

**Segundo plano en Android:**
- Un trabajo expedito de WorkManager corre como servicio en primer plano en Android 11 y anteriores (pide notificación y, en Android 14, el tipo `dataSync`). Por eso el respaldo es un trabajo normal.
- WorkManager detiene un trabajo a los 10 min. Por eso el cursor se guarda después de cada página (8.6) y la primera descarga solo corre con la app al frente.
- Las tablets Samsung mandan apps a "suspensión" y el trabajo periódico puede no correr nunca. Hay que excluir TPVC de la suspensión y de la optimización de batería (sección D de `PENDIENTES_ANDROID.md`).

**Batería:**
- Al frente, cada revisión es una petición de 1–2 KB por Wi-Fi sobre una conexión ya abierta. Es despreciable junto a la pantalla encendida.
- En segundo plano hay como máximo un trabajo por hora, agrupado por Doze, sin wakelocks ni servicio en primer plano.

---

## 4. Vinculación por QR, credenciales, roles y equipo perdido

### 4.1 Cómo está hoy (v1.19.0)

- **Paquete de vinculación.** `GenerateAndroidPairingPayload` (`Services/TursoSettingsService.cs:131-148`) arma un JSON en claro con el **token completo de la PC**, sin vencimiento y sin `DeviceId`.
  - Se muestra en `Views/TursoSyncView.xaml:273`.
  - "Copiar código" lo pone en el portapapeles (`TursoSyncViewModel.cs:413-427`), así que puede quedar en el historial del portapapeles de Windows.
  - Nada lo lee.
- **Rol.** Lo elige cada equipo en un selector (`TursoSyncViewModel.cs:93-97`, 296) y se escribe en `RegisteredDevices`. `DevicePermissions` (`Models/TursoConfig.cs:28-58`) no se aplica en ningún lado: en producción solo se usa `GetRoleDescription`.
- **Token.** Se guarda con DPAPI (`ProtectedAuthToken`). Fuera de Windows queda en base64 con el prefijo `B64:`. Si DPAPI falla al descifrar, el token queda vacío en silencio (`TursoSettingsService.cs:183-208`).
- **URL.** `NormalizePipelineUrl` (`TursoClient.cs:102-150`) acepta `http://` y cualquier host con punto, incluidas IP de la red local.
- **Servidor simulado.** `FakeHranaServer` ignora el encabezado `Authorization`.

### 4.2 Qué ofrece Turso

| Pieza | Qué hace | Nota |
|---|---|---|
| Token de base o de grupo (JWT) | Vencimiento propio (por defecto "never"; también "15m", "2w1d"). Autorización `full-access` o `read-only`. | Se crea con la CLI o con `POST /v1/organizations/{org}/databases/{db}/auth/tokens`. |
| Permisos finos por tabla y acción | `data_read`, `data_add`, `data_update`, `data_delete`, `schema_add`, `schema_update`, `schema_delete` | Están documentados para la CLI y para JWKS. La API de la plataforma solo documenta `permissions.read_attach` (V1). |
| Revocación | No hay revocación por token. `auth/rotate` (lo mismo que `turso db tokens invalidate`) rota las llaves de **todo el grupo**, con una breve interrupción. | Por eso las dos bases del consultorio van solas en su grupo. |
| Token de la API de la plataforma | Por organización, o por grupo con alcances (`db:mint-token`, `db:rotate-creds`…). Se listan y se revocan por id. | Falta ver si existe en el plan gratuito (V2). |
| JWKS propio | En beta, solo con Clerk y Auth0. | No se usa: pide un servidor. |
| Plan gratuito | Sin lista de IP permitidas. | La bitácora de auditoría está por confirmar (V11). |

**Por comprobar en una base de prueba real de Turso.** Siempre en un grupo o una cuenta de prueba, nunca en el grupo del consultorio: una rotación ahí corta al médico.

| # | Qué se comprueba | Qué decide | Antes de |
|---|---|---|---|
| V1 | Si la API de la plataforma acepta permisos por tabla, y con qué campo | Si Turso hace cumplir los límites de la tablet | 8.14 |
| V2 | Si en el plan gratuito funciona un token de grupo con solo `db:mint-token` y `db:rotate-creds` | Nivel A o C | 8.14 |
| V3 | Qué responde Hrana a una instrucción prohibida y a un token vencido o rotado (HTTP 401 o un código Hrana). Si un token de solo lectura rechaza `CREATE TABLE IF NOT EXISTS` sobre una tabla que ya existe | Mensajes y reglas del 401 | 8.14 |
| V4 | Cuánto dura la interrupción al rotar | Texto de "Cortar acceso ahora" | 8.16 |
| V5 | El largo real de un token | Tamaño del QR | 8.15 |
| V6 | Si el cliente puede leer el `exp` del JWT | Distinguir vencido de revocado | 8.14 |
| V7 | Texto y estado HTTP de `BLOCKED` | Traducción | 8.2 |
| V8 | Cuántos grupos permite el plan gratuito | Dónde corren las pruebas de rotación | 8.14 |
| V9 | Si el plan gratuito archiva una base sin uso (hay reportes de 10 días) y qué error da | Mensaje e instrucciones para desarchivarla después de unas vacaciones | 8.2 |
| V10 | Si las entradas de índice cuentan como filas escritas | Presupuesto de §3.2 | 8.2 |
| V11 | Si el plan gratuito tiene bitácora de auditoría, y de cuántos días | §4.7 | 8.16 |
| V12 | El tope de tamaño de una respuesta Hrana | Tamaño de página | 8.6 |
| V13 | Que un token de la base de vinculación no abra la base del consultorio del mismo grupo | §4.4 | 8.15 |
| V14 | Si una petición `batch` de Hrana con BEGIN/COMMIT es atómica en Turso | Opción de 8.6 | 8.6 |
| V15 | `EXPLAIN QUERY PLAN` de F1 y F2 en Turso real (libSQL no es SQLite 3.53) | 8.6 y 8.24 | 8.6 |

El resultado se anota en `docs/TURSO_SYNC_ARCHITECTURE.md` §6.

### 4.3 Credenciales: lo que se decide

| Quién lo tiene | Nivel A (recomendado: la PC crea los tokens) | Nivel C (si no hay token de la API) |
|---|---|---|
| PC | Token de la API limitado al grupo, solo `db:mint-token` y `db:rotate-creds`, cifrado con DPAPI. Con él crea su propio token `full-access` de 90 días y lo renueva cuando quedan menos de 30. **Es la única que cambia el esquema.** | Token completo pegado a mano, como hoy. |
| QR de vinculación | Token de 15 min **de la base de vinculación**, que no tiene datos clínicos. | Token fijo de la base de vinculación, creado en el panel. |
| Tablet | 180 días, renovado solo (8.16). Con V1: lee todo y escribe (`data_add`, `data_update` y `data_delete`, porque cada lápida va seguida de un `DELETE`) solo en Patients, PatientConsultations, Appointments, Sales, SaleItems, InventoryMovements, SyncTombstones, RegisteredDevices y ConsultationHandoffs, sin `schema_*`. Desde 8.20 ya no necesita escribir Products ni ProductBatches. Sin V1: `full-access`. | `full-access` creado en el panel, sin vencimiento. |
| Celular | 180 días, renovado solo, `read-only`. Con V1 se le puede agregar `RegisteredDevices:data_add,data_update` para el latido. | `read-only` creado en el panel, sin vencimiento. |

- El token propio de la PC nunca sale de la PC. El token de datos de un equipo solo viaja sellado para la llave de ese equipo (§4.4), nunca en el QR.
- El `read-only` del celular sí lo hace cumplir Turso, en los dos niveles.
- Los límites de la tablet (sin esquema, sin catálogo ni configuración) solo los hace cumplir Turso si V1 funciona; si no, los aplica la app.
- Con V1 se podrían limitar también las lecturas por tabla, pero la tablet necesita leer todas las tablas sincronizadas (Sales, para sus propios tickets). Por eso el corte de caja solo se oculta en su interfaz.
- En el nivel C los tokens no vencen: la revocación es de todos modos rotar el grupo, y un vencimiento sin renovación automática solo dejaría a la tablet con cambios atorados.
- Pregunta 4: si la cuenta de Turso permite el nivel A.

### 4.4 Vinculación por QR: base de vinculación, código y confirmación

La PC solo ofrece vincular si su sincronización está sana, existe `_tpvc_meta.fullUploadAtUtc` y hay menos de 3 equipos activos.

1. **El médico la inicia** en *Nube y dispositivos → Equipos vinculados → Vincular tablet o celular*. La PC:
   - crea un `PairingId` (16 bytes aleatorios);
   - reserva un código de equipo (`T1`, `C1`…: el mayor de `DeviceRegistry` + 1);
   - crea el token de 15 min de la base de vinculación (nivel A) o toma el fijo (nivel C);
   - inserta en `DevicePairings` una fila `State='esperando'` con el rol, el código y `ExpiresAtUtc` = hora del servidor + 15 min.
2. **El QR** es `tpvc-pair:1:` + base64url(sal de 16 bytes | nonce de 12 | AES-256-GCM(k, JSON)), con k = PBKDF2-SHA256(código de 10 dígitos, sal, 200k iteraciones).
   - El JSON lleva: `u` la URL de la base de vinculación, `t` su token, `p` el `PairingId`, `c` el `clinicId`, `r` el rol, `d` el código de equipo, `n` el nombre del consultorio y `e` el vencimiento en UTC. **Ningún token de la base del consultorio.**
   - Pesa menos de ~1.3 KB (V5), QR de versión 20–27 con corrección M. Se muestra de al menos 8 cm.
   - El código de 10 dígitos aparece debajo del QR, en dos grupos de cinco.
   - El mismo texto sirve pegado, para que otra PC con Windows se una con "Unirse con código".
3. **El equipo reclama la fila.** Se niega si el equipo no tiene bloqueo de pantalla seguro.
   - Lee el QR, pide el código, descifra y revisa el vencimiento.
   - Genera su propio `DeviceId` y un par de llaves P-256.
   - Corre en la base de vinculación `UPDATE DevicePairings SET State='reclamada', ClaimDeviceId=?, ClaimName=?, ClaimModel=?, ClaimPublicKey=? WHERE PairingId=? AND State='esperando' AND ExpiresAtUtc > <hora del servidor>`. Si afecta una fila, ganó. Si afecta cero, muestra "Este código ya se usó".
   - Muestra un código de confirmación de 6 dígitos sacado de SHA-256(`PairingId` ‖ `ClaimPublicKey`).
4. **El médico confirma en la PC.** Mientras la ventana está abierta, la PC revisa la fila cada 2 s y pregunta "¿Vincular «Galaxy Tab S9» como Tablet? Código 482 913". Con *No*, la fila queda en `State='cancelada'`.
5. **Con *Sí*, la PC:**
   - crea el token del equipo (nivel A) o toma el del rol (nivel C);
   - inserta la fila en `DeviceRegistry`, con `PublicKey` = `ClaimPublicKey`;
   - escribe `SealedCredential`: un intercambio ECDH-P256 entre una llave desechable suya y `ClaimPublicKey`, derivado con HKDF-SHA256 (sal = `PairingId`, info = `tpvc-pair-v1`), que cifra {URL de la base del consultorio, token, vencimiento, rol, código, clinicId} con AES-256-GCM (datos asociados = `PairingId` ‖ `DeviceId`);
   - deja `State='confirmada'`.
6. **El equipo termina.** Descifra la credencial, la guarda envuelta con el Android Keystore y deja `State='completada'` con `SealedCredential=''`. La PC borra la fila, igual que las vencidas.

Volver a vincular con el mismo `clinicId` y el mismo `DeviceId` conserva la base del equipo y sube lo pendiente. Un `clinicId` distinto empieza de cero, después de subir o exportar lo pendiente (§4.7).

### 4.5 Qué obtiene quien fotografía el QR

- **Cuánto protege el código.** PBKDF2-SHA256 con 200k iteraciones se prueba a ~40k intentos por segundo en una GPU de consumo. Un PIN de 6 dígitos caería en ~25 s; uno de 10 dígitos tarda días, mucho más que los 15 min que vive el QR en el nivel A.
- **El QR, aun con el código:** solo la base de vinculación, sin datos clínicos (V13). A lo más, una oportunidad de ganar el reclamo antes que la tablet. El médico vería un nombre o un código de confirmación extraño y el reclamo de su tablet fallaría, así que rechazaría. El token de datos nunca está en el QR: viaja sellado para la llave del equipo que reclamó.
- **En el nivel C** el token de la base de vinculación no vence. Una foto filtrada deja vigilar futuras vinculaciones y competir por ellas, pero sigue sin abrir la base del consultorio y el código de confirmación sigue protegiendo. Se corrige cambiando ese token en el panel.

### 4.6 Registro, latido y permisos

- **`DeviceRegistry`** es la autoridad para rol, código, revocación y renovación. La app de Kotlin oculta pantallas según el rol que lee ahí.
- **`RegisteredDevices`** sigue siendo el latido. Cada equipo escribe su fila con la misma instrucción y la hora del servidor, y ahora también `Platform`, `SyncProtocol`, `HandoffProtocol`, `ContractRevision`, `LastPushUtc`, `PendingCount`, `OldestPendingUtc` e `InstanceNonce`.
- El rol de la PC queda fijo en `WindowsDesktop`: se quita el selector (8.2).
- `DevicePermissions` gana `CanViewClinicalRecords` y `CanShareDocuments`.

| Función | PC | Tablet | Celular | Quién lo hace cumplir |
|---|:---:|:---:|:---:|---|
| Ver expedientes (`CanViewClinicalRecords`, nuevo) | sí | sí | sí | La app |
| Editar expedientes y consulta (`CanManageClinicalRecords`) | sí | sí | no (pregunta 3) | Turso en el celular (`read-only`); la app en la tablet |
| Recetar | sí | sí | no | Igual |
| Cobrar y vender | sí | sí | no | Igual |
| Agendar citas | sí | sí | pregunta 3 | Igual |
| Editar inventario y catálogo (`CanManageInventory`) | sí | no, solo ve | no, solo ve | Turso si V1; si no, la app |
| Resumen, alertas y compra sugerida | sí | sí | sí | — |
| Corte de caja (`CanViewCashCut`) | sí | no (oculto) | sí | La app |
| Compartir receta y ticket (`CanShareDocuments`, nuevo) | sí | sí | sí | La app |
| Consulta entre equipos | sí | sí | no | La app (`GetLinkedDevicesAsync` excluye el celular) |
| Configuración, Excel, respaldos, Turso, vincular, perfil y membrete (`CanConfigureSystem`) | sí | no | no | Turso si V1 (sin `schema_*`); si no, la app |

### 4.7 Renovación, quitar un equipo y equipo perdido

**Renovación (8.16, requisito antes de la primera versión de la app).** En el nivel A, cuando al token de un equipo le quedan menos de 30 días, la PC crea uno nuevo, lo sella con `DeviceRegistry.PublicKey` (el mismo esquema del paso 5 de §4.4) y lo deja en `DeviceRegistry.RenewedCredential` hasta que vence el anterior. El equipo lo recoge en su siguiente revisión o descarga. En el nivel C los tokens no vencen.

**Quitar un equipo que no se perdió.** La PC muestra antes sus cambios pendientes ("La tablet tiene 12 cambios sin subir desde las 10:42", de `PendingCount` y `OldestPendingUtc`). Al leer su fila revocada con motivo "quitado", el equipo sube lo pendiente y luego borra su base, su credencial y sus borradores. Si no puede subir, exporta lo pendiente a un archivo que se puede volver a aplicar.

**Perdí este equipo** (en *Equipos vinculados*):

1. **Revocar** con motivo "perdido". La PC pone `RevokedAtUtc` y deja de renovar el token. Si el equipo vuelve a conectarse y lee su fila, borra su base, su credencial y sus borradores sin subir nada.
2. **"Cortar acceso ahora"** (marcado por defecto):
   - La PC rota las llaves del grupo, con una breve interrupción (V4).
   - En el nivel A crea en ese momento su propio token nuevo y sincroniza. En el nivel C el médico pega un token nuevo creado en el panel.
   - Los demás equipos reciben 401 y muestran "Vuelva a vincular" sin perder sus cambios pendientes. El médico escanea un QR nuevo en la tablet, que conserva sus datos.
3. **Reglas ante un 401:**
   - Si el `exp` del token ya pasó (V6), es un vencimiento: "Vuelva a vincular", y nunca se borra nada.
   - Si no, el celular (solo lectura, nada pendiente) se borra solo tras dos 401 con 10 min de diferencia.
   - La tablet se bloquea y conserva sus datos.
4. **Límites:**
   - Si el plan gratuito no tiene bitácora de auditoría (V11), hay que suponer que el equipo perdido pudo leer todo hasta la rotación.
   - Se recomienda borrarlo también con "Encontrar mi dispositivo".
   - Si no se rota, en el nivel A el vencimiento de 180 días es el último límite; en el nivel C no hay otro límite que la rotación.
5. **Si se pierde la PC:** rotar desde el panel de Turso, revocar por su id el token de la API, instalar la PC nueva con "Recuperar desde la nube" (8.8) y marcarla como "Esta PC reemplaza a la principal" (8.10).

### 4.8 Seguridad local en Android

- **Base cifrada.** SQLCipher (`net.zetetic:sqlcipher-android` con `SupportOpenHelperFactory` de Room), con una frase aleatoria de 32 bytes envuelta por una llave AES-256-GCM del Android Keystore.
  - Usa StrongBox si el equipo lo tiene.
  - `unlockedDeviceRequired=false`, para que la sincronización siga con la pantalla bloqueada.
- **Credencial y llave del reclamo:** en un archivo envuelto con una llave del Keystore. `EncryptedSharedPreferences` está en desuso.
- **Sin vinculación** si el equipo no tiene bloqueo de pantalla seguro.
- **Sin copias de seguridad:** `allowBackup=false` y `dataExtractionRules` que excluyen todo. Una copia clonaría el `DeviceId` y además no podría restaurar las llaves del Keystore.
- **Bloqueo de la app en el celular** con huella o con la credencial del equipo, después de 5 min en segundo plano.
- **Red:** el TLS normal del sistema, sin fijar certificados. `usesCleartextTraffic=false` explícito, porque Android 8.0 y 8.1 (API 26–27) permiten texto en claro por defecto; la app solo acepta URL `https://`.

---

## 5. Cómo quedan A1–A6 y los demás riesgos

| # | Riesgo (comprobado en v1.19.0) | Solución | Paso |
|---|---|---|---|
| A1 | **Existencias como valores.** Si dos equipos venden, gana la última escritura y se pierde un descuento. Los lotes no se pueden reconstruir con los movimientos de hoy: una venta anota solo el primer lote aunque FEFO descuente de varios (`DeductFromBatches`, `InventoryService.cs:416-444`); `SaveBatchAsync` no deja movimiento; las devoluciones van a un lote adivinado (:747-757); borrar un lote lo borra físicamente (:377-411). Cancelar el mismo ticket en dos equipos devuelve dos veces. | **Libro de movimientos:** `Delta` con signo, `SaleItemId`, un movimiento por lote, devoluciones con `SyncId` determinista (UUIDv5) y regla elegida por `Sale.LedgerVersion`, lotes cerrados en lugar de borrados, conteos como ancla, saldos iniciales una sola vez en la PC principal, y existencias y lotes como columnas derivadas que se recalculan y nunca se aplican al bajar. Se permite quedar en negativo, con aviso por producto y por lote. | 8.19, 8.20 |
| A2 | **Siembra en cada equipo.** Categorías y SERV-001..005 con `SyncId` aleatorios (`AppDbContext.cs:114`) se duplican al sincronizar. `App.xaml.cs:82` pasa `seedDemoData: !isFirstRun`, así que en cualquier arranque posterior al primero con la tabla de pacientes vacía siembra los pacientes demo. `SeedClinicalServices` vuelve a correr si la tabla está vacía, incluso a media descarga. | Pacientes demo solo desde el asistente; `SyncId` fijos (UUIDv5) para el catálogo de una base nueva; "Unirse a una nube existente" con `CloudJoined` (nunca vuelve a sembrar); Kotlin nunca siembra. | 8.8 |
| A3 | **Folios por equipo.** EXP y TKT son el mayor número local + 1. CNS usa la hora (`ActiveConsultation.cs:39`), tiene otros respaldos (al azar, por conteo, `-IMPnn`), no es columna única, y el `-R1` nunca le aplica. El `-R1` (`TursoSyncService.cs:887-920`) solo existe en el equipo que recibe. | `FolioFactory` con código de equipo. La PC principal lleva código vacío (los folios de hoy no cambian); los demás llevan `EXP-2026-T1-001`, `TKT-20261009-T1-0001`, `CNS-20261009-T1-103015`. Último número por serie guardado fuera de la base. Índice normal en lugar de único y sin `-R` (pregunta 1). | 8.17 · opcional 8.B |
| A4 | **Id locales dentro del JSON** (`ChargedLineId`, `ServiceId`). Error latente entre dos PC: después de recibir una consulta, + / − o quitar en las hojas busca el renglón con el Id del otro equipo (`ConsultationAccount.cs:174`, :192). | `ChargedLineKind`, `ChargedLineSyncId`, `ServiceSyncId` y `RowId` por renglón. Los lectores buscan primero por `SyncId`, luego `libre`, y al final por el Id viejo solo si el nombre coincide. Sin relleno. `[JsonExtensionData]` en los renglones. | 8.18 |
| A5 | **No hay sincronización periódica.** Un equipo que no cambia nada no ve lo de los demás. | `SyncScheduler`, la marca `LastPushUtc` leída en la revisión, y una descarga de seguridad cada 10 min. | 8.24 |
| A6 | **La primera subida real nunca se verificó** (las 1.8.x no subían nada). | Reporte de la "Sincronización completa": filas apartadas, conteo local contra remoto por tabla y tablas `legacy_v1_*`. Solo si sale limpio se escriben `clinicId`, `manifest` y `fullUploadAtUtc`. Vincular queda bloqueado hasta entonces. | 8.7 |
| H1 | **El paciente se guarda completo.** `SavePatientAsync` copia todas las columnas (`SetValues`, `InventoryService.cs:1239`) y `CanReplaceSelectedPatientInstance` (`PatientsWorkspace.Flows.cs:114-117`) conserva copias viejas en memoria. Agendar en otro equipo puede borrar las alergias recién editadas en la PC. | Guardar solo lo que cambió (también corrige un error que hoy existe en la PC sola), combinación por campo con `FieldStampsJson`, hojas combinadas por renglón y resumen de citas derivado. | 8.21, 8.22 |
| H2 | **Citas empalmadas.** `SaveAppointmentAsync` (:1480) no revisa nada. | Regla `AppointmentRules.Overlaps`; sincronización rápida y pregunta antes de guardar; marca "Empalme" después de bajar. Nunca se cancela una cita sola. | 8.23 |
| H3 | **Pacientes duplicados** (no hay CURP ni revisión de nombre). | Regla: nombre normalizado más la misma fecha de nacimiento o los mismos últimos 10 dígitos del teléfono. Aviso al crear, banner después de bajar y la columna `MergedIntoSyncId`. | 8.23 · opcional 8.A |
| H4 | **Borrados.** Los borrados en cascada no dejan lápida. Un borrado remoto que falla solo se anota (:955-960). `SaveClinicalServiceAsync` borra y vuelve a insertar `ServiceDefaultProducts` (:1083). | Filas apartadas que se reintentan, lápidas desde triggers (cascadas incluidas) y comparación de `ServiceDefaultProducts` por producto. | 8.4, 8.5 |
| H5 | **Relojes.** Un equipo adelantado gana todos los conflictos. Si el reloj se atrasa después de sincronizar, lo que se guarde no sube nunca, porque `LastPushedLocalUtc` sale del reloj del equipo (:272, :286). | Cursor por secuencia (`SyncChangeLog.Seq`); marca con la hora del servidor (`max(ahora + desfase, guardada + 1 ms)`), también en las lápidas de los triggers; reparación de marcas en el futuro; reloj anclado en Android; aviso si el desfase pasa de 2 min. | 8.5, 8.7 |
| H6 | **Restaurar con la nube activa.** La restauración borra los cursores (`DataImportService.cs:230-236`): la nube conserva lo posterior al respaldo y una "Sincronización completa" lo regresaría. | Generación nueva con cerca en cada escritura, `generationReady`, lo que se perdería guardado y "Volver a aplicar". Dos salidas: "Reemplazar la nube con este respaldo" o "Desconectar esta PC". | 8.9 |
| H7 | **Borradores solo locales.** Si el equipo que mandó la consulta se descompone, su marca queda atorada. | Sin cambio de diseño: la copia vive en `ConsultationHandoffs` hasta que la acepten. Kotlin guarda la marca de "enviada" en su borrador igual que la PC. | — |
| H8 | Preferencias locales (`HideConsultationTimer`). | Sin cambio: son de cada equipo. | — |
| H9 | **El membrete no se sincroniza.** Las imágenes solo están en `%LocalAppData%\TPVC\membrete`. | Tabla `ClinicAssets`. Se baja solo cuando cambia el hash guardado en `ClinicProfiles`. | 8.12 |
| H10 | **El paquete de vinculación lleva el token completo en claro y el rol no se aplica.** | Se quita el paquete; tokens por equipo o por rol; QR con código de 10 dígitos y sin token de datos; base de vinculación; `DeviceRegistry`; renovación, revocación y rotación. | 8.2, 8.10, 8.14–8.16 |
| H11 | `EnsureStock` (:446-457) e `IsRecordNumberTakenAsync` (:1169) solo miran datos locales. | Se quedan locales. Lo cubren los códigos de folio, la sincronización rápida antes de cobrar y de agendar (con red), la política de negativos y la detección de duplicados después de bajar. | 8.17, 8.20, 8.23 |

**Otros riesgos, del código y de las revisiones del plan:**

| Riesgo | Solución | Paso |
|---|---|---|
| La subida y la bajada omiten con solo un aviso la fila sin su referencia obligatoria (:644, :852), y el cursor avanza. Una referencia **opcional** que llega antes que su principal se guarda en nulo para siempre (:828-848): pasa con `InventoryMovement.BatchId` y pasaría con `SaleItemId`, y termina en existencias por lote distintas y devoluciones dobles. | `SyncParkedRows`: se apartan las dos, se reintentan y la pantalla dice "N cambios sin sincronizar". La opcional se aplica en nulo solo si su principal tiene lápida. | 8.4 |
| Triggers mal hechos: lápida con el reloj crudo, `CREATE TRIGGER IF NOT EXISTS` que no ve columnas nuevas, `ON CONFLICT DO NOTHING` que deja un `Seq` viejo, `SyncId` locales en mayúsculas, `RETURNING` de EF Core. | Marca con desfase, triggers con hash, `INSERT OR REPLACE`, minúsculas, `UseSqlReturningClause(false)`. | 8.5 |
| Lo que se decide durante la bajada (resultado de una combinación, reasignaciones) no entra al registro de cambios y nunca sube. | `MarkForUpload` después de aplicar. | 8.5, 8.22 |
| F1 y la falta de desempate: dos equipos que marcan `guardada + 1 ms` quedan distintos para siempre. | Consulta con `>=` adelante; si un upsert afecta 0 filas, se vuelve a leer la fila del servidor y se aplica aunque empate, o su lápida. | 8.6 |
| Una sola respuesta con la página 1 de las 13 tablas puede pasar de 10 MB en una descarga completa. | Una tabla por petición en la descarga completa, página por tabla que se reduce sola. | 8.6 |
| Un reloj que se mueve sin conexión deja filas con marca en el futuro que ganan todo, y la relectura tras "0 filas" descarta la edición de otro equipo. | Reparación de marcas en el futuro (local antes de subir, y una vez en la nube), reloj anclado a `elapsedRealtime` en Android. | 8.7, §7.3 |
| Un equipo que se actualiza y conoce una columna nueva la pisa en la nube con el valor por defecto. | Antes de subir, bajar completas las tablas cuyas columnas conocidas crecieron. | 8.3, 8.7, §7.3 |
| Pegar URL y token mezcla una base con datos propios con una nube que ya existe (duplicados de A2, folios que chocan). | `DbMeta.clinicId` debe coincidir; si no, solo "Unirse", "Reemplazar con la nube" o "Esta PC ya usaba esta nube". | 8.7 |
| La generación se revisa solo al inicio: los trozos que un equipo seguía subiendo caen en la nube restaurada, y una PC que se cierra a media subida deja una nube incompleta que los demás bajarían. | Cerca de generación en cada escritura, `generationReady` y "reemplazo en curso". | 8.9 |
| Copiar `%LocalAppData%\TPVC` a otra PC clona el `DeviceId`, y cada una omite las filas de la otra. | `DeviceId` nuevo si falla DPAPI o cambia la máquina; `InstanceNonce` en el latido. | 8.10 |
| Una PC que no es la principal queda con código de folio vacío; restaurar o reemplazar la nube repite números EXP y TKT ya impresos. | Barrera para crear folios y último número por serie fuera de la base. | 8.10, 8.17 |
| El `-R` sin marca deja al mismo paciente con dos folios. | Índice normal, sin `-R`, aviso de folios repetidos. | 8.17 |
| Las hojas `Active*Json` se editan fuera de la consulta y se pisan completas: renglones o `ChargedUnits` perdidos, cargos dobles. | `RowId` y combinación por renglón. | 8.18, 8.22 |
| Las operaciones que fijan una cantidad (baja de lote, conteo, diálogo de producto) cuentan doble lo vendido sin conexión. Un lote borrado deja sin lote al movimiento que llega tarde, sin aviso. | Lotes cerrados en lugar de borrados, conteos como ancla y aviso de lote negativo. | 8.19, 8.20 |
| La combinación por campo elige mal cuando falta una marca, y una PC v1.19 no lee `protocol`: no hay barrera técnica. | Regla de escritura sin marcas; 8.20 se niega mientras haya otra PC sin actualizar. | 8.20, 8.22 |
| Aceptar una consulta solo compara el `UpdatedAtUtc` de la fila (`ConsultationHandoffViewModel.cs:1199-1206`): una tablet con la fila "más nueva" por otro campo acepta sin los últimos cargos de la PC. | La copia lleva las marcas por campo y la de la nota. | 8.22 |
| `NextAppointment*` sigue el orden de guardado, no el clínico (`InventoryService.cs:1523-1533`: la última cita guardada, aunque esté "Atendida"). | Resumen derivado, recalculado después de cada bajada. | 8.22 |
| Las fechas del negocio salen de `DateTime.Now` y el contrato no lo dice. Android 8 y 9 sin tzdata actualizada aplican el horario de verano que México quitó en 2022. | `timeKind` en `schema.json`; Kotlin usa la zona del consultorio, con UTC−6 fijo si sus reglas son viejas. | 8.3, §7.3 |
| Revocar, cambiar de `clinicId` o de generación, o subir `minClientProtocol` borran cambios clínicos sin subir. | `PendingCount` en el latido; subir o exportar antes de borrar; nunca borrar por vencimiento; cada fila de la cola guarda su protocolo. | 8.9, 8.10, 8.16 |
| Un ciclo descontrolado agota la cuota del mes y Turso responde `BLOCKED` a los tres equipos. | Cursor por página, freno de consumo, traducción de `BLOCKED`, pruebas de un día simulado. | 8.2, 8.6, 8.24 |
| La CI de Linux no arrancaría: el proyecto de pruebas exige `Microsoft.WindowsDesktop.App`. | El job copia ese runtime. | 8.2 |
| `NormalizePipelineUrl` acepta `http://` e IP de la red local. | Solo `https://` (`http://` solo para localhost). | 8.2 |
| El diálogo de producto compara las existencias absolutas contra la base (`InventoryService.cs:229`): una venta que baja mientras está abierto se revierte. Pasa también con la PC sola. | `OriginalStock` (8.19) y luego un conteo (8.20). | 8.19, 8.20 |
| Todos los clientes corren DDL. | Solo la PC es dueña del esquema; Kotlin nunca corre DDL. | 8.10 |
| `RegisteredDevices` crece sin límite y la revisión lo lee completo. | La limpieza de 6 h borra los revocados y los no vistos en 60 días. | 8.10 |
| F2 (revisión de consultas). | Índice parcial, con el SQL literal en el contrato. | 8.13, 8.24 |
| El comentario de `ExecuteBatchAsync` (`TursoClient.cs:262`) dice "transaccional" y no lo es; los límites del plan gratuito en el código están viejos. | Se corrigen. | 8.2 |

---

## 6. Pasos en la PC

Cada paso es una versión publicable (vX.Y.Z), deja la PC funcionando sola y trae sus pruebas.
- Las pruebas corren en Linux con `FakeHranaServer` (desde 8.2).
- **[W]** marca lo que necesita la CI de Windows o capturas (`UiSmokeTests`).
- **[T]** marca una comprobación manual en una base de prueba real de Turso, en un grupo o cuenta aparte.

Las etapas 0 y 1 valen la pena aunque nunca exista la app: dejan una sincronización sin pérdidas, la hora del servidor, una siembra segura y la restauración correcta. Después de la etapa 2 puede salir el celular; la tablet necesita también las etapas 3 y 4.

### Etapa 0 · Base

#### 8.1 · Documentación
- **Cambia:** este plan y los cambios de §8 en los demás documentos. No toca código.
- **Prueba:** la CI de Windows sigue verde.
- **Riesgo:** ninguno.

#### 8.2 · CI de Linux, simulador más fiel, freno de consumo y limpieza
- **Antes:** [T] V7, V9 y V10 (§4.2).
- **Cambia:**
  - **Job `ubuntu-latest` en `ci.yml`.** El proyecto de pruebas es `net10.0-windows` con `UseWPF` (`tests/TPVC.Tests/TPVC.Tests.csproj`), y su testhost exige `Microsoft.WindowsDesktop.App`, que `setup-dotnet` no instala en Linux. `EnableWindowsTargeting` (`Directory.Build.props`) permite compilar, no ejecutar; en la máquina de desarrollo corre porque ese runtime se copió a mano. El job:
    - descarga el `windowsdesktop-runtime-10.0.x-win-x64.zip` oficial, con versión fijada, y copia `shared/Microsoft.WindowsDesktop.App` a `$DOTNET_ROOT/shared`;
    - corre `dotnet test --filter "TestCategory!=Windows&TestCategory!=Rendimiento"`, una vez en UTC y otra con `TZ=America/Mexico_City`.

    `UiSmokeTests` y las pruebas que crean tipos de WPF o usan DPAPI real se marcan `[TestCategory("Windows")]`. Separar un `TPVC.Core` en `net10.0` sería la alternativa grande; no hace falta ahora.
  - **`FakeHranaServer`** (ya atiende una petición a la vez con `_gate`) gana:
    - un reloj del servidor que se puede inyectar;
    - una estimación de lecturas cobradas (instrucciones más filas recorridas);
    - una grabadora de peticiones;
    - una revisión con `EXPLAIN QUERY PLAN` ante un SCAN de una tabla sincronizada. Solo avisa hasta 8.6; desde ahí falla, con una lista de excepciones (`COUNT(*)` de 8.7, `DELETE` de 8.9);
    - un tope configurable de tamaño de respuesta;
    - la respuesta `BLOCKED` a pedido.
  - **`BLOCKED`** se traduce a "Se agotó la cuota mensual de Turso", y la sincronización automática espera 1 h antes de reintentar (V7). Una base archivada se traduce con instrucciones para desarchivarla (V9).
  - **Freno de consumo**, con el estimador local (`RecordUsage`): como máximo 2 descargas completas al día y un tope diario de 3M lecturas y 30k escrituras estimadas. Al pasarlo se pausa la sincronización automática con "Sincronización pausada: consumo anormal" y un botón *Reanudar*.
  - Límites correctos del plan gratuito (500M, 10M, 5 GB) en `TursoQuotaSummary` y `RefreshQuotaDisplay`. Se quita la leyenda "consume menos del 0.05%" (`TursoSyncViewModel.cs:180`).
  - Se corrige el comentario de `ExecuteBatchAsync`: no es transaccional.
  - `NormalizePipelineUrl` acepta solo `https://`; `http://` solo para `localhost` y `127.0.0.1`.
  - **Se quita el paquete en claro:** `GenerateAndroidPairingPayload`, `OpenPairingModal`, `CopyPairingPayload` (portapapeles) y su ventana. Los botones dicen "Disponible con la vinculación por QR".
  - El rol de la PC queda fijo en `WindowsDesktop` (se quita el selector).
- **Prueba:**
  - El job nuevo pasa y el de Windows no cambia.
  - Prueba base: "una sincronización sin cambios = 16 peticiones".
  - `BLOCKED` pausa una hora; una descarga completa repetida activa el freno.
  - Una URL `http://192.168.…` se rechaza.
  - [W] Captura de *Nube y dispositivos*.
- **Riesgo:** bajo.

#### 8.3 · Contrato de sincronización exportado
- **Cambia:**
  - `contracts/sync/schema.json`, generado desde `GetTableMaps`. Lleva:
    - las tablas en el orden de `OrderByDependencies` y el `EntityType` de cada una (el nombre de la clase C#: `PatientRecord`, `PatientAppointment`…). **La subida va en este orden**, en la PC y en Kotlin;
    - columnas con tipo remoto, si aceptan nulo, el valor que va en lugar del nulo, si son derivadas y su **`timeKind`**: `utc` (`UpdatedAtUtc`, `SyncedAtUtc`), `clinicLocal` (`Sale.Date`, `CancelledAt`, `InventoryMovement.Date`, `StartedAt` y `EndedAt` de la consulta, `CreatedAt`: hoy salen de `DateTime.Now`) o `dateOnly` (`BirthDate`, fecha de la cita);
    - referencias con su principal, si son obligatorias o suaves, y qué pasa al borrar (en cascada, restringir o vaciar);
    - las columnas únicas, el DDL y la huella.
  - Reglas que el contrato fija por escrito:
    - Todo `SyncId` viaja, y entra a un UUIDv5, en minúsculas. Los locales están en mayúsculas (`BackfillSyncColumns`, `DatabaseInitializer.Schema.cs:106-117`; así guarda EF Core los `Guid` en SQLite).
    - Un equipo que se actualiza y conoce columnas nuevas de una tabla la baja completa antes de subir nada (8.7 en la PC, §7.3 en Kotlin).
  - Vectores en `contracts/golden/`: `values.json` (codificación Hrana), `lww.json` (con el empate y la relectura que encuentra lápida), `stamping.json` y `uuid5.json` (RFC 4122, con el caso `uuid5(DNS,"python.org") = 886313e1-3b8a-5372-9b90-0c9aee199e5d` y un `SyncId` en mayúsculas que da lo mismo que en minúsculas).
  - `GoldenVectorTests` compara contra los archivos y los reescribe con `TPVC_WRITE_GOLDEN=1`. Así un cambio en el modelo de EF aparece como diferencia del contrato.
  - Aclara si `Sale.PatientName` y `SaleItem.ProductName` (alias con get y set, `Models/Sale.cs:19`, :50) se mapean como columnas extra.
- **Prueba:** cambiar una regla o el modelo sin regenerar los archivos rompe la CI.
- **Riesgo:** ninguno.

### Etapa 1 · Sincronización sin pérdidas

#### 8.4 · Filas apartadas (H4)
- **Cambia:**
  - `SyncParkedRows` (tabla local). Lo que hoy se omite con solo un aviso, dejando avanzar el cursor, se aparta, se reintenta en cada sincronización y se cuenta en pantalla ("N cambios sin sincronizar"):
    - en la subida, la fila cuya referencia obligatoria no tiene `SyncId` (`TursoSyncService.cs:634-646`);
    - en la bajada, la fila sin su principal obligatorio (:850-854), **y la que trae una referencia opcional con un `SyncId` que todavía no existe en este equipo**. Hoy esa referencia se guarda en nulo (:828-848) y la fila nunca se vuelve a aplicar, porque la marca local ya es igual. Se aplica en nulo solo si el principal tiene lápida;
    - la lápida que falla al aplicarse (:955-960).
  - `SaveClinicalServiceAsync` compara `ServiceDefaultProducts` por producto, sin borrar y volver a insertar.
  - El cursor de subida sigue siendo el de reloj; cambia en 8.5.
- **Prueba:**
  - Una fila hija que llega sin su principal se aplica en la siguiente sincronización.
  - Un movimiento que llega antes que su lote termina con su lote.
  - Una lápida que falla se reintenta.
  - El contador aparece y vuelve a cero.
- **Riesgo:** bajo-medio.

#### 8.5 · Registro de cambios y cursor por secuencia (H4, H5)
- **Cambia:**
  - `SyncChangeLog(Seq AUTOINCREMENT, EntityType, SyncId, Deleted, UNIQUE(EntityType, SyncId))`, llenado por triggers `AFTER INSERT`, `AFTER UPDATE OF <columnas no derivadas>` y `AFTER DELETE`, generados desde `GetTableMaps`. Comprobado con sqlite3: un borrado en cascada sí dispara el trigger de la tabla hija.
  - Los triggers escriben con `INSERT OR REPLACE`: una fila ya subida y editada otra vez recibe un `Seq` nuevo. Con `DO NOTHING` conservaría un `Seq` menor que el cursor y no subiría.
  - **Cada trigger lleva en su nombre un hash de su definición.** Al arrancar, los que no coinciden se borran y se vuelven a crear en una transacción. `CREATE TRIGGER IF NOT EXISTS` nunca reemplaza un trigger cuya lista de columnas cambió, así que las ediciones a las columnas que agregan los pasos siguientes (`Delta`, `FieldStampsJson`…) no entrarían al registro. Esto corre fuera del atajo de `user_version` (`DatabaseInitializer.Schema.cs:37-40`), así que un respaldo viejo restaurado también los recibe. Respaldo antes de crearlos la primera vez.
  - `UseSqlReturningClause(false)` en las 13 entidades, en esta misma versión: EF Core lo indica para tablas con triggers en SQLite.
  - Una fila `SyncApplyState.Applying` dentro de la transacción de bajada apaga los triggers, para que lo recibido no vuelva a subir. Lo que se decide al bajar y sí debe subir (el resultado de una combinación en 8.22, la reasignación de 8.A) se registra después con `SyncChangeLog.MarkForUpload` y marca nueva.
  - **Subida por `Seq > LastPushedSeq`**, en lugar de `LastPushedLocalUtc`, tabla por tabla en el orden del contrato. El cursor se lee en la misma foto que las filas y vive en `DbMeta` junto a un `DbInstanceId`: guardado fuera de la base, una base reemplazada podría numerar por debajo de él. La primera vez sube todo una vez, de forma idempotente.
  - **Lápidas desde los triggers**, cascadas incluidas. Reemplazan la que hoy pone EF en `StampSyncMetadata`. Su marca es `max(OLD.UpdatedAtUtc + 1 ms, ahora + desfase)`, en el formato de 27 caracteres, con el desfase de `DbMeta` (0 hasta 8.7). Con el reloj crudo, un borrado en cascada con el reloj atrasado perdería contra una edición vieja y la fila reviviría.
  - Todo `SyncId` se pasa a minúsculas antes de subir.
- **Prueba:**
  - Una fila marcada antes del cursor de reloj sube igual (reloj atrasado).
  - Editar una fila ya subida le da un `Seq` mayor que el cursor.
  - Borrar una venta sube las lápidas de sus `SaleItems`, con marca posterior a la de cada fila.
  - Lo recibido no vuelve a subir.
  - Una columna nueva en el modelo regenera su trigger y su edición sube.
  - Un respaldo de v1.19 restaurado queda con los triggers.
  - Un `SyncId` en mayúsculas sube en minúsculas.
- **Riesgo:** medio, porque toca el esquema y el núcleo de la sincronización. Va solo.

#### 8.6 · Descarga eficiente y desempates (F1)
- **Antes:** [T] V12, V14 y V15 con una copia real de ~20k filas.
- **Cambia:**
  - Consulta de página `WHERE "SyncedAtUtc" >= ? AND ("SyncedAtUtc" > ? OR ("SyncedAtUtc" = ? AND "SyncId" > ?)) …`.
  - **Bajada incremental:** una sola petición con la marca de agua (primero), la página 1 de las 13 tablas, las lápidas y el perfil. Solo se piden más páginas de las tablas que llenaron la suya.
  - **Descarga completa:** una tabla por petición. La página 1 de las 13 tablas en una sola respuesta (Patients con sus `Active*Json`, notas con `PrescriptionsJson`, cada valor envuelto como `{"type","value"}`) puede pasar de 10 MB.
  - Tamaño de página por tabla: 100 en Patients y PatientConsultations, 500 en las demás. Se reduce a la mitad ante un error de tamaño o de tiempo.
  - **El cursor de cada tabla se guarda después de cada página aplicada**, sin pasar de la marca de agua de esa sincronización. Un corte a media descarga continúa donde iba.
  - Si un upsert afecta 0 filas, se vuelve a leer esa fila del servidor y se aplica **aunque las marcas empaten**; si ya no existe, se busca su lápida y se aplica. No se hace durante la primera subida por `Seq` (8.5), en la que casi todo afecta 0 filas y costaría ~20k lecturas de más.
  - Si V14 confirma que una petición `batch` con BEGIN/COMMIT es atómica: la bajada incremental lee la marca de agua y las tablas como una sola foto, y la subida manda cada operación completa (venta con renglones y movimientos; lápida con su `DELETE`) en una transacción. Antes se enseña a `FakeHranaServer`. Si no, se queda como hoy y lo cubren las filas apartadas.
- **Prueba:**
  - Sincronización sin cambios = 1 petición.
  - 1,200 filas con el mismo `SyncedAtUtc` y página de 100: no se pierde ninguna.
  - Ningún SCAN en los planes (la revisión de 8.2 pasa a fallar).
  - El escenario de empate converge.
  - La descarga completa cuesta ~N lecturas y termina con un tope de 1 MB por respuesta en el simulador.
  - Un corte a la mitad reanuda sin volver a leer lo aplicado.
  - La batería actual de punta a punta pasa.
- **Riesgo:** medio-bajo.

#### 8.7 · Hora del servidor e identidad de la nube (A6, H5)
- **Cambia:**
  - La primera instrucción de cada sincronización regresa la hora del servidor y `generation`, `generationReady`, `protocol`, `minClientProtocol` y `clinicId`.
  - `ISyncClock` marca con `max(UtcNow + desfase, guardada + 1 ms)` dentro de `StampSyncMetadata`. El desfase se guarda en `DbMeta` (lo usan los triggers de 8.5). Si pasa de 2 min, avisa "La hora de esta computadora difiere N min de la nube".
  - **Marcas en el futuro:** antes de subir, toda fila local pendiente con marca mayor que la hora del servidor + 2 min se vuelve a marcar con la hora del servidor. Una sola vez, la PC repara igual las de la nube (`UPDATE … WHERE "UpdatedAtUtc" > ?`). Así una marca adelantada, de v1.19 o de un reloj que cambió sin conexión, deja de ganar todos los conflictos.
  - Si `minClientProtocol` es mayor que el del programa, se detiene con "Actualice TPVC en esta computadora".
  - **Identidad:** `DbMeta.clinicId` debe ser igual al de la nube. Si la nube ya tiene uno y la base local no (el camino de hoy: pegar URL y token), la sincronización no arranca y ofrece "Unirse a una nube existente" (8.8), "Reemplazar con la nube" (teclear CONFIRMAR, respaldo antes) o "Esta PC ya usaba esta nube". Esta última solo procede si el reporte de abajo no encuentra filas locales que falten en la nube.
  - **Columnas nuevas:** `DbMeta` guarda la revisión del contrato con la que se bajó cada tabla. Si el modelo conoce columnas que esa revisión no tenía, la tabla se baja completa antes de subir.
  - **"Sincronización completa" con reporte:** filas apartadas, `COUNT(*)` local contra remoto por tabla (una vez, ~20k lecturas) y tablas `legacy_v1_*`. Solo si sale limpio escribe `clinicId` (una vez, también en `DbMeta`), `protocol = 2`, `minClientProtocol = 2`, `manifest`, `generation` y `generationReady` (si faltan) y `fullUploadAtUtc`.
- **Prueba:**
  - Con el servidor adelantado o atrasado 10 min, las marcas siguen al servidor.
  - Una fila marcada 3 h en el futuro se repara antes de subir.
  - La sincronización completa escribe las claves; una fila sin su principal bloquea `fullUploadAtUtc`.
  - `manifest` es igual a `schema.json`.
  - Una base con datos propios no sincroniza con una nube de otro `clinicId`.
  - Una columna nueva provoca la descarga completa de su tabla y no pisa el valor remoto.
- **Riesgo:** bajo-medio.

#### 8.8 · Siembra segura, unirse y recuperar desde la nube (A2)
- **Cambia:**
  - `Initialize(db, seedDemoData: false)` siempre. Los datos demo solo salen del asistente (`WizardSeedDemoData`).
  - En bases nuevas, el catálogo sembrado lleva `SyncId` UUIDv5 sobre un espacio de nombres TPVC: `Category:<nombre>`, `ClinicalService:<código>`, `ServiceDefaultProduct:<código>|<código del producto>`. Además `UpdatedAtUtc = 2000-01-01`, insertado con `ApplyingRemoteChanges` para que el sello no lo cambie. Así cualquier edición o borrado real le gana a una siembra repetida.
  - **"Unirse a una nube existente":**
    1. Lee el `clinicId` de la nube.
    2. Exige una base intacta: cero pacientes, consultas, citas, ventas, movimientos, lotes, productos, plantillas y reglas de dosis, y no más catálogo del que siembra.
    3. Respalda.
    4. Borra el catálogo local sin lápidas.
    5. Pone `CloudJoined = true` y el `clinicId` en `DbMeta`. `CloudJoined` apaga toda siembra para siempre, incluida la que repite `SeedClinicalServices`.
    6. Baja todo sin omitir sus propias filas.
    7. Lleva el cursor al `Seq` actual.

    Si la base no está intacta, se niega y ofrece "Reemplazar con la nube": hay que teclear CONFIRMAR y se respalda antes.
  - **"Recuperar desde la nube"** es la misma descarga, a una base vacía (PC nueva o descompuesta).
- **Prueba:**
  - Dos bases nuevas siembran y sincronizan, y quedan 5 categorías y 5 servicios.
  - Una categoría sembrada que se borró sigue borrada después de que una tercera instalación sincroniza.
  - Un segundo arranque sin pacientes no siembra nada.
  - Unirse no crea duplicados ni sube lápidas.
  - Un reinicio a media descarga no vuelve a sembrar.
  - Una base con datos reales se rechaza.
  - Recuperar da los mismos conteos.
- **Riesgo:** medio, porque borra filas locales. Hay respaldo antes.

#### 8.9 · Restaurar con la nube activa (H6)
- **Cambia:** una restauración (`DataImportService`) con la nube activa pregunta:
  - **"Reemplazar la nube con este respaldo":**
    1. Antes de borrar, baja de la nube las filas que no están en el respaldo o son más nuevas, muestra los conteos por tabla ("se perderán 12 notas y 30 ventas de la tablet") y las guarda en un archivo `.db`.
    2. Guarda "reemplazo en curso" en `DbMeta`. Si TPVC se cierra, lo continúa al abrir.
    3. Escribe la generación nueva.
    4. `DELETE` remoto de las tablas sincronizadas y de las lápidas.
    5. Subida completa (~0.2% del mes) y reporte de 8.7.
    6. Al final escribe `generationReady` con la generación nueva y un `fullUploadAtUtc` nuevo.
  - **"Desconectar esta PC de la nube".**
  - **Cerca de generación.** Cada upsert, lápida y `DELETE` de la subida lleva `AND (SELECT "Value" FROM _tpvc_meta WHERE "Key"='generation') = ?`, y cada petición termina leyendo la generación. Si cambió, el equipo se detiene. Sin esto, los trozos de 80 instrucciones que otro equipo seguía subiendo caerían en la nube restaurada: filas posteriores al respaldo, y lápidas que borran filas recién restauradas.
  - **Los demás equipos**, al ver otra generación: dejan de subir y guardan lo que no habían subido, cada fila con el protocolo con que se escribió. Solo cuando `generationReady` es igual a la generación nueva borran y vuelven a bajar todo. Después ofrecen "Volver a aplicar N cambios", que inserta esas filas con marca nueva. El archivo de la PC del paso 1 se puede volver a aplicar igual.
  - Aquí se implementa en la PC y queda especificado para Kotlin.
- **Prueba:**
  - La copia remota queda igual al respaldo.
  - Un trozo atrasado de la generación anterior se rechaza.
  - Una segunda PC simulada vuelve a bajar todo una sola vez, y sus cambios sin subir se pueden volver a aplicar.
  - Un cierre a media subida se continúa, y nadie baja la nube incompleta.
  - Los conteos de lo que se perdería coinciden con lo guardado.
- **Riesgo:** medio, porque la operación remota es destructiva y la confirmación debe ser clara.

### Etapa 2 · Equipos, documentos y vinculación (lo que necesita el celular)

#### 8.10 · Registro de equipos, PC principal y compatibilidad
- **Cambia:**
  - Tabla `DeviceRegistry` (§2.3), escrita solo por la PC. Sus filas nunca se borran: revocar pone `RevokedAtUtc` y `RevokedReason`. El siguiente código de cada letra es el mayor registrado + 1, así que nunca se reutiliza.
  - Columnas nuevas en `RegisteredDevices` (§2.3), con `ALTER` explícito. Todas aceptan nulo, así que el latido de una PC v1.19 (upsert con columnas explícitas, `ConsultationHandoffService.cs:157-160`) sigue igual. Cada subida termina escribiendo `LastPushUtc` con la hora del servidor, y el latido lleva `PendingCount` y `OldestPendingUtc`.
  - **PC principal:** reclama `_tpvc_meta('primaryDeviceId')` con `INSERT … ON CONFLICT DO NOTHING`. "Esta PC reemplaza a la principal", para una PC nueva que recuperó la nube, toma la clave y revoca a la anterior.
  - **Identificador clonado.** El `DeviceId` vive en `turso_config.json` (`Models/TursoConfig.cs:92`). Si se copia `%LocalAppData%\TPVC` a otra máquina, DPAPI falla (hoy da un token vacío en silencio) y el médico vuelve a pegar el token con el mismo `DeviceId`: las dos PC omitirían las filas de la otra. Se genera un `DeviceId` nuevo cuando falla DPAPI o cambia la máquina, y cada arranque escribe un `InstanceNonce`. Si el latido encuentra un nonce ajeno reciente con su `DeviceId`, avisa y genera uno nuevo.
  - `ToDevice`: compatible si `HandoffProtocol >= 1`; si está vacío, la regla de `AppVersion`. El rol sale de `DeviceRegistry` (de `RegisteredDevices` para las PC). Los revocados se excluyen.
  - Lista "Equipos vinculados": nombre, rol, código, "En línea / visto hace…", cambios pendientes y *Quitar*. `DevicePermissions` gana `CanViewClinicalRecords` y `CanShareDocuments`.
  - La limpieza de cada 6 h (solo la PC) borra de `RegisteredDevices` los revocados y los que no se ven hace más de 60 días. Cada revinculación o reinstalación agrega un `DeviceId`, y la revisión lee la tabla completa.
  - Se documenta que el esquema remoto solo lo crea o migra la PC.
- **Prueba:**
  - Una fila de Kotlin con `AppVersion` 0.3.0 y `HandoffProtocol` 1 cuenta como compatible.
  - Los revocados no aparecen.
  - El latido de una PC v1.19 sigue funcionando con las columnas nuevas, y una fila vieja sin ellas sigue siendo compatible.
  - Dos instalaciones con el mismo `DeviceId` se detectan; un descifrado DPAPI fallido da un `DeviceId` nuevo.
  - [W] Captura de la lista.
- **Riesgo:** bajo-medio, porque hace `ALTER` sobre la base remota en uso (solo agrega columnas).

#### 8.11 · Generadores puros, reloj inyectable y vectores del celular
- **Cambia:**
  - `Services/Documents/PrescriptionHtmlBuilder`, que sale de `PrintingService.BuildPrescriptionHtml` (`PrintingService.Prescription.cs:561`, que hoy vive en una clase con WPF).
  - `TicketText` (42 columnas) y `TicketHtml`, a partir de `SaleReceipt`, con la misma salida que hoy.
  - `VitalSignsRules` (aviso de signos e IMC), `PatientFormRules` (validación, `ParseFullName`, `TryParseBirthDate`, `IsValidEmail`) y `HandoffPayloadCodec`, que sale de `ConsultationHandoffViewModel`.
  - `TimeProvider` donde hoy se lee la fecha directamente: edad, `IsUpcoming`, `DaysRemaining` y resumen.
  - Vectores que necesita el celular, en `contracts/golden/` con la forma `{rule, culture: "es-MX", today, now, cases:[{name, input, expected}]}`: `cash-cut`, `alerts`, `suggested-purchase`, `prescription-sheet`, `prescription-html-fields` (valores `data-field`) y `ticket-text`. De ellos sale el PDF que el celular dibuja con `PdfDocument`.
  - Reglas para escribir vectores (valen también para 8.26):
    - Todo redondeo lleva un caso de punto medio: .NET redondea al par, así que 5.625 da 5.6 (`HALF_EVEN` en Kotlin).
    - Los desempates llevan nombres con acentos (`Collator` es-MX).
    - Se generan en UTC y en `America/Mexico_City`.
- **Prueba:**
  - Salidas idénticas.
  - `ConsultationHandoffTests` y `handoff-payload.golden.json` sin cambios.
  - Prueba de arquitectura: `Services/Documents` sin WPF.
- **Riesgo:** bajo, porque solo mueve código.

#### 8.12 · Membrete en la nube (H9)
- **Cambia:**
  - Tabla `ClinicAssets` (tipo, hash, tipo MIME, contenido, `UpdatedAtUtc`). La PC la escribe solo cuando cambia una imagen.
  - `ClinicProfiles` gana `CrestImageHash` y `HeaderImageHash`.
  - Los equipos bajan una imagen solo cuando cambia su hash.
- **Prueba:**
  - Cambiar el escudo sube una sola vez.
  - Las bajadas normales no leen `ClinicAssets`.
  - Una PC que se une recibe las imágenes.
- **Riesgo:** bajo.

#### 8.13 · Kit de conformidad de lectura
- **Cambia:**
  - `contracts/hrana/*.jsonl`: intercambios grabados por `FakeHranaServer`, con los valores generados reemplazados por marcadores. Primera sincronización, incremental, descarga completa por tabla, empates en la llave, cambio de generación, `BLOCKED` y 401.
  - El SQL de cada instrucción que Kotlin tiene que repetir va **literal** en `contracts/hrana/` (la revisión de consultas se agrega en 8.24). SQLite usa un índice parcial solo si el `WHERE` repite su condición, así que una consulta escrita de otra forma recorrería la tabla.
  - `docs/FAKE_HRANA_SPEC.md`: qué copia el simulador de Turso. Enteros como texto (si no, HTTP 400), un resultado por instrucción, sin transacción, un escritor a la vez, reloj inyectable, lecturas cobradas, tope de respuesta, `BLOCKED` y la revisión de planes.
  - `FakeHranaServer` expuesto como servidor HTTP de pruebas (`TPVC_FAKE_TURSO_HOST=1 dotnet test --filter TestCategory=FakeTursoHost`), para que la CI de Kotlin y la PC sincronicen contra el mismo servidor en un solo job. Vive en el proyecto de pruebas, escucha solo en 127.0.0.1 y nunca entra a `TPVC.exe`.
- **Prueba:** todo corre en la CI de C# en Linux; los planes de las consultas literales no tienen SCAN.
- **Riesgo:** ninguno.

#### 8.14 · Tokens por equipo
- **Antes:** [T] V1–V3, V6 y V8 (§4.2).
- **Cambia:**
  - Nivel A: cliente de la API de la plataforma (crear token y rotar), con el token de grupo protegido con DPAPI. La PC renueva su propio token. Nivel C: campos para pegar los tokens de la tablet, del celular y de la base de vinculación.
  - `FakeHranaServer` con un mapa de tokens:
    - completo, de solo lectura o con permisos por tabla (con el autorizador de SQLite vía `SQLitePCLRaw`);
    - un token vencido o desconocido recibe 401.
  - `FakeTursoPlatformApi`, que comparte ese mapa.
  - `TursoClient` traduce esas respuestas a `AUTH_EXPIRED` y `AUTH_FORBIDDEN`, con mensajes en español, y lee el `exp` del JWT si V6 lo permite.
- **Prueba:**
  - Un cliente de solo lectura falla limpio.
  - El esquema se rechaza para un token de tablet limitado.
  - Con los permisos de §4.3 la tablet sube, aplica lápidas y no toca el catálogo.
  - La renovación funciona con `TimeProvider`.
  - Después de una rotación, los tokens viejos reciben 401 y la PC crea uno nuevo y sigue.
- **Riesgo:** medio.

#### 8.15 · Vincular por QR
- **Antes:** [T] V5 y V13.
- **Cambia:**
  - Base de vinculación con la tabla `DevicePairings`, en el mismo grupo. La base la crea una vez quien administra la cuenta (panel o CLI), igual que la del consultorio; la tabla la crea la PC, que es la dueña del esquema.
  - QR con QRCoder (licencia MIT) y el flujo de §4.4: código de 10 dígitos, reclamo, código de confirmación y credencial sellada.
  - "Unirse con código" para otra PC, que recibe su código (`P2`…).
  - `PairingReferenceClient` en C# dentro de las pruebas, como especificación ejecutable para Kotlin.
  - Vectores `pairing-qr`, `seal` y `confirmation-code`.
  - Capítulo "Tablet y celular" en el manual.
- **Prueba:**
  - El flujo completo.
  - Dos reclamos al mismo tiempo: gana uno.
  - Rechazar no escribe credencial.
  - Un QR vencido se rechaza; un cifrado alterado se rechaza.
  - El QR no contiene ningún token de la base del consultorio.
  - [W] Capturas en los 4 temas.
- **Riesgo:** medio-alto (criptografía y experiencia de uso). Lo mitigan los vectores.

#### 8.16 · Renovación de tokens, quitar y equipo perdido
- **Antes:** [T] V4 y V11.
- **Cambia:**
  - Renovación automática del nivel A (§4.7). Es requisito antes de la primera versión de la app.
  - *Quitar* con aviso de cambios pendientes; el equipo sube lo pendiente antes de borrarse, o lo exporta.
  - "Perdí este equipo" con revocar y "Cortar acceso ahora" (§4.7), estado "Vuelva a vincular" en la lista, e instrucciones para el nivel C.
  - Reglas ante un 401 que distinguen vencido de revocado.
- **Prueba:**
  - La PC sella una credencial nueva 30 días antes del vencimiento y `PairingReferenceClient` la recoge.
  - Después de rotar, el token de la tablet recibe 401 y la PC sigue sincronizando.
  - Volver a vincular con el mismo `DeviceId` conserva las filas pendientes.
  - Un token vencido nunca borra la base.
  - *Quitar* con cambios pendientes los sube antes de borrar.
- **Riesgo:** medio.

**Corte: primera versión del celular.** Con 8.2–8.16 puede salir el celular (K0–K4, §7.5): solo baja, se vincula con QR, y comparte la receta y el ticket. Prueba en equipos reales Hrana, la vinculación, la criptografía y el PDF antes de escribir la tablet. Cuando 8.20 suba `protocol` a 3, el celular se actualiza por la prueba interna de Play.

### Etapa 3 · Datos correctos con varios equipos que escriben

#### 8.17 · Folios con código de equipo (A3)
- **Cambia:**
  - Una `FolioFactory(código, reloj, existentes, último guardado)` pura reemplaza todos los puntos que generan folios: `InventoryService` 626, 1181 y 1334; `ActiveConsultation.cs:39`; `ConsultationViewModel.cs:365` y 462; `DataImportService.cs:817` y 888; `PatientFormViewModel.cs:593`.
  - La PC principal (`primaryDeviceId`, 8.10) conserva el código vacío y sus folios no cambian. Los demás llevan el código de `DeviceRegistry`. Una PC sin `primaryDeviceId` propio ni código no crea folios: "Vincule esta PC desde la principal o márquela como la que la reemplaza".
  - Cada equipo guarda el último número de cada serie **fuera de la base** (`folios.json` en `%LocalAppData%\TPVC`; en Android, un archivo que no se borra al volver a bajar todo). `FolioFactory` usa el mayor entre ese número y el máximo local. Así restaurar un respaldo no repite un EXP o un TKT ya impreso.
  - `PatientRecord.RecordNumber` y `Sale.Folio` pasan de índice único a índice normal (`AppDbContext.cs:215-217`, 253-255), y el `-R` deja de aplicarse. Las filas viejas con `-R` se quedan. Un folio repetido (filas viejas o tecleado a mano) aparece en un aviso de la PC.
- **Prueba:**
  - Dos equipos sin conexión (código vacío y `T1`) crean pacientes, tickets y notas sin choques.
  - Los lectores ignoran los folios del otro equipo.
  - Restaurar un respaldo viejo no repite el siguiente EXP.
  - Dos filas con el mismo folio sincronizan sin renombrarse y aparecen en el aviso.
  - Vector `folios.json`.
- **Riesgo:** bajo-medio.

#### 8.18 · SyncIds y renglones con identidad en las hojas del paciente (A4)
- **Cambia:**
  - `PatientSupplyItem.ChargedLineKind` (`producto`/`libre`, las palabras de la consulta entre equipos), `ChargedLineSyncId` y `PatientServiceItem.ServiceSyncId`. Los escriben `ConsultationAccount.cs:138` y `ClinicalServicesSheetViewModel.cs:200`.
  - **`RowId` (GUID) en cada renglón** de las tres hojas `Active*Json`, para combinarlas por renglón en 8.22. Un renglón viejo recibe un `RowId` determinista (UUIDv5 del paciente, la hoja, la posición y el nombre), así dos equipos le ponen el mismo.
  - Los lectores (`SupplyLineId`, `FindService`) resuelven en este orden:
    1. el `SyncId`;
    2. `libre`, con `FreeChargeId(nombre)` local;
    3. el Id viejo solo si ese producto local tiene el mismo nombre normalizado;
    4. si no, por nombre.
  - Recibir una consulta también traduce las hojas.
  - `[JsonExtensionData]` en los renglones, para que una reescritura de la PC no tire los campos que agregue un equipo más nuevo.
  - La PC sigue escribiendo `ChargedLineId` para las PC viejas. No hay relleno.
- **Prueba:**
  - Dos `Fase6Clinic` con Id distintos: recibir una consulta y usar + / − y quitar cambia el renglón correcto.
  - Un campo desconocido sobrevive a una reescritura.
  - El mismo renglón viejo recibe el mismo `RowId` en dos equipos.
  - Vector `synced-json-columns.json`.
- **Riesgo:** medio-bajo.

#### 8.19 · Kardex completo (A1, primera parte)
- **Cambia:**
  - En `InventoryMovement`: `Delta` con signo (0 marca una fila vieja, que sigue la regla de `IsEntry`), `SaleItemId` (viaja como `SaleItemSyncId`) y `MovementType.SaldoInicial = 8`, que va al final porque el enum se guarda como entero. Las ventas nuevas llevan `Sale.LedgerVersion = 1`.
  - `DeductFromBatches` regresa una asignación por lote más el resto sin lote. `RegisterQuickConsultationUsageAsync`, `RegisterSaleAsync` y la salida de `RegisterMovementAsync` escriben un movimiento por lote y otro con `BatchId = null` para el resto.
  - `CancelSaleAsync` revierte cada movimiento original en su propio lote:
    - la regla nueva o la vieja se elige por `Sale.LedgerVersion`, no por si la búsqueda encuentra algo;
    - nueva: encuentra los movimientos por `SaleItemId`; `SyncId` = UUIDv5(`"return:" + SyncId del movimiento original`);
    - vieja: por producto más "Venta POS {folio}" en `Reason`; `SyncId` = UUIDv5(`"return:" + SyncId de la venta + ":" + SyncId del producto`);
    - se niega si los movimientos de una venta nueva no están completos (Σ|Delta| de un renglón distinta de su cantidad): "Sincronice primero";
    - si la devolución determinista ya existe, la cancelación cuenta como hecha. Dos equipos que cancelan el mismo ticket sin conexión escriben filas idénticas, y la nube conserva una sola.
  - **Los lotes ya no se borran.** `DeleteBatchAsync` (hoy los borra físicamente y topa con `Math.Min`, :387) cierra el lote (`ProductBatch.ClosedAtUtc`) y escribe `Delta = −libro(lote)` con su `BatchId`. Un movimiento tardío de otro equipo sigue en su lote, que queda visible en negativo.
  - Diálogo de producto con `OriginalStock`: si el campo no cambió, la existencia no se toca.
  - `SaveBatchAsync`, que solo usan las pruebas, se quita.
  - `StockLedger.VerifyAsync` reporta la diferencia entre lo guardado y el libro, sin cambiar nada.
  - Las existencias se siguen guardando como hoy; el libro corre al lado para verificarlas.
- **Prueba:**
  - Secuencias de operaciones al azar mantienen guardado = libro.
  - Cancelar dos veces en dos equipos deja una sola devolución.
  - Una venta con movimientos incompletos no se cancela.
  - Una venta que baja con el diálogo abierto no se revierte.
  - Un movimiento que llega después de cerrar su lote queda en ese lote.
- **Riesgo:** medio. El Kardex muestra más renglones y `MovementsToday` cuenta renglones (`InventoryService.cs:1004`). En este paso se decide si se agrupan por operación.

#### 8.20 · Existencias como libro de movimientos (A1, segunda parte)
- **Requisito:** que `VerifyAsync` reporte cero diferencias en la PC del médico durante varios días después de 8.19, y un respaldo previo.
- **Cambia:**
  - **Barrera.** Se niega a convertir si `RegisteredDevices` muestra otra `WindowsDesktop` vista en los últimos 30 días con una versión anterior: "Actualice la otra PC" o "Esa PC ya no existe". Corre solo en la PC principal, después de una sincronización completa y sin cambios pendientes en ningún equipo (`PendingCount`). Una PC v1.19 no lee `protocol`, y dos PC calcularían saldos iniciales sobre fotos distintas. Si una PC vieja aparece después: alerta y, en el nivel A, rotación.
  - **Saldos iniciales, una sola vez:**
    - para cada lote: `Delta = cantidad − libro(lote)`;
    - para las piezas sin lote: `Delta = CurrentStock − Σ lotes − libro(sin lote)`;
    - se omiten los ceros. Tipo `SaldoInicial`, `SyncId` UUIDv5(`"opening:v1:" + SyncId`), `StockLedgerVersion = 1` en `DbMeta`.
    - Las bases nuevas, incluida la de Kotlin, empiezan en 1 y nunca crean saldos iniciales.
    - Esto absorbe los defectos viejos: FEFO de un solo lote, devoluciones a un lote adivinado, productos demo sin lote (`DatabaseInitializer.cs:282-296`).
  - **El conteo como ancla.** `MovementType.Conteo = 9` con `CountedQuantity`, por lote o para la parte sin lote. Lo escriben cerrar un lote (conteo 0), el ajuste por conteo y un cambio de existencia en el diálogo de producto (parte sin lote = tecleado − lotes). Lo derivado es el último conteo más la suma de los movimientos posteriores por su `UpdatedAtUtc` (los movimientos no se editan, así que es su marca de creación corregida). Una venta hecha sin conexión antes del conteo, que llega después, no se descuenta dos veces. Antes de contar, la PC sincroniza y avisa si otro equipo reporta cambios pendientes.
  - **`StockLedger.RecomputeAsync(db, productIds)`:** un `UPDATE … (SELECT …)` en SQL directo, que no deja sello ni registro. Corre:
    - dentro de cada escritura local de existencias;
    - después de una bajada que aplicó Products, ProductBatches o InventoryMovements;
    - completo al arrancar.
  - `TableMap.DerivedColumns` = {`Products.CurrentStock`, `ProductBatches.Quantity`, `ProductBatches.IsDepleted`}: suben como información y nunca se aplican al bajar.
  - Los caminos de existencias dejan de tocar `CurrentStock` y `UpdatedAt` del producto con EF. Una venta en la tablet ya no le gana a un cambio de precio hecho en la PC.
  - **Negativos:**
    - Se permiten y nunca se compensan solos: varias compensaciones de varios equipos contarían doble.
    - `EnsureStock` bloquea nuevas ventas con existencias ≤ 0, y FEFO salta los lotes ≤ 0.
    - Insignia "−1 · revisar inventario" y alerta nueva en `GetSystemAlertsSummaryAsync`, **también por lote negativo** aunque el total del producto sea positivo.
    - Se corrige con un conteo en la PC.
  - `_tpvc_meta`: `protocol = 3`, `minClientProtocol = 3`.
- **Prueba:**
  - Una base vieja con diferencias: lo guardado no cambia y derivado = guardado.
  - La última pieza vendida en dos equipos deja −1 y la alerta.
  - Una venta sin conexión anterior a un conteo no se descuenta dos veces; cerrar un lote con una venta tardía deja el total correcto.
  - Un cambio de precio en la PC sobrevive a una venta en la tablet.
  - El recálculo no deja sellos ni renglones en el registro.
  - Con otra PC vieja a la vista, la conversión se niega.
  - Vectores `stock-ledger.json`, `stock-count.json` y `sale-fefo.json`.
- **Riesgo:** alto.

#### 8.21 · Guardar solo lo que cambió (H1a)
- **Cambia:**
  - `SavePatientAsync(editado, base)` copia sobre la fila recién leída solo los campos donde editado ≠ base, y regresa la fila fresca para refrescar la copia del espacio de trabajo.
  - Quienes guardan pasan la foto tomada al cargar: el alta y edición, las hojas y el resumen de la nota.
- **Prueba:**
  - Guardar una hoja con una copia vieja conserva `NextAppointment*`.
  - `PacientesTests` y las pruebas de flujos pasan.
  - [W] Las capturas no cambian.
- **Riesgo:** alto, porque es guardar el paciente. Va solo.

#### 8.22 · Combinación por campo en pacientes (H1b)
- **Cambia:**
  - Columna `Patients.FieldStampsJson` = {campo: marca}, mantenida por `StampSyncMetadata`. Cubre sola `AddConsultationNoteAsync` y `SaveAppointmentAsync`.
  - Al bajar, `PatientRecord` se salta el LWW por fila (`TursoSyncService.cs:823`) y usa `PatientMerge.Merge`, que es pura:
    - por campo gana la marca mayor; una marca que falta cuenta como mínima;
    - **escritura sin marcas:** si el `UpdatedAtUtc` de la fila es mayor que la marca máxima de sus campos (una PC v1.19 o una escritura que no pasó por el sello), los campos que difieren toman `UpdatedAtUtc` como marca. Equivale al LWW de hoy, en lugar de una moneda al aire;
    - con marcas iguales y valores distintos gana el valor serializado mayor en orden ordinal, así los dos lados convergen;
    - **hojas `Active*Json` por renglón:** unión por `RowId`, marca por renglón y bandera de quitado; los quitados se purgan a los 90 días;
    - las llaves desconocidas de `FieldStampsJson` se conservan.
  - Si el resultado difiere de la versión remota **por significado**, la marca queda en `max(L, R) + 1 ms` y la fila se registra para subir (`MarkForUpload`, 8.5). Se compara en forma canónica: llaves ordenadas, nombres de columna remota y fechas de 27 caracteres. Comparado como texto, C# y Kotlin verían siempre una diferencia y volverían a subir sin fin.
  - **Resumen derivado:** `NextAppointment*` y `LastVisit` se recalculan desde Citas y Consultas después de cada bajada y cada guardado, y entran a `DerivedColumns`.
  - **Aceptar una consulta:** la copia lleva las marcas por campo del paciente (al menos las tres hojas y el resumen) y el `UpdatedAtUtc` de la nota. Quien recibe exige que cada marca local sea mayor o igual; si no, "Intente de nuevo en unos segundos".
  - La nube sigue con LWW por fila. Una subida vieja que rechaza se repara en la siguiente combinación.
  - Si `RegisteredDevices` muestra otra PC sin actualizar, la PC avisa que sus cambios se combinan como fila completa.
- **Prueba:**
  - Una edición de alergias sin conexión en otro equipo y un cambio de teléfono en la PC sobreviven los dos.
  - Un renglón agregado en la PC y otro en la tablet a la misma hoja sobreviven los dos.
  - Una escritura de v1.19 gana con su `UpdatedAtUtc`.
  - Prueba de propiedades: cualquier orden de intercalado converge y no deja subidas repetidas.
  - La próxima cita es la misma en los dos equipos.
  - Aceptar con un campo local más viejo pide esperar.
  - Vectores `field-stamps.json`, `patient-merge.json`, `canonical-json.json`, `patient-summary.json` y `handoff-accept.json`.
- **Riesgo:** medio-alto.

#### 8.23 · Empalmes, duplicados y sincronización rápida (H2, H3, H11). Formato congelado
- **Cambia:**
  - `AppointmentRules.Overlaps`, pura: misma fecha, ninguna "Cancelada" y [Hora, Hora + Duración) se cruzan, usando `ParseAppointmentTime`.
  - Antes de agendar, con red, una sincronización rápida (≤ 3 s) y luego la revisión local: "Ya hay una cita a las 10:30 con … ¿Agendar de todos modos?". Después de bajar, marca "Empalme" en la agenda y un aviso.
  - Regla de duplicados: aviso al crear y banner en la PC con los pares recientes después de bajar.
  - Columna `Patients.MergedIntoSyncId`, que se agrega ya aunque la herramienta de unir sea opcional.
  - Sincronización rápida (≤ 3 s) antes de cobrar, con red. Sin red, se cobra igual o se pide red, según la pregunta 2.
  - Sube `contract`. **`contracts/sync/schema.json` queda congelado como base de la tablet.** A partir de aquí solo se admiten cambios aditivos.
- **Prueba:**
  - Pruebas de las reglas y de la detección después de bajar.
  - Se puede cobrar sin conexión (si la pregunta 2 lo permite).
  - Vectores `appointment-overlap.json` y `duplicate-patients.json`.
  - [W] Capturas de los avisos.
- **Riesgo:** bajo.

### Etapa 4 · Cadencia y kit para la tablet

#### 8.24 · Cadencia y marca de cambios (A5, F2)
- **Cambia:**
  - `Services/SyncScheduler.cs` con la cadencia de §3.3.
  - La revisión lee `LastPushUtc` de los otros equipos y dispara una sincronización.
  - Latido cada 60 s.
  - Revisión rápida solo con un compañero o con un envío pendiente. La lista de compañeros se refresca cada 10 min agregando el SELECT de `RegisteredDevices` a una revisión.
  - Índice parcial de F2, y el SQL literal de la revisión en `contracts/hrana/`.
  - La limpieza de cada 6 h sigue solo en la PC.
- **Prueba (con tiempo simulado):**
  - El equipo B edita y la PC lo ve en ≤ 4 s en Pacientes y en ≤ 30 s fuera de ahí.
  - Descarga de seguridad a los 10 min.
  - Reintentos crecientes sin red.
  - Nada corre con la nube apagada.
  - El plan usa el índice parcial.
  - 60 latidos por hora.
  - Un día normal simulado de tres equipos (supuestos de §3.2) cuesta menos de 200k lecturas y 5k escrituras.
  - Un cambio de generación provoca exactamente una descarga completa por equipo.
- **Riesgo:** bajo. Va a la lista D: refrescar con un formulario abierto (lo cuida `CanReplaceSelectedPatientInstance`).

#### 8.25 · Kit de conformidad de escritura
- **Cambia:**
  - `contracts/scenarios/*.json`: pasos por estado (editar, sincronizar en el equipo A o B, desfase o salto de reloj, corte a media subida) con las filas esperadas en la nube y en cada equipo, por `SyncId`. Cubren los escenarios de varios equipos de 8.4, 8.5, 8.9, 8.17, 8.19, 8.20 y 8.22. No dependen del texto del SQL, salvo las consultas literales de 8.13 y 8.24.
  - `contracts/hrana/*.jsonl` de escritura: subida, conflicto de empate, lápida contra edición, cerca de generación, y la consulta entre equipos: enviar, aceptar, respuesta perdida y recuperar.
  - Vectores de la consulta entre equipos: copia de trabajo con reloj fijo y las fechas tal cual (la de `TestAssets/fase6` las cambia por marcadores), traducción (`FindCatalogRef`), `linked-device.json` y `handoff-accept.json`.
- **Prueba:** todo corre en la CI de C# en Linux.
- **Riesgo:** ninguno.

#### 8.26 · Vectores de reglas del negocio
- **Cambia:** `contracts/golden/` con la forma y las reglas de 8.11:
  - búsqueda: `text-search`, `rank-patients`, `rank-products` (con el prefijo `3*`), `cie10-catalog` y `cie10-search`, `catalog-matching`;
  - seguridad e identidad: `allergy-check`, `patient-identity`, `date-mask`;
  - ficha y agenda: `chart-antecedents`, `chart-timeline`, `latest-vitals`, `vitals-evolution`, `agenda`;
  - consulta: `vitals`, `pediatric-dose`, `consultation-account`, `sheet-charges`, `checkout`.
- **Prueba:** cambiar una regla sin regenerar rompe la CI.
- **Riesgo:** bajo.

### Pasos opcionales

| Paso | Cuándo | Qué |
|---|---|---|
| 8.A Unir expedientes duplicados | Si aparecen duplicados | Solo en la PC. Se elige el expediente que queda y los campos, se reasignan consultas y citas, y el otro queda `IsActive = false` con `MergedIntoSyncId`. Cualquier equipo reasigna después de bajar las filas hijas que lleguen tarde, y lo registra para subir (8.5). Los números de expediente nunca se reutilizan. |
| 8.B Una sola serie de folios | Pregunta 1 = no | Números asignados en línea con un contador condicionado en Turso; la letra del equipo solo sin conexión. |
| 8.C Avisos con la pantalla apagada | Si el médico lo pide | Servicio de notificaciones (por ejemplo Firebase). Es infraestructura nueva. |

---

## 7. App Android en Kotlin (fase siguiente; aquí solo el esqueleto)

**Ubicación:** carpeta `android/` en el mismo repositorio, para leer `contracts/` directamente. Flujo de CI propio, `android.yml` (`ubuntu-latest`, JDK 17, pruebas de JVM y lint, sin emulador). minSdk 26. Una sola app para la tablet y el celular; el rol sale de `DeviceRegistry`.

### 7.1 Módulos

Se empieza con cuatro módulos de Gradle y se dividen cuando crezcan:
- `:sync`: lee `contracts/sync/schema.json`, cliente Hrana, motor v2 (cola de salida, subida, bajada, LWW, lápidas, filas apartadas, combinación de pacientes, recálculo de existencias), cliente de la consulta entre equipos, y vinculación (QR, código, criptografía y credencial). Una prueba compara las entidades de Room contra el manifiesto.
- `:domain`: reglas portadas (búsqueda, cuenta y hojas, FEFO, folios, signos, dosis, alergias, reportes, receta y ticket), con sus vectores.
- `:data`: Room + SQLCipher y DAO.
- `:app`: pantallas por función (pacientes, consulta, agenda, punto de venta, reportes, compartir), navegación por rol, bloqueo y arranque.

### 7.2 Pila

| Pieza | Elección | Por qué |
|---|---|---|
| Interfaz | Jetpack Compose + Material 3 | Un solo código para la tablet (ancha) y el celular (angosto). Se prueba con Robolectric. |
| Base local | Room + SQLCipher | Transacciones y `Flow` para refrescar pantallas, y cifrado con llave del Keystore. Llaves por `SyncId`, sin llaves foráneas. |
| Red | OkHttp (HTTP/2), solo `https://`, `usesCleartextTraffic=false` | Conexión reusada para la revisión de 4 s, buen control de tiempos y `MockWebServer` para reproducir los intercambios grabados. |
| JSON | kotlinx.serialization | Sin reflexión. `ignoreUnknownKeys` acepta los cambios aditivos. Los decimales se leen como `BigDecimal`. |
| Segundo plano | WorkManager, trabajos normales (no expeditos) | Subida de respaldo y descarga periódica, agrupadas por Doze, sin servicio en primer plano ni notificación. |
| Ciclo de vida | `ProcessLifecycleOwner` + corrutinas y `Flow` | Revisión y latido solo con la app al frente; subida inmediata en ON_STOP. Se prueba con `kotlinx-coroutines-test`. |
| Reloj | Hora del servidor anclada con `SystemClock.elapsedRealtime()` | Un cambio de hora del aparato sin conexión no mueve las marcas. Después de reiniciar usa el reloj normal con un aviso, hasta la siguiente sincronización. |
| QR | CameraX + ML Kit (modelo incluido en la app) | Funciona sin descargar el modelo de Google Play. Como respaldo, el código se puede pegar. |
| Criptografía | Android Keystore + `javax.crypto` (PBKDF2-SHA256, AES-GCM, ECDH P-256; HKDF con HmacSHA256) | Sin depender de `EncryptedSharedPreferences`. Se comprueba con los vectores de 8.15. |
| Receta en PDF para compartir | `android.graphics.pdf.PdfDocument` dibujado a partir del modelo `PrescriptionSheet` y los vectores `prescription-sheet` y `prescription-html-fields` (8.11), en media carta horizontal, con el membrete de `ClinicAssets` | `PrintDocumentAdapter` no genera un PDF sin el diálogo de impresión del sistema. Se compara a ojo con la de la PC en la tablet real al empezar K4. |
| Imprimir la receta | WebView con la plantilla portada de `PrescriptionHtmlBuilder` y la impresión del sistema | Mismo HTML que la PC; aquí sí se quiere el diálogo de impresión. |
| Ticket en PDF o imagen | `PdfDocument` con `TicketText` | El ticket son 42 columnas de texto. |
| Compartir | `FileProvider` + hoja de compartir de Android | WhatsApp, correo o guardar. |
| Bloqueo | androidx.biometric | Huella o PIN en el celular. Bloqueo de pantalla obligatorio para vincular. |
| Formato es-MX y fechas | `BigDecimal` con `HALF_EVEN`, `Locale("es","MX")`, `Collator` es-MX, `ZoneId.of("America/Mexico_City")` | Igual que .NET: redondeo al par y orden con ICU. Si las reglas de zona del aparato todavía marcan horario de verano después de 2022 (Android 8 y 9 sin tzdata nueva), usa UTC−6 fijo. |

### 7.3 Motor de sincronización v2 en Kotlin

- **Transporte:**
  - `POST {url}/v2/pipeline` con `Authorization: Bearer`, cuerpo `{"requests":[{"type":"execute","stmt":{"sql","args"}}…,{"type":"close"}]}`, sin baton.
  - User-Agent `TPVC-Android/x.y.z`.
  - Las instrucciones se confirman una por una (no hay transacción, salvo lo que decida V14), así que todo debe ser idempotente.
  - Un error llega como `{"type":"error","error":{message,code}}`. `BLOCKED` pausa una hora; 401 sigue §4.7.
  - El SQL de cada instrucción es el literal de `contracts/hrana/` (8.13, 8.24).
- **Valores** (`contracts/golden/values.json`):
  - entero, booleano y enum van como `{"type":"integer","value":"<texto>"}`; un número JSON provoca HTTP 400;
  - double como `float`;
  - decimal como texto plano con su escala (`toPlainString()`);
  - GUID en minúsculas con guiones;
  - fecha como `yyyy-MM-dd HH:mm:ss.fffffff`, siempre de 27 caracteres; `utc`, `clinicLocal` o `dateOnly` según el `timeKind` de la columna, sin conversión al escribir;
  - un nulo en una columna obligatoria va como `""`, 0 o `0001-01-01 00:00:00.0000000`.
- **Primer arranque**, solo con la app al frente:
  1. `SELECT Key, Value FROM _tpvc_meta`, sin DDL. `clinicId` debe coincidir con el del QR, `minClientProtocol` debe ser ≤ el suyo, y `fullUploadAtUtc` y `generationReady` deben existir.
  2. El manifiesto debe contener todo lo de su manifiesto incluido. Si no, solo lectura con "Actualice la app".
  3. Lee su fila de `DeviceRegistry`.
  4. Toma la marca de agua antes de leer.
  5. Baja todo, una tabla por petición en orden de dependencias, luego las lápidas, `ClinicProfiles` y `ClinicAssets`. El cursor de cada tabla se guarda después de cada página.
  6. Recalcula las columnas derivadas.

  **No escribe nada antes de terminar esta primera descarga.**
- **Subida:**
  - Cola de salida con una secuencia local que solo sube. No depende del reloj, incluye los borrados en cascada, aparta lo que no puede subir, y cada fila guarda el protocolo con que se escribió. Si `minClientProtocol` sube por encima del suyo, no sube esa cola: la conserva y pide actualizar; la versión nueva la sube o la exporta.
  - Se sube tabla por tabla en el orden de `schema.json`.
  - Antes de subir: vuelve a marcar con la hora del servidor lo pendiente que esté más de 2 min en el futuro, y si la versión nueva de la app conoce columnas que su base no tenía, baja completas esas tablas.
  - Upsert igual a `UpsertSql` (`TursoSyncService.cs:328-345`), pero **solo con las columnas que conoce** (`ON CONFLICT DO UPDATE SET` columna por columna, nunca `INSERT OR REPLACE`), para no borrar las que agregue una PC más nueva. Cada escritura lleva la cerca de generación (8.9).
  - `OriginDeviceId` = su `DeviceId` (32 hexadecimales).
  - Cada lápida es un upsert seguido de `DELETE … UpdatedAtUtc <= borrado`. Hasta 80 instrucciones por petición.
  - Al terminar, escribe `RegisteredDevices.LastPushUtc`.
- **Bajada:**
  - La marca de agua es la hora del servidor menos 5 s, tomada antes de leer y guardada tal cual, sin interpretarla.
  - Llave (`SyncedAtUtc`, `SyncId`), con la forma de F1. Incremental: todas las tablas en una petición; página de 100 en Patients y PatientConsultations y de 500 en las demás, que se reduce a la mitad ante un error de tamaño o de tiempo.
  - Omite lo propio salvo en una descarga completa.
  - Lee su fila de `DeviceRegistry` en cada sincronización: revocación y credencial renovada (§4.7).
  - Cada página se aplica en una transacción y deja guardado el cursor de su tabla.
  - Una referencia opcional que todavía no existe aparta la fila (8.4).
  - Si la generación cambió, sigue la regla de 8.9: deja de subir, guarda lo pendiente, espera `generationReady`, vuelve a bajar y ofrece "Volver a aplicar N cambios".
- **LWW:**
  - Una fila remota no se aplica si la local tiene `UpdatedAtUtc` mayor o igual.
  - La nube actualiza solo si la versión que llega es estrictamente más nueva.
  - Una lápida remota se aplica salvo que la fila local sea estrictamente más nueva.
  - Marca local = `max(ahora anclado + desfase, guardada + 1 ms)`.
  - Si un upsert afecta 0 filas, vuelve a leer la fila del servidor y la aplica aunque empate; si no existe, aplica su lápida.
  - Pacientes: combinación por campo y hojas por renglón (8.22), con la forma canónica. El resultado distinto se encola para subir. El resumen de citas es derivado.
- **Existencias:**
  - Solo escribe `InventoryMovements`, con `Delta`, un movimiento por lote y devoluciones deterministas.
  - Recalcula las columnas derivadas, con los conteos como ancla, después de cada escritura y de cada bajada.
  - Nunca escribe Products, ProductBatches, saldos iniciales, conteos ni columnas derivadas.
- **Folios:** con su código de `DeviceRegistry` y el último número guardado fuera de la base. La fecha del folio y "hoy" salen del reloj anclado, en la zona del consultorio. Sin índice único de folio y sin renombrar nunca.
- **Hojas del paciente:** formato de 8.18, con `RowId`. Conserva los campos que no conoce. No escribe `ChargedLineId`.
- **Freno:** como máximo 2 descargas completas al día y un tope diario estimado de lecturas y escrituras, igual que la PC (8.2).
- **Nunca:**
  - corre DDL;
  - siembra;
  - escribe catálogo, configuración, `ClinicProfiles`, `ClinicAssets`, `_tpvc_meta` o `DeviceRegistry`;
  - reutiliza un código;
  - sube a otra generación;
  - borra cambios sin subir: antes los sube o los exporta (§4.7).
- **Consulta entre equipos (solo la tablet):**
  - Mismas instrucciones y orden que la PC (`ConsultationHandoffService`): sincronizar antes; marca local antes del `INSERT`; recuperar con cerca; guardar la ficha antes del `UPDATE` condicionado; borrar la copia por ficha solo después de guardar.
  - Al aceptar, exige las marcas por campo que trae la copia (8.22).
  - Escribe `HandoffProtocol = 1` en su latido.

### 7.4 Roles en la app

| | Tablet | Celular |
|---|---|---|
| Pantallas | Inicio (citas de hoy, recientes, búsqueda), ficha, alta y edición, agenda, consulta completa (receta, insumos, servicios, cuenta, terminar y cobrar), consulta entre equipos, punto de venta, inventario de solo lectura, resumen y alertas | Resumen, corte de caja, alertas, compra sugerida, expedientes de solo lectura |
| Compartir | Receta y ticket en PDF | Receta y ticket en PDF |
| Escribe | Sí (§4.3) | No (pregunta 3) |
| Revisión y latido | Al frente | No |

### 7.5 Orden de trabajo y tamaño

Entre paréntesis, los pasos de la PC que necesita cada uno.

**Celular (primera versión):**
1. **K0.** Esqueleto con los cuatro módulos, CI, textos en español y tema.
2. **K1.** Contrato y reglas del celular, con sus vectores: reportes, receta y ticket (8.3, 8.11).
3. **K2.** Cliente Hrana y motor de lectura: primer arranque, descarga completa, cursor por página, recálculo, generación y freno (8.6, 8.7, 8.9, 8.13). Datos de prueba con un token pegado en las compilaciones de desarrollo.
4. **K3.** Vinculación y seguridad: QR, código, reclamo, confirmación, credencial, renovación, Keystore, SQLCipher y reglas del 401 (8.14–8.16).
5. **K4.** Celular: resumen, corte, alertas, compra sugerida, expedientes de solo lectura y compartir la receta y el ticket (8.11, 8.12). **Primera versión del celular.**

**Tablet:**
6. **K5.** Motor de escritura: cola de salida, LWW, desempate, filas apartadas, cerca de generación, combinación de pacientes, movimientos, folios y WorkManager. Pasa los escenarios contra el servidor simulado (8.17–8.23, 8.25).
7. **K6.** Inicio, ficha, agenda con aviso de empalme, alta y edición con aviso de duplicado (8.26).
8. **K7.** Consulta: las secciones en el orden de la PC con la cuenta a la derecha, hojas con `SyncId` y `RowId`, receta con plantillas y calculadora pediátrica, terminar y cobrar (nota, venta, movimientos por lote, cita "Atendida") y compartir receta y ticket. La pantalla se queda encendida durante la consulta.
9. **K8.** Consulta entre equipos: aviso, "Continuar aquí", "Enviar a…" y solo lectura mientras está ofrecida (8.10, 8.24, 8.25). **Con K5–K8, primera versión de la tablet.**
10. **K9.** Punto de venta en la tablet, con `RankProducts` y el prefijo `3*`.
11. **K10.** Accesibilidad, tema oscuro y revisión de textos. Más adelante, impresión ESC/POS por Bluetooth con `TicketText`.

**Tamaño estimado** (gruesa, para un desarrollador de tiempo completo):

| Parte | Tiempo |
|---|---|
| Reglas y vectores (K1 y las de la tablet) | 4–6 semanas |
| Motor de lectura y escritura (K2, K5) | 6–9 semanas |
| Vinculación y seguridad (K3) | 3–4 semanas |
| Pantallas (K4, K6–K9) | 12–18 semanas |
| Pulido y pruebas en equipos reales | 3–4 semanas |
| **Total Kotlin** | **7–10 meses** |
| Pasos de la PC 8.2–8.26 (25 versiones; 8.15, 8.20, 8.21 y 8.22 de riesgo alto o medio-alto) | ~3–4 meses más |

El celular (K0–K4) es la parte más corta y puede empezar en cuanto exista 8.16. Además hay un costo permanente: todo cambio a `GetTableMaps` o a una regla con vectores pide otra versión de la app.

### 7.6 Cómo se prueba sin equipos

- **Vectores:** JUnit lee los mismos archivos de `contracts/golden/` que C#.
- **Intercambios grabados:** `MockWebServer` reproduce `contracts/hrana/*.jsonl`. Las peticiones de Kotlin se comparan por significado, no byte por byte, salvo el SQL, que debe ser el literal.
- **Escenarios de varios equipos:** la CI de Kotlin levanta el servidor simulado de 8.13 y corre `contracts/scenarios/` con el motor de la PC (C#) y el de Kotlin sobre el mismo servidor. Por ejemplo: la PC y la tablet venden la última pieza; dos equipos cancelan el mismo ticket; alergias en la tablet y teléfono en la PC; un renglón en cada equipo en la misma hoja; la PC reemplaza la nube mientras la tablet sube. El mismo job corre la revisión de planes sobre las consultas de Kotlin.
- **Base local:** Room en la JVM con el controlador SQLite incluido (`androidx.sqlite`). SQLCipher solo se comprueba en el equipo real.
- **Pantallas:** Robolectric para los estados clave de Compose (sin conexión, "N cambios sin sincronizar", "Sincronización pausada", confirmaciones, solo lectura).
- **En equipos reales**, solo lo que no se puede simular: leer el QR con la cámara, el PDF de la receta junto al de la PC, la hoja de compartir, Keystore y StrongBox, los tiempos del segundo plano con Doze y la suspensión de Samsung, y un día completo en el consultorio. Va en la sección D de `PENDIENTES_ANDROID.md`.

---

## 8. Cambios en otros documentos (paso 8.1)

El commit `0d9e12f` (del mismo día) reescribió estos documentos para el plan de la red local. Hay que revertir lo que describe ese plan y **conservar las correcciones que siguen valiendo**:
- los límites vigentes del plan gratuito;
- la descripción exacta de cómo hoy la restauración deja la nube distinta;
- el atraso del reloj que impide subir;
- que `ExecuteBatchAsync` no es transaccional;
- que la revisión no lee la hora del servidor;
- la decisión de Kotlin;
- los datos de código corregidos de A1–A4.

En todos: los números de paso del borrador anterior no coinciden con los de este plan. Se toman de §6.

**`docs/PENDIENTES_ANDROID.md`**
- **Introducción.** Reemplazar el párrafo "Decidido el 2026-10-09…" por:
  - Cada equipo tiene su SQLite y se sincroniza por Turso (protocolo v2).
  - La app es Kotlin nativo y cumple el motor v2 y el contrato de `ConsultationHandoffs`.
  - El plan de la red local se descartó el mismo día.
  - Enlace a este plan.
  - "Orden sugerido": pasos 8.2–8.16; el celular (K0–K4); pasos 8.17–8.26; la tablet (K5–K10).
- **Sección A**, párrafo de entrada. Revertir "la tablet y el celular no tienen base de negocio… resueltos por diseño". Queda: "siguen siendo bloqueantes; la tablet escribirá en su base y sincronizará por Turso; el celular solo baja".
- **Columna "Cómo queda":**
  - A1: libro de movimientos, lotes cerrados y conteos como ancla, 8.19–8.20. Conservar la corrección "los lotes no se pueden reconstruir con los movimientos de hoy".
  - A2: 8.8. Conservar lo de los pacientes demo.
  - A3: código de equipo `EXP-2026-T1-001`, índice normal sin `-R`, 8.17, pregunta 1. Conservar la corrección de CNS.
  - A4: 8.18, con `RowId` y **sin relleno de los JSON existentes**. Corregir el texto actual, que dice que "se completan al arrancar". Conservar el error latente entre dos PC.
  - A5: 8.24.
  - A6: sigue vigente, con el reporte de 8.7; la primera subida por `Seq` (8.5) reescribe todo una vez.
- **B1:** se queda Kotlin. Quitar "la app no habla con Turso ni reimplementa el protocolo v2" y "OkHttp con el certificado fijado". Queda: "implementa el motor v2 y la consulta entre equipos; la conformidad se comprueba con `contracts/` (8.3, 8.11, 8.13, 8.25, 8.26)". Pila en el plan §7.
- **B2:** sigue retirado. Lo reemplazan los generadores puros (8.11) y el contrato exportado (8.3), ya no "la API de la red".
- **B3:** quitar que los ViewModels "corren en la PC" sin pantalla y la opción `RemoteConsultationHost`/`ConsultationEngine`. Son solo la referencia de comportamiento.
- **B4:** el rol sale de `DeviceRegistry`, que solo escribe la PC. Turso hace cumplir el solo lectura del celular y, con V1, los límites de la tablet; lo demás lo aplica la app (8.10, 8.14). Matriz en el plan §4.6.
- **C1:** QR con código de 10 dígitos, base de vinculación aparte, código de confirmación, credencial sellada y token por equipo (8.15). Quitar direcciones, puerto y certificado. En la app: K3.
- **C2:** la receta para compartir se dibuja con `PdfDocument` desde el modelo `PrescriptionSheet` (8.11) con el membrete de `ClinicAssets` (8.12); para imprimir, la plantilla portada de `PrescriptionHtmlBuilder` en WebView. Ticket con `TicketText`. ESC/POS en K10.
- **C3a:** volver al contrato de `ConsultationHandoffs` en Turso. El texto anterior a `0d9e12f` vale, sin la frase de reutilizar con MAUI, más compatibilidad por `HandoffProtocol` (8.10), cadencia (8.24) y marcas por campo en la copia (8.22). En la app: K8.
- **C3:** "la tablet trabaja sobre su base local (Room + SQLCipher) y sincroniza al volver la red; indicador «N cambios sin sincronizar»". Cobrar sin conexión según la pregunta 2.
- **C4:** se queda Compose. Quitar "llegarán ya calculados por la PC" y "el cálculo será un comando a la PC": Kotlin porta las reglas con los vectores de 8.26.
- **C6:** el PDF se arma en el propio equipo, sin la PC (8.11, 8.12). Quitar "Sin la PC puede volver a compartir…". En la app: K4 (celular) y K7 (tablet).
- **D, lista "Fase 8".** Reemplazar firewall, IP, aislamiento de clientes y "la PC no se duerme" por:
  - aviso de hora (8.7);
  - unirse sin duplicados (8.8);
  - restaurar con la nube activa: la lista de lo que se perdería y "Volver a aplicar" (8.9);
  - vincular con QR, el código de 10 dígitos y el mismo código de confirmación en los dos equipos (8.15);
  - "Quitar" con cambios pendientes y "Perdí este equipo" (8.16);
  - las existencias no cambian al convertirlas y aparece "Saldo inicial" (8.20);
  - lo de la tablet llega a la PC en menos de 30 s (8.24);
  - la última pieza vendida en dos equipos sin internet queda en −1 con aviso;
  - receta en PDF por WhatsApp desde el celular y la tablet, igual a la de la PC;
  - TPVC excluido de la suspensión y de la optimización de batería en la tablet Samsung;
  - un día completo con la tablet: notas, tickets, folios y existencias cuadran en los tres equipos.

**`docs/TURSO_SYNC_ARCHITECTURE.md`**
- **§1:** quitar los puntos "solo respaldo", "solo la PC habla con Turso" y `SharedPcs`. Queda: los tres equipos sincronizan por Turso, cada uno con su base, y la PC es dueña del esquema remoto.
- **§2:** conservar los límites vigentes (500M, 10M, 5 GB, 100 bases, 3 GB, 1 día). Agregar el costo de `ALTER` y `CREATE INDEX` y el comportamiento de `BLOCKED`. Reemplazar la tabla de uso por la de §3.2 de este plan, con F1, F2 y el freno de consumo. La nota sobre las constantes viejas apunta a 8.2.
- **§3:** cambiar el diagrama de la red local por la estrella de §2.1. Matriz de `DevicePermissions` más los dos permisos nuevos (8.10). Quitar "la PC los aplicará en cada ruta", `PairedDevices` y las referencias a la "pregunta 4 del plan" (ahora pregunta 3, solo para el celular; el corte de caja de la tablet queda oculto). Agregar `DeviceRegistry` y qué hace cumplir Turso (§4.6).
- **§4:** quitar "deja de ser el contrato de la app Android". Es el contrato de los dos motores y se fija en `contracts/sync/schema.json` (8.3).
  - §4.2: quitar "modo Backup, sin latido". Agregar que solo la PC crea o migra el esquema, las tablas y columnas nuevas, las claves de `_tpvc_meta`, la base de vinculación y el `timeKind` de las fechas.
  - §4.3: conservar el párrafo de los hechos (filas omitidas, reloj atrasado). Cambiar el plan: filas apartadas, también las de referencia opcional (8.4); cursor `Seq` guardado en `DbMeta` como `LastPushedSeq` (8.5); descarga eficiente con cursor por página (8.6); hora del servidor (8.7). Quitar `LastBackedUpSeq`, "detiene el cursor", "N cambios sin respaldar" y "no se descarga".
  - §4.4: conservar "sin transacción". Folios: código de equipo, índice normal y sin `-R` en adelante (8.17), no "la PC asignará todos". Agregar combinación por campo (8.22), existencias como libro (8.19–8.20) y cerca de generación (8.9).
  - §4.5: el contrato vale PC ↔ PC y PC ↔ tablet. Quitar `LanHandoffs`, "solo `SharedPcs`" y "capacidad `handoff.v1` (paso 8.23)". Conservar la corrección de que la revisión no lee la hora del servidor. Agregar cadencia e índice parcial (8.24), `HandoffProtocol` (8.10) y marcas por campo en la copia (8.22).
- **§5:** "Hoy: paquete en claro (se retirará en el paso 8.17)" pasa a "se retira en 8.2". Reemplazar el QR por la red con el flujo de §4.4 de este plan (8.15).
- **§6:** el token de la PC no sale de la PC; cada equipo recibe el suyo sellado al vincularse. La base del consultorio y la de vinculación, solas en su grupo (la guía rápida agrega crear la segunda); nivel A o C; las pruebas de rotación en otro grupo o cuenta; el resultado de V1–V15.
- **§7:** reescribir "Respaldo y recuperación". Quitar el modo `Backup`, `SharedPcs`, `lan.db` y la base aparte de cifras. Conservar la descripción de cómo se comporta hoy la restauración. Agregar: generación con cerca y `generationReady`, "Reemplazar la nube" o "Desconectar", la lista de lo que se perdería y "Volver a aplicar", la nueva descarga de los demás equipos, "Recuperar desde la nube" = unirse (8.8–8.9), el último número de folio guardado fuera de la base (8.17), la restauración a un punto de 1 día y los 14 respaldos locales.

**`docs/PLAN_REDISENO_UI.md`**
- Principio 8: se queda (Kotlin no reutiliza los ViewModels: son su referencia).
- Tabla de fases:
  - fila E: "✅ Entre dos PC hecha (v1.17) · la tablet, con su app en Kotlin y el mismo contrato por Turso";
  - fila 8: "Datos listos para tres equipos con Turso · Plan".
- Fase E: quitar el párrafo "Plan de la Fase 8 … red del consultorio … `LanHandoffs`" y el "(entre dos PC)" de *Cómo llega*. *Cobro*: la venta de la tablet sube como movimientos del libro (8.19–8.20). *Pendiente*: "la tablet implementa el mismo contrato (K8)".
- Notas al final de la Fase 7: QR con código por Turso (8.15) y PDF armado en el equipo (8.11, 8.12).
- Sección Fase 8: título "Datos listos para tres equipos con Turso (plan)" y su párrafo de entrada según §1 de este plan. Tabla de pantallas de la PC:
  - "Equipos vinculados" (lista con cambios pendientes, *Quitar*, "Perdí este equipo", "Esta PC reemplaza a la principal");
  - "Vincular tablet o celular" (QR, código de 10 dígitos, código de confirmación);
  - "Unirse a una nube existente", "Recuperar desde la nube" y "Esta PC ya usaba esta nube";
  - la pregunta al restaurar ("Reemplazar la nube" / "Desconectar"), con la lista de lo que se perdería, y "Volver a aplicar";
  - "N cambios sin sincronizar";
  - el aviso de hora;
  - "Sincronización pausada: consumo anormal" y "Se agotó la cuota mensual de Turso";
  - la insignia "−1 · revisar inventario" y el lote negativo;
  - "Empalme" en la agenda;
  - el banner de duplicados y el aviso de folios repetidos.

  Quitar "Uso de la nube", "Probar conexión (red)", "Consulta en curso en la tablet" y el aviso al cerrar.

**`docs/ARQUITECTURA_TECNICA.md`**
- **§1, principio 5:** reemplazar "La PC también será el servidor del consultorio" por: "tres equipos con su propia base, sincronizados por Turso (protocolo v2); la PC es la única que crea o migra el esquema remoto y es la referencia del protocolo (plan Fase 8)".
- **§6:** quitar `Services/Lan/` y `contracts/lan-api/v1/`. Conservar `android/`, `contracts/golden/` (8.3, 8.11, 8.26) y `Services/Documents/` (8.11). Agregar `contracts/sync/` (8.3), `contracts/scenarios/` (8.25), `contracts/hrana/` (8.13, 8.25) y `docs/FAKE_HRANA_SPEC.md` (8.13). La línea de `PLAN_FASE8_VARIOS_EQUIPOS.md` dice "datos listos para tres equipos con Turso".
- **§7:** conservar el job de Linux (paso 8.2, no 8.3) y agregar el paso que copia `Microsoft.WindowsDesktop.App`, porque el proyecto de pruebas es `net10.0-windows`. Vectores en 8.3, 8.11 y 8.26. Cambiar "Ejemplos del contrato (lan-api)" y "Deriva de OpenAPI" por: deriva de `schema.json`, escenarios, intercambios grabados con el SQL literal, y los instrumentos de `FakeHranaServer` (reloj, lecturas cobradas, planes, tope de respuesta, `BLOCKED`, mapa de tokens), con el servidor HTTP de pruebas solo en 127.0.0.1.
- **§8:**
  - conservar `SyncChangeLog` (8.5), ahora con `SyncParkedRows` (8.4), `SyncApplyState`, `MarkForUpload`, triggers con hash y `UseSqlReturningClause(false)` (no `HasTrigger`);
  - `DbMeta`: `clinicId`, `generation`, `DbInstanceId`, `LastPushedSeq`, desfase, revisión del contrato por tabla, `StockLedgerVersion` y `CloudJoined`;
  - quitar `LanRequestLog`;
  - agregar `FieldStampsJson`, `RowId` en las hojas, `Delta`, `SaleItemId`, `LedgerVersion`, `ClosedAtUtc` y `Conteo`;
  - "Guardado de pacientes por campo": guardar solo lo que cambió (8.21) y combinación con marcas por campo y por renglón (8.22), en lugar de `PatientPatch` con comparar y poner;
  - "Servidor" pasa a "Varios equipos": LWW por fila con la hora del servidor, combinación por campo en pacientes, existencias como libro con conteos y folios con código de equipo.
- **§9:** quitar "puede correr sin pantalla en la PC (8.21)", la subsección "Sin pantalla en el servidor" y la mención en la tabla de puertos.
- **§10:** cambiar "Servidor del consultorio" por "Varios equipos con Turso (plan Fase 8)": resumen de `SyncChangeLog`, `SyncParkedRows`, `StockLedger`, `PatientMerge`, `FolioFactory`, `SyncScheduler`, `DeviceRegistry` y la vinculación, con enlace a este plan.

**`README.md`**
- Línea de Turso: "Protocolo v2: la PC, la tablet y el celular sincronizan por Turso; esquema remoto y garantías". Quitar "Turso queda solo como respaldo".
- Línea de la Fase 8: título "Fase 8: datos listos para tres equipos con Turso". Descripción: "plan para que los tres equipos (app Android en Kotlin) escriban sin perder datos, vinculación por QR y pasos en la PC". Conservar el ícono de la línea.

**También:**
- `CHANGELOG.md`, en `[Sin publicar]`: cambiar la entrada "varios equipos en la red local" por este plan.
- `docs/MANUAL_DE_USUARIO.md` tendrá el capítulo "Tablet y celular" en 8.15 (vincular, equipos vinculados, perdí este equipo, restaurar con la nube).

---

## 9. Preguntas abiertas para el médico

Solo las que bloquean un paso.

1. **Folios hechos en la tablet** (paso 8.17). ¿Acepta que lleven la letra del equipo, como `EXP-2026-T1-001` o `TKT-20261009-T1-0001`? Los de la PC no cambian. Si el expediente tiene que ser una sola serie consecutiva, se construye 8.B: el número se asigna con internet y la letra queda solo para lo que se capture sin conexión.
2. **Cobrar en la tablet sin internet** (app K7):
   - Si sí: de vez en cuando una existencia puede quedar en negativo, con el aviso "revisar inventario", y se corrige con un conteo.
   - Si no: cobrar exige una sincronización al momento, y los negativos casi desaparecen.
3. **¿El celular solo consulta?** Es decir, sin agendar ni escribir notas.
   - Si sí: su token es de solo lectura y Turso lo hace cumplir.
   - Si quiere agendar desde el celular: necesita escribir citas y expedientes (agendar también escribe en el expediente), y se pierde esa garantía.
4. **Cuenta de Turso** (quien la administre; paso 8.14). ¿Se puede crear la base de vinculación y un token de la API de la plataforma limitado al grupo, con `db:mint-token` y `db:rotate-creds`, y dejar en ese grupo solo la base del consultorio y la de vinculación? Si no, se usa el nivel C: tokens creados a mano en el panel, sin vencimiento, que solo se cortan rotando el grupo.
