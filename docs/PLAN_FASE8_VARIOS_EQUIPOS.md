# Fase 8 · Datos listos para tres equipos con Turso

Estado al 2026-10-14 (TPVC v1.39.0). **Hechos: los pasos 8.2 (v1.20.0), 8.3 (v1.20.1), 8.4 (v1.20.2), 8.5 (v1.20.3), 8.6 (v1.20.4), 8.7 (v1.20.5), 8.8 (v1.20.6), 8.9 (v1.26.0), 8.10 (v1.27.0), 8.11 (v1.28.0), 8.12 (v1.29.0), 8.12b (v1.30.0), 8.13 (v1.31.0), 8.14 en la PC (v1.32.0), 8.15 en la PC (v1.33.0), 8.16 en la PC (v1.34.0), 8.17 (v1.35.0), 8.18 en la PC (v1.36.0), 8.19 en la PC (v1.37.0), 8.21 en la PC (v1.38.0) y 8.22 en la PC (v1.39.0).** Lo demás todavía es plan. Aquí se fijan la arquitectura, el contrato que tendrán que cumplir la PC y la app en Kotlin, y el orden de los pasos. El código viene después, un paso por versión. Cada versión deja la PC funcionando sola y trae pruebas que corren en Linux con el servidor Turso simulado (`FakeHranaServer`, en `tests/TPVC.Tests/TursoSyncEndToEndTests.cs`), sin equipos Android ni una base real de Turso. Para que eso funcione en la CI hace falta el paso 8.2: hoy el proyecto de pruebas es `net10.0-windows` y no arranca en Linux sin el runtime de escritorio.

Esta versión **reemplaza el borrador del mismo día** que se publicó en este archivo (commit `0d9e12f`). Ese borrador convertía la PC en servidor de la red local y en la única que escribe, y dejaba Turso solo como respaldo. El médico lo descartó el 2026-10-09 después de ver que el uso real de Turso cabe de sobra en el plan gratuito (§3). Esta versión ya trae las correcciones de dos revisiones técnicas.

El mismo día el médico respondió las cuatro preguntas abiertas (§9), y esta versión ya las incorpora: un solo consecutivo de folios, con la letra del equipo solo sin conexión y una regularización diaria en la PC; la tablet cobra sin internet; el celular ve expedientes, agenda citas, revisa inventario y registra entradas de mercancía; y la cuenta de Turso no tiene token de la API, así que se usa el nivel C (tokens creados a mano), sin base de vinculación. El QR iba a quedar limitado a la red local; el 2026-10-11 el médico lo cambió: cada equipo recibe la URL y el token como la PC, pegados o escaneados de un QR de la PC, y la PC aprueba cada equipo nuevo (paso 8.14).

**En una frase:** cada equipo (PC, tablet y celular) trabaja sobre su propia base y se sincroniza por Turso con el protocolo v2, como ya lo hace la PC. La Fase 8 cierra los huecos que hoy perderían o duplicarían datos cuando escriban dos equipos, y deja listo lo que la app en Kotlin necesita antes de su primera pantalla.

---

## 1. Decisiones del médico y supuestos

| # | Decisión (2026-10-09) | Qué implica |
|---|---|---|
| 1 | Tres equipos: la PC con Windows, una tablet Android y un celular Android. El 99% del tiempo están en la misma red, pero **la red local no se usa como canal de datos**. | No hay servidor permanente en la PC y los datos nunca pasan de un equipo a otro: todo pasa por Turso. La única conexión local es la vinculación por QR, de 10 min y un solo uso (decisión 9). |
| 2 | **Se conserva la arquitectura original.** Cada equipo tiene su SQLite local y se sincroniza por Turso (plan gratuito) con el protocolo v2. La consulta pasa entre equipos por `ConsultationHandoffs` en Turso. | Varios equipos escriben, así que los bloqueantes A1–A6 y los riesgos H1–H11 se resuelven en los datos (§5). |
| 3 | La app de la tablet y del celular es **Kotlin nativo**, no .NET MAUI. | En Android no se reutiliza código C#. El código de la PC es la referencia. Kotlin implementa por su cuenta el motor del protocolo v2 y el contrato de la consulta entre equipos, y los dos lados se comparan con archivos de conformidad (`contracts/`). |
| 4 | La Fase 8 tiene dos partes: datos seguros con tres equipos escribiendo por Turso, y todo lo que va antes de las pantallas en Kotlin. Por ahora solo se escriben el plan y la documentación. | El código va después, una versión por paso (§6). |
| 5 | La receta impresa no lleva línea de firma (ya decidido). | La receta de Android sale del mismo modelo que la de la PC. |
| 6 | **Folios: un solo consecutivo por serie.** Sin conexión, el equipo crea el folio con su letra y lo sube así. La PC, que es una laptop, lo regulariza en segundo plano una vez al día, cuando no hay consulta ni venta en curso: le da el siguiente número disponible y deja asociado el folio con letra, para que la búsqueda lo encuentre. | Contador `FolioCounters` en Turso y columna `OriginalFolio` (pasos 8.16 y 8.17). Reemplaza la letra fija en todo folio hecho fuera de la PC, y el opcional 8.B pasa al núcleo. |
| 7 | **La tablet cobra sin internet.** La meta: no notar diferencia entre usar la PC o la app. | Venta, movimientos por lote, nota y cita "Atendida" sin conexión, con el folio con letra. Una existencia puede quedar en negativo con "revisar inventario" y se corrige con un conteo (8.20, K7). |
| 8 | **El celular ve expedientes, agenda citas, revisa inventario y registra entradas de mercancía.** | Escribe citas, lotes y movimientos de entrada; nada clínico, ni recetas ni ventas. Los límites los aplica la app (§4.6). Su primera versión solo baja (K4); lo que escribe llega en K4b. |
| 9 | **La cuenta de Turso no tiene token de la API de la plataforma.** Vinculación (decisión del 2026-10-11, reemplaza el QR por la red local): cada equipo recibe la URL y el token como la PC, pegados o escaneados de un QR que la PC muestra 2 min; la PC aprueba cada equipo nuevo. | Nivel C: tokens creados a mano en el panel, sin vencimiento, que solo se cortan anulándolos todos (8.15). Sin base de vinculación ni servidor en la red local (§4.4, descartada; paso 8.14). |

**Pedidos del médico que cubre este plan:**
- Vincular la tablet leyendo un código QR, en la red del consultorio (paso 8.14). El 2026-10-11 el médico lo simplificó: la URL y el token, pegados o leídos de un QR de la PC, y la PC aprueba cada equipo (§4.3).
- No notar diferencia entre la PC y la app, también sin internet (8.16, 8.17, K7).
- Agendar y registrar entradas de mercancía desde el celular (K4b).
- Compartir la receta y el ticket en PDF desde donde se vean (pasos 8.11 y 8.12; app K4 en el celular y K7 en la tablet).
- Continuar una consulta en otro equipo. Entre dos PC ya funciona desde v1.17; la tablet implementará el mismo contrato (app K8).

**Reglas de la consulta que se conservan en la tablet:** la consulta nunca manda al POS, "Se surte aquí" viene marcado por defecto y no existe "terminar sin cobrar".

**Supuestos**
- La PC funciona completa por sí sola, siempre. Con la nube apagada, TPVC se comporta como en v1.19.
- Un solo médico, como máximo tres equipos activos. Si hay otra PC con Windows conectada a la misma base, el paso 8.20 no avanza hasta actualizarla o marcarla como retirada.
- Sin internet, cada equipo sigue trabajando sobre su base y sus cambios esperan. Las revisiones que dependen de los demás (existencias, citas) se hacen con los datos locales, y los folios llevan la letra del equipo hasta la regularización (H11, §5).
- La PC del consultorio es una laptop: puede estar cerrada o suspendida. Nada depende de que esté prendida; si un día no se abre, la regularización de folios corre la siguiente vez que esté abierta y libre.
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
 │ motor v2 (C#)        │    │ motor v2 (Kotlin)    │    │ motor v2 (Kotlin)    │
 └──────────┬───────────┘    └──────────┬───────────┘    └──────────┬───────────┘
            │ sube y baja               │ sube y baja               │ sube y baja (§2.2)
            ▼                           ▼                           ▼
 ┌──────────────────────────────────────────────────────────────────────────────┐
 │ Turso · base del consultorio, sola en su grupo (libSQL, Hrana v2 por HTTPS): │
 │ 14 tablas sincronizadas, SyncTombstones, ClinicProfiles, ClinicAssets,       │
 │ LabBlobs y LabBlobChunks (archivos de laboratorio),                          │
 │ RegisteredDevices, DeviceRegistry, ConsultationHandoffs, FolioCounters,      │
 │ _tpvc_meta                                                                   │
 └──────────────────────────────────────────────────────────────────────────────┘
  Al vincular, el equipo recibe la URL y el token pegados o escaneados de un QR de la PC, y la PC lo aprueba (paso 8.14; la entrega por la red local de §4.4 se descartó el 2026-10-11).
```

- El **protocolo v2** sigue siendo el de hoy (`Services/TursoSyncService.cs`, `Services/TursoClient.cs`, `docs/TURSO_SYNC_ARCHITECTURE.md` §4):
  - una fila remota por registro, con llave `SyncId`;
  - LWW por `UpdatedAtUtc`;
  - marca de agua del servidor (`SyncedAtUtc`) para bajar;
  - `OriginDeviceId` para omitir lo propio;
  - lápidas para los borrados.

  La Fase 8 le agrega columnas y reglas, siempre de forma aditiva. Cuando cambia el significado de las existencias (paso 8.20), la nube se marca `protocol = 3` para que un equipo que no conoce las reglas nuevas se detenga.
- **Turso no ejecuta reglas del negocio.** Solo guarda filas y decide de uno en uno los upserts condicionados (el contador de folios es uno de ellos). Las reglas (libro de existencias, combinación de pacientes, folios) viven en cada motor y se comprueban con vectores compartidos.

### 2.2 Papel de cada equipo

| | PC (`WindowsDesktop`) | Tablet (`AndroidTablet`) | Celular (`AndroidMobileAdmin`) |
|---|---|---|---|
| Base local | `consultorio.db` (EF Core) | Room + SQLCipher, con llaves por `SyncId` | Igual que la tablet |
| Escribe en Turso | Todo | Pacientes, notas, citas, ventas, movimientos, lápidas, su latido, `FolioCounters` y `ConsultationHandoffs` | Citas, lotes y movimientos de entrada (con sus lápidas) y su latido. Nada clínico, ni recetas ni ventas. Su token es completo: los límites los aplica la app (§4.6) |
| Crea o migra el esquema remoto | **Sí, solo la PC** | Nunca | Nunca |
| Vincula y revoca equipos, escribe `DeviceRegistry` y `_tpvc_meta` | Sí | No | No |
| Edita catálogo, inventario, perfil y membrete | Sí | No (inventario solo lectura) | No; solo registra entradas de mercancía (lote y movimiento de entrada) |
| Consulta entre equipos | Envía y recibe | Envía y recibe | No (excluido por su rol, como hoy, `ConsultationHandoffService.cs:181`) |
| Latido en `RegisteredDevices` | Sí | Sí, solo con la app al frente | Sí, solo con la app al frente |
| Folios | Consecutivo del contador en Turso; sin conexión, con `P1` (otra PC, `P2`…). Solo la PC principal regulariza (8.17) | Consecutivo del contador; sin conexión, con `T1`… | No crea folios |
| Siembra datos | Solo en una base nueva (8.8) | Nunca | Nunca |

### 2.3 Esquema remoto: dueño y piezas nuevas

- **Solo una PC con Windows crea o migra el esquema remoto.** Hoy cualquier cliente corre DDL:
  - `EnsureRemoteSchemaAsync` empieza con `CREATE TABLE IF NOT EXISTS _tpvc_meta` (`TursoSyncService.cs:494-506`) y puede renombrar tablas a `legacy_v1_*`;
  - `EnsureTablesAsync` de la consulta entre equipos corre `CREATE` en cada proceso nuevo (`ConsultationHandoffService.cs:97-103`).

  Kotlin nunca ejecuta DDL, nunca corre la limpieza de `ConsultationHandoffs` y se niega a sincronizar si falta `_tpvc_meta.schema` ("Sincronice primero la PC").
- **Tablas nuevas:**

  | Tabla | Dónde y quién escribe | Para qué | Paso |
  |---|---|---|---|
  | `DeviceRegistry` (`DeviceId` PK, `DeviceCode` único, `Role`, `DisplayName`, `Model`, `PairedAtUtc`, `RevokedAtUtc`, `RevokedReason`) | Base del consultorio; solo la PC | Autoridad sobre el rol, el código de folios y la revocación de cada equipo. Sus filas nunca se borran. | 8.10, 8.14 |
  | `ClinicAssets` (escudo y encabezado del membrete) | Base del consultorio; solo la PC | La receta de Android lleva el mismo membrete. | 8.12 |
  | `FolioCounters` (`Series` PK, como `EXP-2026` o `TKT-20261009`; `LastNumber`) | Base del consultorio; la crea la PC y la incrementan la PC y la tablet con una sola instrucción | Un solo consecutivo por serie con varios equipos (§2.5). No es tabla sincronizada: no lleva `SyncId`, sus filas nunca se borran y "Reemplazar la nube" (8.9) no la toca. | 8.16 |

- **Columnas nuevas en tablas que no salen del modelo de EF.** Llevan `ALTER` explícito, revisado con `pragma_table_info` e incluido en la huella, porque el ciclo de columnas nuevas solo cubre las tablas de `GetTableMaps`. Todas aceptan nulo:
  - `RegisteredDevices`: `Platform`, `SyncProtocol`, `HandoffProtocol`, `ContractRevision`, `LastPushUtc`, `PendingCount`, `OldestPendingUtc`, `InstanceNonce` (8.10).
  - `ClinicProfiles`: `CrestImageHash`, `HeaderImageHash` (8.12).
- **Columnas nuevas en tablas sincronizadas** (salen del modelo y viajan por el ciclo normal): `Patients.OriginalFolio` y `Sales.OriginalFolio` (8.17); `InventoryMovements.Delta` y `SaleItemSyncId`, `Sales.LedgerVersion`, `ProductBatches.ClosedAtUtc` (8.19); `InventoryMovements.CountedQuantity` (8.20); `Patients.FieldStampsJson` (8.22); `Patients.MergedIntoSyncId` (8.23). Cada `ALTER` recorre su tabla en Turso una vez (§3.2).
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
  | `primaryDeviceId` | La PC principal: la única que regulariza folios (8.17) y crea saldos iniciales (8.20) | 8.10 |

- **`DbMeta` local** (dentro de `consultorio.db`): `clinicId`, `generation`, `DbInstanceId`, `LastPushedSeq`, desfase del reloj, revisión del contrato con la que se bajó cada tabla, `StockLedgerVersion`, `CloudJoined`, "reemplazo en curso" y la fecha de la última regularización de folios (8.17). El consecutivo de cada serie vive en `FolioCounters`; el último número de cada serie **con letra** (los folios sin conexión) vive **fuera** de la base (8.16), para que sobreviva a una restauración.

### 2.4 Consulta entre equipos

- Se conserva el contrato de `ConsultationHandoffs` (`docs/TURSO_SYNC_ARCHITECTURE.md` §4.5):
  - estados `enviada`, `aceptada` y `cancelada`;
  - `AcceptToken` para que gane un solo equipo;
  - recuperar con la instrucción con cerca;
  - la copia se borra solo después de guardarla.

  Kotlin lo implementa tal cual (app K8). La copia de trabajo es el `ConsultationDraft` v1. Desde 8.22 la copia lleva además las marcas por campo del paciente y la marca de la nota, y quien la recibe exige tenerlas (campo aditivo, `PayloadVersion` sigue en 1).
- Para recibir una consulta, un equipo cuenta como compatible si `RegisteredDevices.HandoffProtocol >= 1`. Si esa columna está vacía (PC v1.17–v1.19), sigue la regla de hoy: `AppVersion >= 1.17.0` (`ConsultationHandoffService.ToDevice`). Así la app Android puede numerar sus versiones desde 1.0.0 (paso 8.10).
- El celular no envía ni recibe consultas: aunque ahora escribe su latido, su rol lo deja fuera de la lista.

### 2.5 Decisiones técnicas de este plan

| Tema | Opciones | Decisión | Por qué |
|---|---|---|---|
| Aviso de que otro equipo subió algo | (a) una clave `changed` en `_tpvc_meta`; (b) una clave por equipo en `_tpvc_meta`; (c) bajar cada 60 s | Columna `RegisteredDevices.LastPushUtc`, escrita al final de cada subida con la hora del servidor y leída dentro de la revisión de consultas, más una descarga de seguridad cada 10 min | La tablet y el celular no escriben `_tpvc_meta`. La marca de uno mismo no dispara una descarga inútil. Cuesta unas 3–6 filas leídas por revisión. |
| Identidad del equipo al vincular | La PC genera el `DeviceId` y lo pone en el QR, o el equipo genera el suyo | El equipo genera su `DeviceId`; la PC solo asigna el código de folios | La descarga omite las filas propias (`TursoSyncService.cs:771`): dos instalaciones con el mismo `DeviceId` nunca verían las filas de la otra. Por eso el `DeviceId` nunca viaja en un QR ni en un respaldo, y la PC detecta un identificador clonado (8.10). |
| Dónde vive el código de folios | `RegisteredDevices.DeviceCode` o una tabla aparte | `DeviceRegistry.DeviceCode` con índice único | `DeviceRegistry` solo la escribe la PC, así que un equipo no puede cambiarse el código. |
| Numeración de folios | (a) la letra del equipo en todo folio hecho fuera de la PC; (b) consecutivo de un contador en Turso, la letra solo sin conexión y una regularización diaria en la PC | (b), decisión del médico | Un solo consecutivo por serie, como hoy. El número se toma con una sola instrucción atómica (`INSERT … ON CONFLICT DO UPDATE SET LastNumber = LastNumber + 1 RETURNING LastNumber`, V16), y el folio con letra queda en `OriginalFolio` para buscarlo. |
| Quién regulariza los folios con letra | Cada equipo los suyos, o solo la PC principal | Solo la PC principal, como máximo una vez al día, sin consulta ni venta en curso | Un solo escritor para ese cambio: dos equipos nunca dan números distintos al mismo registro. Sube por el ciclo normal (LWW; en pacientes, la combinación por campo de 8.22). |
| Formato del código y del folio sin conexión | `EXP-2026-T001`, o códigos `T1`/`C1` | Código de letra y número (`T1`, `C1`, `P1`), y folio con guion: `EXP-2026-T1-001` | No se confunde cuando llegue `T10`, y los lectores actuales de folios de la PC lo ignoran (§5, A3). |
| Folios repetidos | (a) el `-R1` de hoy al recibir; (b) índice normal y aviso | (b) | El `-R` renombra solo en el equipo que recibe, sin marca y sin subirlo: el mismo paciente queda con dos folios. Con el contador y la letra solo se repiten filas viejas o números tecleados a mano. |
| Compatibilidad entre versiones | Lista de capacidades (`handoff.v1`) o números | Columnas enteras `SyncProtocol` y `HandoffProtocol`, más `Platform` y `ContractRevision` | Se comparan sin interpretar texto. |
| Cursor de subida | Registro de cambios con triggers (`Seq`), o regresar el cursor de reloj cuando la hora se atrasa | Registro de cambios (`SyncChangeLog`) | No depende del reloj y además registra los borrados en cascada. |
| Tablas en Kotlin | Traducir `SyncId` a un Id local, o usar `SyncId` como llave sin llaves foráneas | `SyncId` como llave, sin llaves foráneas | No hay traducción y nunca se pierde una fila que llega antes que su principal. Los borrados copian las reglas en cascada, restringir o vaciar del contrato (8.3). |
| Columnas de existencias derivadas | No subirlas, o subirlas solo como información | Viajan como información y nunca se aplican al bajar | El formato no cambia y la copia en Turso sigue siendo legible. |
| Cantidades que fija el usuario (baja de lote, conteo) | (a) un movimiento con la diferencia contra el libro; (b) un conteo que sirve de ancla | (b) | Con (a), una venta hecha sin conexión antes del conteo, que llega después, se descuenta dos veces. |
| Hojas del paciente (`Active*Json`) | (a) un solo campo, gana la hoja completa; (b) renglones con `RowId` que se combinan | (b) | Las hojas también se editan fuera de la consulta (`SuppliesSheetViewModel.cs:204-216`), así que la PC y la tablet pueden tocar la misma hoja. |
| Cargos libres en las hojas | Copiar el hash de .NET en Kotlin, o una clave estable | Ninguna de las dos: el renglón lleva la clase `libre` y el nombre, y cada equipo calcula su Id local con `FreeChargeId` | Ese Id solo vive en la cuenta del equipo que lo calcula; entre equipos viajan la clase y el nombre. |
| Rellenar `ChargedLineSyncId` en hojas existentes | Al arrancar, o no | No | Las filas recibidas de otra PC traen los Id del otro equipo, y un relleno a ciegas las mapearía mal. Se actualizan en su siguiente guardado. |
| Número de protocolo | Quedarse en 2 con una revisión, o subir a 3 | `protocol` sube a 3 en 8.20; `contract` cuenta las revisiones aditivas | En 8.20 cambia el significado de las existencias. La tablet empieza directamente en 3. |
| Credenciales | (a) el token completo de la PC compartido, en claro (hoy); (b) un token por equipo creado por la PC (nivel A); (c) tokens creados a mano: el de la PC y uno para tablet y celular (nivel C) | (c), porque la cuenta no tiene token de la API | Sin ese token la PC no puede crear tokens. El de la PC nunca sale de ella y el de los equipos solo viaja cifrado al vincular (§4.3). |
| Entrega de la credencial al vincular | (a) base de vinculación en Turso, con código de 10 dígitos y credencial sellada; (b) la PC la entrega por la red local, cifrada con un secreto del QR; (c) la URL y el token pegados o en un QR de la PC, con aprobación en la PC | (c), decisión del médico del 2026-10-11 (antes era (b)) | Turso no anula un solo token, así que otro token aparte no protege más; (c) evita el servidor temporal, el Firewall y un protocolo cifrado que habría que mantener igual en C# y en Kotlin (paso 8.14). |
| PDF para compartir | (a) WebView + `PrintDocumentAdapter`; (b) `PdfDocument` dibujado | (b), y WebView solo para *Imprimir* | `PrintDocumentAdapter` no da un PDF sin el diálogo de impresión: sus callbacks tienen constructores privados del paquete. |
| Siembra en un equipo que se une | `SyncId` fijos, o unirse sin sembrar | Las dos: "Unirse a una nube existente" es la solución; los `SyncId` fijos son un seguro para instalaciones nuevas | Las nubes que ya existen tienen `SyncId` aleatorios. |
| Orden de los pasos | (a) primero todo lo de varios equipos que escriben; (b) primero lo que necesita el celular | (b): etapas 0–2, luego el celular, luego etapas 3–4 | La primera versión del celular solo baja y no necesita 8.16–8.26; lo que escribe (K4b) llega después de 8.19–8.23. Sale meses antes y prueba en equipos reales lo más incierto: Hrana, la vinculación en la red local y el PDF. |

### 2.6 Alternativas descartadas

| Alternativa | Por qué no |
|---|---|
| **La PC como servidor en la red local** y única que escribe, con Turso solo de respaldo (borrador `0d9e12f`) | Descartada por el médico el 2026-10-09. Hoy la PC hace ~8.7k peticiones al día, más del 90% por la revisión de cada 4 s y el latido de cada 30 s. Con tres equipos haciendo lo mismo serían ~23k al día: ~1% del límite de escrituras y ~2.5% del de lecturas, muy dentro del plan gratuito. A cambio, el servidor local pedía Kestrel, certificado, firewall, que la PC nunca se durmiera, y una tablet que no cobra con la PC apagada. |
| .NET MAUI reutilizando el C# | El médico eligió Kotlin. |
| Android sin base local, leyendo y escribiendo directo en Turso | Sin internet no funcionaría, y cada pantalla sería una ida y vuelta por internet. |
| Cambiar el protocolo v2 por la sincronización propia de libSQL/Turso | Habría que rehacer el motor que ya funciona en la PC, y no resuelve A1–A4: son reglas del negocio, no del transporte. |
| El token completo de la PC compartido con todos los equipos (el paquete de hoy) | Viaja en claro y por el portapapeles (§4.1). Con el nivel C también hay un solo token para tablet y celular, pero es distinto del de la PC y solo viaja cifrado por la red local. |
| Base de vinculación en Turso, con código de 10 dígitos y credencial sellada (versión anterior de este plan) | Valía la pena con el nivel A (tokens de 15 min) y costaba una segunda base. El médico limitó el QR a la red local (§9). |
| La letra del equipo en todos los folios hechos fuera de la PC (versión anterior de este plan) | El médico quiere un solo consecutivo por serie (§9). La letra queda solo para lo que se crea sin conexión. |

---

## 3. Uso de Turso con tres equipos y cadencia

### 3.1 Límites vigentes del plan gratuito

Al mes, según <https://turso.tech/pricing> (consultado el 2026-10-09): 500M filas leídas, 10M filas escritas, 5 GB, 100 bases y 3 GB de sincronización de réplicas (TPVC no la usa). Restauración a un punto en el tiempo de hasta 1 día. Según <https://docs.turso.tech/help/usage-and-billing>:
- Turso cobra como lectura cada fila recorrida; una instrucción que no toca filas cuenta como 1.
- `ALTER TABLE` y `CREATE INDEX` recorren toda la tabla (una lectura por fila), y `ALTER` puede escribir cada fila.
- Al pasar el límite, toda instrucción falla con el código `BLOCKED`, también la consulta entre equipos.

La app todavía muestra los límites viejos (`TursoQuotaSummary`, `Models/TursoConfig.cs:138-139`; `TursoSyncViewModel.RefreshQuotaDisplay`, :175-180). Se corrigen en 8.2.

**Dos hallazgos que entran en el plan:**
- **F1 · La descarga completa lee muchísimo de más.** Medido con SQLite 3.53, parámetros enlazados y sin `ANALYZE`, como en una base nueva de Turso. Con la consulta de `TursoSyncService.cs:775`, el planificador usa "MULTI-INDEX OR" y ordena todo lo que queda de la tabla en cada página de 500. Una tabla de 20k filas cuesta ~275k lecturas en lugar de ~20k. Las descargas incrementales no se ven afectadas. Se corrige anteponiendo `"SyncedAtUtc" >= ? AND (…)` (paso 8.6). Confirmado en Turso real el 2026-10-10 (V15): la primera página de 500 lee 26,666 filas con la consulta de hoy y ~2,000 con la corregida.
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
| Peticiones al día | ~8,700 | ~23,000 | ~12,800 (PC ~7.9k, tablet ~4.9k, celular ~40) | El plan no limita peticiones |
| Filas leídas al mes | 0.4M–4.2M | hasta ~12M | 2M–4.5M: cada revisión lee 4–11 filas (con el índice parcial) y hay ~600 bajadas al día de ~25 filas | 0.4–0.9% de 500M |
| Filas escritas al mes | ~45k (el latido es 1,200 al día) | ~120k | ~50k: al día, ~975 latidos (960 de la PC y la tablet, cada 60 s, y ~15 del celular), ~400 filas de datos y ~300 `LastPushUtc` | ~1.5% de 10M: Turso cuenta también cada entrada de índice (V10); sin ellas sería ~0.5% |
| Peor caso: PC y tablet abiertas 24 h en Pacientes | — | — | ~45k peticiones al día; ~7M lecturas y ~120k escrituras al mes | 1.4% y 1.2% |
| Almacenamiento | Decenas de MB al año | Igual | Igual, más ~1 MB del membrete (`ClinicAssets`) | < 1% de 5 GB |
| Bases | 1 | 1 | 1, sola en su grupo (§4.2) | 1 de 100 |

**Costos de una sola vez:**
- Vincular un equipo o hacer una "Sincronización completa" lee todo una vez: ~20–30k filas después de 8.6. Hoy cuesta ~N²/1000 por tabla.
- La primera subida con el cursor nuevo (8.5) y "Reemplazar la nube" (8.9) escriben ~20k filas (0.2% del mes) en el peor caso. Reemplazar además lee la nube una vez para guardar lo que se perdería.
- La verificación de la subida completa (8.7) lee ~20k filas una vez.
- Los `ALTER` de 8.17, 8.19, 8.20, 8.22 y 8.23 cuestan ~20k lecturas y hasta ~20k escrituras cada uno; el índice parcial de 8.24 recorre `ConsultationHandoffs` una vez. Todo junto queda abajo del 1–2% de un mes.

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
| | Solo la PC principal, con la nube al día y sin consulta ni venta en curso | Regularización de folios con letra (8.17) | Como máximo una vez al día; si la laptop estuvo cerrada, la siguiente vez que esté abierta y libre |
| Tablet | Al abrir la app (`ProcessLifecycleOwner` ON_START) | Latido, sincronización y revisión, en ese momento | Una consulta enviada aparece en ~1 s |
| | Al frente | Revisión con la misma marca de cambios | 4 s en pacientes y consulta; 30 s en otras pantallas |
| | | Descarga de seguridad / sincronizar tras guardar | 10 min / 4 s |
| | | Sin red (`registerDefaultNetworkCallback`) | Se detiene y sincroniza cuando vuelve la red |
| | Al pasar a segundo plano (ON_STOP) con cambios sin subir | Sube en ese momento desde el proceso, que Android deja vivo unos segundos. De respaldo, un trabajo único `tpvc-push` en WorkManager: normal (no expedito), red conectada, reintento creciente desde 30 s | Una vez |
| | En segundo plano | Descarga incremental periódica (red conectada, batería no baja). Nunca la primera descarga completa. Sin revisión ni latido, así que la PC la muestra como "la verá al abrir Pacientes" | Cada 60 min |
| Celular | Al abrir y al frente | Descarga y latido, sin revisión de consultas / sincronizar tras guardar (desde K4b) | Al abrir y cada 120 s / 4 s |
| | Al pasar a segundo plano con cambios sin subir | Igual que la tablet (`tpvc-push`) | Una vez |
| | En segundo plano | Descarga incremental periódica | Cada 3 h, con cualquier red |

Lo que ve el médico:
- Lo capturado en la tablet aparece en la PC en ≤ 8 s si la PC está en Pacientes, o en ≤ 35 s en otra pantalla.
- Lo de la PC aparece en la tablet al frente en 4–30 s.
- El celular se pone al día al abrirlo, y lo que agenda o registra llega a la PC igual que lo de la tablet.

**Transporte en Android:** un solo cliente OkHttp para toda la app, con HTTP/2, conexiones vivas al menos 5 min, gzip y 30 s de espera (20 s en la consulta entre equipos). Es lo mismo que hace `SharedHttpClient` (`TursoClient.cs:44-51`). Sin reusar la conexión, cada revisión de 4 s pagaría un saludo TLS nuevo.

**Segundo plano en Android:**
- Un trabajo expedito de WorkManager corre como servicio en primer plano en Android 11 y anteriores (pide notificación y, en Android 14, el tipo `dataSync`). Por eso el respaldo es un trabajo normal.
- WorkManager detiene un trabajo a los 10 min. Por eso el cursor se guarda después de cada página (8.6) y la primera descarga solo corre con la app al frente.
- Las tablets Samsung mandan apps a "suspensión" y el trabajo periódico puede no correr nunca. Hay que excluir TPVC de la suspensión y de la optimización de batería (sección D de `PENDIENTES_ANDROID.md`).

**Batería:**
- Al frente, cada revisión es una petición de 1–2 KB por Wi-Fi sobre una conexión ya abierta. Es despreciable junto a la pantalla encendida.
- En segundo plano hay como máximo un trabajo por hora, agrupado por Doze, sin wakelocks ni servicio en primer plano.

---

## 4. Vinculación, credenciales, roles y equipo perdido

### 4.1 Cómo está hoy (v1.19.0)

- **Paquete de vinculación.** `GenerateAndroidPairingPayload` (`Services/TursoSettingsService.cs:131-148`) arma un JSON en claro con el **token completo de la PC**, sin vencimiento y sin `DeviceId`.
  - Se muestra en `Views/TursoSyncView.xaml:273`.
  - "Copiar código" lo pone en el portapapeles (`TursoSyncViewModel.cs:413-427`), así que puede quedar en el historial del portapapeles de Windows.
  - Nada lo lee.
- **Rol.** Lo elige cada equipo en un selector (`TursoSyncViewModel.cs:93-97`, 296) y se escribe en `RegisteredDevices`. `DevicePermissions` (`Models/TursoConfig.cs:28-58`) no se aplica en ningún lado: en producción solo se usa `GetRoleDescription`.
- **Token.** Se guarda con DPAPI (`ProtectedAuthToken`). Fuera de Windows queda en base64 con el prefijo `B64:`. Si DPAPI falla al descifrar, el token queda vacío en silencio (`TursoSettingsService.cs:183-208`).
- **URL.** `NormalizePipelineUrl` (`TursoClient.cs:102-150`) acepta `http://` y cualquier host con punto, incluidas IP de la red local.
- **Servidor simulado.** `FakeHranaServer` ignora el encabezado `Authorization`.

### 4.2 Qué ofrece Turso y qué se usa

| Pieza | Qué hace | Aquí |
|---|---|---|
| Token de base o de grupo (JWT) | Se crea en el panel o con la CLI. Vencimiento propio (por defecto "never"), autorización `full-access` o `read-only`. | Nivel C: el de la PC y uno para tablet y celular, `full-access` y sin vencimiento, creados a mano (§4.3). |
| Permisos finos por tabla y acción | Documentados para la CLI y para JWKS. | No se usan. Sin la API la PC no puede crear un token por equipo, y la revocación sería de todos modos rotar el grupo. Los límites por rol los aplica la app (§4.6). |
| Revocación | No hay revocación por token. `turso db tokens invalidate` rota las llaves de **todo el grupo**, con una breve interrupción. | La base del consultorio va sola en su grupo. "Cortar acceso ahora" es esta rotación (§4.7). |
| Token de la API de la plataforma | Dejaría a la PC crear y rotar tokens (nivel A). | La cuenta no lo tiene (§9). |
| JWKS propio | En beta, solo con Clerk y Auth0. | No se usa: pide un servidor. |
| Plan gratuito | Sin lista de IP permitidas. | Sin bitácora de auditoría (V11): existe desde el plan Developer (US$4.99, 3 días) y solo anota acciones de la cuenta, no lecturas. |

**Por comprobar en una base de prueba real de Turso.** Siempre en un grupo o una cuenta de prueba, nunca en el grupo del consultorio: una rotación ahí corta al médico.

| # | Qué se comprueba | Qué decide | Antes de |
|---|---|---|---|
| V3 | Qué responde Hrana a un token rotado o desconocido (HTTP 401 o un código Hrana) | Mensajes y reglas del 401 | 8.14 |
| V4 | Cuánto dura la interrupción al rotar | Texto de "Cortar acceso ahora" | 8.15 |
| V7 | Texto y estado HTTP de `BLOCKED` | Traducción | 8.2 |
| V9 | Si el plan gratuito archiva una base sin uso (hay reportes de 10 días) y qué error da | Mensaje e instrucciones para desarchivarla después de unas vacaciones | 8.2 |
| V10 | Si las entradas de índice cuentan como filas escritas | Presupuesto de §3.2 | 8.2 |
| V11 | Si el plan gratuito tiene bitácora de auditoría, y de cuántos días | §4.7 | 8.15 |
| V12 | El tope de tamaño de una respuesta Hrana | Tamaño de página | 8.6 |
| V14 | Si una petición `batch` de Hrana con BEGIN/COMMIT es atómica en Turso | Opción de 8.6 | 8.6 |
| V15 | `EXPLAIN QUERY PLAN` de F1 y F2 en Turso real (libSQL no es SQLite 3.53) | 8.6 y 8.24 | 8.6 |
| V16 | Que `INSERT … ON CONFLICT DO UPDATE SET LastNumber = LastNumber + 1 RETURNING LastNumber` corra en Turso por Hrana como una sola instrucción atómica y regrese el número en su resultado, también con dos equipos a la vez | El contador de folios. Si no: leer el número n y `UPDATE … SET LastNumber = n + 1 WHERE Series = ? AND LastNumber = n`, que se reintenta si afecta 0 filas | 8.16 |
| V18 | BLOB con y sin relleno de base64, y la forma en que regresa | Que la PC lea lo que manda Turso: según el código de libSQL responde `{"type":"blob","base64":…}` sin el relleno `=` y acepta los argumentos con o sin él. La PC ya lee las dos formas y manda con relleno; si Turso rechazara el relleno, la PC lo quitaría al mandar | 8.12 |
| V17 | El tope de la petición con un BLOB de 0.5 a 32 MB | El tamaño del trozo y el manejo del error | 8.12b |
| V19 | Filas escritas y leídas con `LabBlobs` y `LabBlobChunks` | El presupuesto de `EVALUACION_RESULTADOS_LABORATORIO.md` §3 | 8.12b |
| V20 | Si borrar un BLOB libera el espacio que cuenta Turso, sin `VACUUM` | Si el tope de 2 GB es sobre lo que hay o sobre lo acumulado | 8.12b |
| V21 | Si la respuesta viene con gzip | Si la bajada cuesta +0% o +33% | 8.12b |
| V22 | Tiempos de 512 KiB y de 2 MiB en cada red | El tamaño del trozo y los tiempos de espera | 8.12b |
| V23 | Un `batch` con `BEGIN`/`COMMIT` que lleva un BLOB de 1 MB | Si el cierre va en la misma transacción que el último trozo | 8.12b |

V1, V2, V5, V6, V8 y V13 se retiraron: solo servían al nivel A o a la base de vinculación. El resultado se anota en `docs/TURSO_SYNC_ARCHITECTURE.md` §6.

### 4.3 Credenciales: nivel C

| Quién lo tiene | Token | Dónde vive |
|---|---|---|
| PC | Su propio token `full-access`, pegado a mano como hoy. **Es la única que cambia el esquema.** | Cifrado con DPAPI. |
| Tablet y celular | Un token `full-access` de la base, pegado en el equipo (copiado del panel de Turso) o escaneado del QR que muestra la PC (8.14). Puede ser el mismo de la PC o uno creado aparte: Turso no los distingue y no deja anular uno solo. | En el equipo, envuelto con el Keystore. |
| Otra PC con Windows | Su token, pegado como hoy, y «Unirse a esta nube» (8.8). | Con DPAPI. Se vuelve a pegar después de una rotación. |
| QR de la PC (8.14) | La URL y el token. | En la pantalla de la PC, solo mientras está abierto (2 min). |

- **Decisión del médico (2026-10-11):** en lugar de la vinculación por QR en la red local (§4.4, descartada), cada equipo recibe la URL y el token como la PC. Lo que se ahorra (el servidor temporal en la PC, el Firewall, el protocolo cifrado igual en C# y en Kotlin) vale más que lo que protegía: el token igual vive en cada equipo y un equipo perdido igual se corta rotando.
- Los tokens no vencen y **todos** mueren con una rotación (`turso db tokens invalidate` anula todos los de la base y los del grupo; no hay forma de anular uno solo, §4.7). Un vencimiento sin renovación automática solo dejaría a la tablet con cambios atorados.
- Turso no distingue roles: los tokens pueden todo, y los límites por rol los aplica la app (§4.6).

### 4.4 Vinculación por QR en la red local (descartada el 2026-10-11)

> **Descartada por decisión del médico** (ver §4.3 y el paso 8.14): la tablet y el celular reciben la URL y el token pegados o escaneados de un QR de la PC, y la PC aprueba cada equipo nuevo, como en el «Respaldo si falla la red local» de abajo. Lo que sigue se conserva solo como referencia.

La PC solo ofrece vincular si su sincronización está sana, existe `_tpvc_meta.fullUploadAtUtc`, ya se pegó el token para tablet y celular y hay menos de 3 equipos activos. La PC y el equipo deben estar en la misma red (el Wi-Fi del consultorio).

1. **El médico la inicia** en *Nube y dispositivos → Equipos vinculados → Vincular tablet o celular* y elige el rol. La PC:
   - crea un `PairingId` de 16 bytes y un secreto de 128 bits, los dos aleatorios;
   - reserva un código de equipo (`T1`, `C1`…: el mayor de `DeviceRegistry` + 1);
   - abre un `TcpListener` (`System.Net.Sockets`) en un puerto al azar, **solo en sus direcciones privadas** (RFC 1918 y de enlace local) y solo mientras la ventana está abierta, 10 min como máximo. Solo acepta conexiones desde direcciones privadas. No pide administrador ni `urlacl`, y no usa Kestrel ni el runtime de ASP.NET.
   - La primera vez, el Firewall de Windows puede preguntar si permite a TPVC en redes privadas, y la red del consultorio debe estar marcada como privada en Windows. Lo explica el manual (8.14).
2. **El QR** es `tpvc-pair:1:` + base64url(JSON) con `a` las direcciones de la PC, `o` el puerto, `p` el `PairingId`, `s` el secreto, `r` el rol, `d` el código de equipo, `n` el nombre del consultorio y `e` el vencimiento en UTC. **Ningún token.** Son unos 300 caracteres: un QR chico. El mismo texto, pegado, sirve para que otra PC con Windows se una ("Unirse con código") y reciba su código (`P2`…).
3. **El equipo se presenta.** Se niega si no tiene bloqueo de pantalla seguro.
   - Lee el QR, revisa el vencimiento y genera su propio `DeviceId`.
   - Se conecta a la PC (5 s por dirección) y manda el `PairingId`, su `DeviceId`, nombre, modelo y un nonce, con un HMAC-SHA256 que prueba que tiene el secreto. El secreto nunca viaja.
   - La PC atiende un solo equipo por ventana. Un HMAC inválido no recibe respuesta, y tres cierran la ventana.
   - Los dos muestran un código de confirmación de 6 dígitos sacado del secreto y del mensaje del equipo.
   - Las llaves salen de HKDF-SHA256(secreto, sal = `PairingId`), con un `info` distinto para el HMAC, el código y el cifrado.
4. **El médico confirma en la PC:** "¿Vincular «Galaxy Tab S9» como Tablet? Código 482 913". Con *No*, la PC cierra la ventana sin mandar nada.
5. **Con *Sí*, la PC:**
   - inserta la fila en `DeviceRegistry`;
   - manda AES-256-GCM(llave de cifrado, datos asociados = `PairingId` ‖ `DeviceId`) de {URL de la base del consultorio, token para tablet y celular, rol, código, clinicId};
   - cierra el `TcpListener`.
6. **El equipo termina.** Descifra, guarda la credencial envuelta con el Android Keystore, avisa a la PC y empieza su primera descarga (§7.3).

**Respaldo si falla la red local** (Wi-Fi con aislamiento de clientes, Firewall): en el equipo se teclean la URL de Turso y el token para tablet y celular. El equipo escribe su latido y espera. La PC muestra "Equipo nuevo: «Galaxy Tab S9». ¿Vincular como Tablet?", con los últimos 6 caracteres de su `DeviceId` en las dos pantallas, y al aceptar escribe su fila en `DeviceRegistry`. Hasta entonces el equipo no baja datos.

Volver a vincular con el mismo `clinicId` y el mismo `DeviceId` conserva la base del equipo (si no se borró por la regla 3 de §4.7) y sube lo pendiente. Un `clinicId` distinto empieza de cero, después de subir o exportar lo pendiente (§4.7).

### 4.5 Qué obtiene quien fotografía el QR o escucha la red

> **Con la decisión del 2026-10-11 cambia:** el QR de la PC lleva el token, así que quien lo fotografía mientras está en pantalla obtiene acceso completo, igual que quien copia el token del panel. Por eso se muestra solo a pedido y se cierra solo a los 2 minutos. La red no lleva nada: el equipo habla solo con Turso, por HTTPS. Lo de abajo describe el QR descartado.

- **Fuera de la red local o después de los 10 min:** nada. El QR no lleva token y la PC ya no escucha.
- **En la red local, durante la ventana y con la foto:** puede intentar vincularse antes que la tablet. La PC pregunta al médico con el nombre y el código de confirmación; el de un equipo ajeno no coincide con el de la tablet, y la tablet recibe "Ya hay un equipo vinculándose", así que el médico rechaza y empieza otra vez.
- **Quien escucha el Wi-Fi sin el QR** solo ve el HMAC y la credencial cifrada: el secreto nunca pasa por la red.
- **El costo del nivel C, dicho claro:** cada equipo guarda un token `full-access` sobre todos los expedientes, sin vencimiento. Los límites por rol (el celular no edita notas ni cobra; la tablet no toca catálogo ni configuración) los aplica la app, así que quien saque el token de un equipo puede hacer todo. Un equipo perdido solo se corta rotando el grupo, y eso obliga a pegar tokens nuevos en la PC y a volver a vincular los demás. Por eso: SQLCipher, credencial envuelta con el Keystore, bloqueo de pantalla obligatorio y "Cortar acceso ahora" marcado por defecto (§4.7, §4.8).

### 4.6 Registro, latido y permisos

- **`DeviceRegistry`** es la autoridad para rol, código y revocación. La app de Kotlin oculta pantallas y bloquea escrituras según el rol que lee ahí.
- **`RegisteredDevices`** sigue siendo el latido. Cada equipo escribe su fila con la misma instrucción y la hora del servidor, y ahora también `Platform`, `SyncProtocol`, `HandoffProtocol`, `ContractRevision`, `LastPushUtc`, `PendingCount`, `OldestPendingUtc` e `InstanceNonce`. El celular también lo escribe con la app al frente: sube cambios, así que su `PendingCount` cuenta para *Quitar* y para la barrera de 8.20. Su rol lo sigue dejando fuera de la consulta entre equipos.
- El rol de la PC queda fijo en `WindowsDesktop`: se quita el selector (8.2).
- `DevicePermissions` gana `CanViewClinicalRecords`, `CanShareDocuments`, `CanScheduleAppointments` y `CanRegisterStockEntries`.
- Con el nivel C, todo lo de esta tabla lo hace cumplir la app.

| Función | PC | Tablet | Celular |
|---|:---:|:---:|:---:|
| Ver expedientes (`CanViewClinicalRecords`, nuevo) | sí | sí | sí |
| Editar expedientes y consulta (`CanManageClinicalRecords`) | sí | sí | no |
| Recetar | sí | sí | no |
| Cobrar y vender, también sin conexión | sí | sí | no |
| Agendar citas (`CanScheduleAppointments`, nuevo) | sí | sí | sí |
| Registrar entradas de mercancía: lote y movimiento de entrada (`CanRegisterStockEntries`, nuevo) | sí | no | sí |
| Editar inventario y catálogo: productos, precios, conteos, cerrar lotes (`CanManageInventory`) | sí | no, solo ve | no, solo ve |
| Resumen, alertas y compra sugerida | sí | sí | sí |
| Corte de caja (`CanViewCashCut`) | sí | no (oculto) | sí (supuesto, §9) |
| Compartir receta y ticket (`CanShareDocuments`, nuevo) | sí | sí | sí |
| Consulta entre equipos | sí | sí | no (`GetLinkedDevicesAsync` excluye el celular) |
| Configuración, Excel, respaldos, Turso, vincular, perfil y membrete (`CanConfigureSystem`) | sí | no | no |

### 4.7 Quitar un equipo y equipo perdido

**Quitar un equipo que no se perdió.** La PC muestra antes sus cambios pendientes ("La tablet tiene 12 cambios sin subir desde las 10:42", de `PendingCount` y `OldestPendingUtc`). Al leer su fila revocada con motivo "quitado", el equipo sube lo pendiente y luego borra su base, su credencial y sus borradores. Si no puede subir, exporta lo pendiente a un archivo que se puede volver a aplicar.

**Perdí este equipo** (en *Equipos vinculados*):

1. **Revocar** con motivo "perdido": la PC pone `RevokedAtUtc`. Si el equipo Android se conecta antes de la rotación y lee su fila, borra su base, su credencial y sus borradores sin subir nada. Una PC marcada como perdida no borra nada (v1.33.0).
2. **"Cortar acceso ahora"** (marcado por defecto). Sin token de la API la PC no puede rotar sola, así que guía al médico:
   - rotar los tokens en el panel de Turso o con `turso db tokens invalidate` (anula **todos** los de la base y los del grupo, también el de la PC), con una breve interrupción (V4);
   - crear un token nuevo y pegarlo en la PC, que sincroniza en ese momento;
   - los demás equipos reciben 401 y muestran "Pegue el token nuevo" sin perder sus cambios pendientes. Se pega (o se escanea del QR de la PC) y siguen: el que tenía cambios pendientes conserva su base y los sube; el que no tenía nada pendiente ya se borró (regla 3) y vuelve a bajar todo (~20–30k lecturas). No hace falta aprobarlos otra vez: su fila de `DeviceRegistry` sigue.
3. **Reglas ante un 401** (con el nivel C no hay vencimientos: un 401 es una rotación):
   - con cambios pendientes, el equipo se bloquea y los conserva hasta volver a vincular;
   - sin nada pendiente (casi siempre el celular), se borra solo tras dos 401 con 10 min de diferencia.
   - un HTTP 400 con "JWT" en el cuerpo es un token mal copiado o incompleto (V3), no una rotación: pide pegarlo otra vez y no cuenta para borrarse.
4. **Límites:**
   - El plan gratuito no tiene bitácora de auditoría (V11), y la de los planes de pago solo anota acciones de la cuenta (crear tokens o bases), no quién leyó datos: hay que suponer que el equipo perdido pudo leer todo hasta la rotación. Lo único que queda es su última sincronización en *Equipos vinculados* (su latido).
   - Se recomienda borrarlo también con "Encontrar mi dispositivo".
   - Sin rotar, el equipo perdido conserva el acceso: no hay otro límite.
5. **Si se pierde la PC:** rotar desde el panel de Turso, instalar la PC nueva con "Recuperar desde la nube" (8.8) y el token nuevo, marcarla como "Esta PC reemplaza a la principal" (8.10) y pegar el token nuevo en la tablet y el celular.

### 4.8 Seguridad local en Android

- **Base cifrada.** SQLCipher (`net.zetetic:sqlcipher-android` con `SupportOpenHelperFactory` de Room), con una frase aleatoria de 32 bytes envuelta por una llave AES-256-GCM del Android Keystore.
  - Usa StrongBox si el equipo lo tiene.
  - `unlockedDeviceRequired=false`, para que la sincronización siga con la pantalla bloqueada.
- **Credencial:** en un archivo envuelto con una llave del Keystore. `EncryptedSharedPreferences` está en desuso.
- **Sin vinculación** si el equipo no tiene bloqueo de pantalla seguro.
- **Sin copias de seguridad:** `allowBackup=false` y `dataExtractionRules` que excluyen todo. Una copia clonaría el `DeviceId` y además no podría restaurar las llaves del Keystore.
- **Bloqueo de la app en el celular** con huella o con la credencial del equipo, después de 5 min en segundo plano.
- **Red:** el TLS normal del sistema, sin fijar certificados. `usesCleartextTraffic=false` explícito, porque Android 8.0 y 8.1 (API 26–27) permiten texto en claro por defecto; la app solo acepta URL `https://`. La vinculación usa un socket TCP en la red local, no HTTP: la protege el secreto del QR (§4.4), así que no hace falta abrir el texto en claro.

---

## 5. Cómo quedan A1–A6 y los demás riesgos

| # | Riesgo (comprobado en v1.19.0) | Solución | Paso |
|---|---|---|---|
| A1 | **Existencias como valores.** Si dos equipos venden, gana la última escritura y se pierde un descuento. Los lotes no se pueden reconstruir con los movimientos de hoy: una venta anota solo el primer lote aunque FEFO descuente de varios (`DeductFromBatches`, `InventoryService.cs:416-444`); `SaveBatchAsync` no deja movimiento; las devoluciones van a un lote adivinado (:747-757); borrar un lote lo borra físicamente (:377-411). Cancelar el mismo ticket en dos equipos devuelve dos veces. | **Libro de movimientos:** `Delta` con signo, `SaleItemId`, un movimiento por lote, devoluciones con `SyncId` determinista (UUIDv5) y regla elegida por `Sale.LedgerVersion`, lotes cerrados en lugar de borrados, conteos como ancla, saldos iniciales una sola vez en la PC principal, y existencias y lotes como columnas derivadas que se recalculan y nunca se aplican al bajar. Se permite quedar en negativo, con aviso por producto y por lote. | 8.19, 8.20 |
| A2 | **Siembra en cada equipo.** Categorías y SERV-001..005 con `SyncId` aleatorios (`AppDbContext.cs:114`) se duplican al sincronizar. `App.xaml.cs:82` pasa `seedDemoData: !isFirstRun`, así que en cualquier arranque posterior al primero con la tabla de pacientes vacía siembra los pacientes demo. `SeedClinicalServices` vuelve a correr si la tabla está vacía, incluso a media descarga. | Pacientes demo solo desde el asistente; `SyncId` fijos (UUIDv5) para el catálogo de una base nueva; "Unirse a una nube existente" con `CloudJoined` (nunca vuelve a sembrar); Kotlin nunca siembra. | 8.8 |
| A3 | **Folios por equipo.** EXP y TKT son el mayor número local + 1. CNS usa la hora (`ActiveConsultation.cs:39`), tiene otros respaldos (al azar, por conteo, `-IMPnn`), no es columna única, y el `-R1` nunca le aplica. El `-R1` (`TursoSyncService.cs:887-920`) solo existe en el equipo que recibe. | `FolioFactory`: con conexión, EXP y TKT toman el siguiente número de `FolioCounters` en Turso con una sola instrucción atómica, en cualquier equipo. Sin conexión, el folio lleva el código del equipo (`EXP-2026-T1-001`, `TKT-20261009-T1-0001`; la PC, `P1`) y sube así. La PC principal lo regulariza una vez al día con el siguiente número y guarda el folio con letra en `OriginalFolio`, que la búsqueda también encuentra. CNS sigue saliendo de la hora: sin conexión lleva la letra (`CNS-20261009-T1-103015`) y nunca se regulariza. Los folios que ya existen no cambian. Último número de las series con letra guardado fuera de la base. Índice normal en lugar de único y sin `-R` (respuesta 1). | 8.16, 8.17 |
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
| H10 | **El paquete de vinculación lleva el token completo en claro y el rol no se aplica.** | Se quita el paquete (hecho en v1.20.0). Nivel C: el token de la PC y uno para tablet y celular, que solo viaja cifrado. QR limitado a la red local, sin token, con un secreto de 128 bits y una ventana de 10 min. `DeviceRegistry`, revocación y rotación del grupo. El rol lo aplica la app. | 8.2, 8.10, 8.14, 8.15 |
| H11 | `EnsureStock` (:446-457) e `IsRecordNumberTakenAsync` (:1169) solo miran datos locales. | Se quedan locales. Lo cubren el contador de folios (con letra sin conexión y la regularización diaria), la sincronización rápida antes de cobrar y de agendar (con red), la política de negativos y la detección de duplicados después de bajar. | 8.16, 8.17, 8.20, 8.23 |

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
| Restaurar un respaldo o reemplazar la nube repetiría números EXP y TKT ya impresos, y una PC sin código crearía sin conexión folios que chocan. | El contador vive en Turso y "Reemplazar la nube" no lo toca; el último número de cada serie con letra se guarda fuera de la base; una PC sin código no crea folios sin conexión. | 8.9, 8.10, 8.16 |
| El `-R` sin marca deja al mismo paciente con dos folios. | Índice normal, sin `-R`, aviso de folios repetidos. | 8.16 |
| La regularización de un folio y una edición del mismo registro en otro equipo, al mismo tiempo. | Corre solo con la nube al día y sin cambios pendientes en ningún equipo (`PendingCount`). En pacientes, la combinación por campo conserva las dos (8.22); en ventas gana la marca mayor, como hoy. Si una fila ya regularizada vuelve con su folio con letra (una edición sin conexión hecha antes de bajar la regularización), la PC le devuelve el mismo número (registro local `OriginalFolio` → folio asignado) en lugar de tomar otro del contador. | 8.17, 8.22 |
| Las hojas `Active*Json` se editan fuera de la consulta y se pisan completas: renglones o `ChargedUnits` perdidos, cargos dobles. | `RowId` y combinación por renglón. | 8.18, 8.22 |
| Las operaciones que fijan una cantidad (baja de lote, conteo, diálogo de producto) cuentan doble lo vendido sin conexión. Un lote borrado deja sin lote al movimiento que llega tarde, sin aviso. | Lotes cerrados en lugar de borrados, conteos como ancla y aviso de lote negativo. | 8.19, 8.20 |
| La combinación por campo elige mal cuando falta una marca, y una PC v1.19 no lee `protocol`: no hay barrera técnica. | Regla de escritura sin marcas; 8.20 se niega mientras haya otra PC sin actualizar. | 8.20, 8.22 |
| Aceptar una consulta solo compara el `UpdatedAtUtc` de la fila (`ConsultationHandoffViewModel.cs:1199-1206`): una tablet con la fila "más nueva" por otro campo acepta sin los últimos cargos de la PC. | La copia lleva las marcas por campo y la de la nota. | 8.22 |
| `NextAppointment*` sigue el orden de guardado, no el clínico (`InventoryService.cs:1523-1533`: la última cita guardada, aunque esté "Atendida"). | Resumen derivado, recalculado después de cada bajada. | 8.22 |
| Las fechas del negocio salen de `DateTime.Now` y el contrato no lo dice. Android 8 y 9 sin tzdata actualizada aplican el horario de verano que México quitó en 2022. | `timeKind` en `schema.json`; Kotlin usa la zona del consultorio, con UTC−6 fijo si sus reglas son viejas. | 8.3, §7.3 |
| Revocar, cambiar de `clinicId` o de generación, o subir `minClientProtocol` borran cambios clínicos sin subir. | `PendingCount` en el latido; subir o exportar antes de borrar; nunca borrar ante un 401 con cambios pendientes; cada fila de la cola guarda su protocolo. | 8.9, 8.10, 8.15 |
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

Las etapas 0 y 1 valen la pena aunque nunca exista la app: dejan una sincronización sin pérdidas, la hora del servidor, una siembra segura y la restauración correcta. Después de la etapa 2 puede salir la primera versión del celular, que solo baja; la tablet y lo que escribe el celular necesitan también las etapas 3 y 4.

### Etapa 0 · Base

#### 8.1 · Documentación
- **Cambia:** este plan y los cambios de §8 en los demás documentos. No toca código.
- **Prueba:** la CI de Windows sigue verde.
- **Riesgo:** ninguno.

#### 8.2 · CI de Linux, simulador más fiel, freno de consumo y limpieza
- **Hecho en v1.20.0 (2026-10-10).** V7, V9 y V10 no se comprobaron antes: `BLOCKED` se reconoce por el código Hrana o por el texto de una respuesta HTTP de error, y una base archivada por "archived" (`TursoException.ClassifyServiceError`); quedan en la lista de `docs/PENDIENTES_ANDROID.md` §D. Decisiones al programarlo:
  - La revisión de "descarga completa" (la «Sincronización completa» o la primera sincronización) va antes de subir nada; se cuenta al empezar a bajar, cuando la red ya respondió, así un corte de red no dispara el freno.
  - El freno se guarda en `turso_config.json` y sigue al reabrir; los contadores del día usan la hora del consultorio. *Reanudar* vuelve a contar el día desde cero, así el freno solo se vuelve a activar si el consumo anormal sigue.
  - Pausados: sincronizar tras guardar, al abrir y la revisión de consultas entre equipos (`ThrowIfAutomaticSyncPaused`, sin llegar a Turso). Siguen: "Sincronizar ahora" y lo que hace la consulta entre equipos a pedido del médico. Con el freno activo, otra descarga completa espera a *Reanudar*.
  - Tras `BLOCKED`, un solo reintento automático a la hora (`Task.Delay` con `TimeProvider`); una sincronización buena quita la pausa.
  - Estimador de `FakeHranaServer`: pasos de recorrido completo (`SQLITE_STMTSTATUS_FULLSCAN_STEP` + 1), filas que entran a un ordenamiento antes del `LIMIT` (cuenta de la consulta sin el `LIMIT`), filas devueltas o cambiadas y la tabla entera en `ALTER` y `CREATE INDEX`; mínimo 1. Las filas leídas por un índice sin ordenar no se ven en SQLite.
  - El runtime de escritorio del zip de Windows viene precompilado (ReadyToRun) para Windows y el de Linux lo rechaza ("incorrect format"); la CI le quita esa parte con `scripts/strip-readytorun.ps1` (marca de solo IL y sin los directorios nativos del PE) y Linux compila el IL al vuelo.
  - `[TestCategory("Windows")]`: `UiSmokeTests`, `UiPerfTests`, dos de Apariencia, dos convertidores de WPF y `MedicalRecords_TakesTheContextWhenItIsCreated` (el anfitrión de WPF de Pacientes). Ninguna prueba usa DPAPI real (fuera de Windows el token se guarda como `B64:`).
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
- **Hecho en v1.20.1 (2026-10-10).** `contracts/sync/schema.json` (revisión 1) y `contracts/golden/` (`values`, `lww`, `stamping`, `uuid5`), con `contracts/README.md`. Decisiones al programarlo:
  - El mapeo del modelo salió de `TursoSyncService` a `Services/SyncSchema.cs` sin cambiar su comportamiento: el motor y el contrato leen lo mismo. Las reglas puras quedaron en `SyncRules` (gana el más reciente, marca local, subida rechazada) y `SyncIds` (UUIDv5), y el motor y `AppDbContext` las usan.
  - `lww.json` prueba las reglas de la nube con el SQL real (`UpsertSql`, `SyncSchema.TombstoneUpsertSql` y `DeleteTombstonedRowSql`) ejecutado sobre `FakeHranaServer`, no con una copia en C#. La regla de la subida rechazada (`SyncRules.AfterRejectedUpsert`) ya tiene vectores; el motor la usa desde 8.6.
  - `Sale.PatientName` y `SaleItem.ProductName` **sí** se mapean como columnas extra (alias con get y set de `CustomerName` y `Description`): viajan dos veces con el mismo valor y el contrato los marca con `aliasOf`.
  - Toda fecha declara su `timeKind` en `SyncContract.TimeKinds`; una columna de fecha nueva sin declararlo hace fallar la exportación. `LastVisit` es `clinicLocal` (sale de la fecha de la consulta, con hora) y `NextAppointmentDate`, `dateOnly`.
  - `ClientSetNull` (lo que EF usa por omisión en una referencia opcional) se reporta como `restrict`, porque en SQLite queda como NO ACTION. Hoy ninguna referencia sincronizada lo usa.
  - Espacio de nombres de TPVC para los UUIDv5: `uuid5(URL, "https://github.com/Marioalr/TPVC/sync")` = `ff997a05-ea47-5008-a4c1-6d022b9f1a6f`. Los derivados (`SyncIds.Derived`) unen el prefijo y los SyncId en minúsculas con `:`.
  - La revisión entra a la huella del esquema: al actualizar, cada nube repite una vez la revisión del esquema (sin cambiar tablas) y escribe `_tpvc_meta.contract`. Al regenerar, `GoldenVectorTests` exige que un cambio de tablas, columnas o enums solo agregue y suba `SyncContract.Revision`.
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
- **Hecho en v1.20.2 (2026-10-10).** Decisiones al programarlo:
  - La fila remota apartada se guarda con cada valor como argumento Hrana (`TursoClient.FormatArg`), así se vuelve a aplicar sin perder su tipo.
  - Lo apartado al bajar se reintenta **al final de cada descarga** (no hasta la siguiente): lo que llega en la misma sincronización se aplica en ella. Antes de reintentar se pregunta, aquí y en la nube (una instrucción por tipo de principal), qué principales que faltan tienen lápida.
  - Una fila cuyo principal **obligatorio** tiene lápida se descarta con un aviso en la bitácora (el plan solo decía qué hacer con la opcional): no puede existir sin él, y apartarla dejaría el contador arriba para siempre.
  - Al subir, la fila apartada se vuelve a leer y se intenta aunque el cursor de reloj ya la pasó. Con las llaves foráneas activas solo pasa con filas huérfanas.
  - `SaveClinicalServiceAsync` compara por `ProductId` (si un producto se repite, gana el último) y solo cambia cantidad e indicaciones.
  - El contador sale en el estado ("✓ Sincronizado … · N cambios sin sincronizar") y en *Nube y dispositivos*, con una explicación.
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
- **Hecho en v1.20.3 (2026-10-10).** Decisiones al programarlo:
  - **Los triggers no usan `INSERT OR REPLACE`:** borran la anotación anterior de la fila y la vuelven a insertar (mismo efecto, un `Seq` nuevo). En un trigger que dispara una acción de llave foránea (el borrado en cascada de las filas hijas, el `SET NULL` del lote de un movimiento), SQLite cambia el `OR REPLACE` por `ABORT`, y el borrado entero fallaba con "UNIQUE constraint failed" (lo encontró la prueba de borrar un lote).
  - `AFTER UPDATE OF` vigila las columnas que viajan (hoy ninguna es derivada) más `SyncId` y `UpdatedAtUtc`. Nombre de cada trigger: `TPVC_sync_<tabla>_<ins|upd|del>_<8 hex del SHA-256 de su definición>`.
  - En el registro y en las lápidas, el `SyncId` va en mayúsculas (`upper()`), como lo guarda EF Core; pasa a minúsculas al subir (`SyncIds.Format`). Las filas se buscan con `upper("SyncId")`, así una fila con el `SyncId` en minúsculas tampoco se pierde.
  - La marca de la lápida es la regla de `SyncRules.NextLocalStamp` escrita en SQL (`SyncChangeLog.StampSql`): `CASE WHEN guardada >= ahora + desfase THEN guardada + 1 ms ELSE ahora + desfase END`, exacta a 7 decimales (SQLite solo suma milisegundos, así que suma sobre los primeros 23 caracteres y conserva los 4 dígitos restantes). `GoldenVectorTests` la compara con `stamping.json`. El desfase sale de `DbMeta.ClockOffsetMs`, que hasta 8.7 no existe (= 0).
  - La subida lee el registro (`Seq` en (cursor, toSeq]), sus filas y sus lápidas en una sola transacción de lectura. Lo anotado que todavía existe sube completo; lo que ya no, como su lápida más reciente. El cursor (`DbMeta.LastPushedSeq`) se guarda en cuanto termina la subida, aunque falle la bajada.
  - Primera vez sin cursor (al actualizar, una base restaurada) y «Sincronización completa»: se anotan todas las filas y lápidas que falten (`INSERT OR IGNORE`, lo ya anotado conserva su `Seq`) y se sube desde 0.
  - Restaurar un respaldo borra `LastPushedSeq` y pone un `DbInstanceId` nuevo en la base restaurada; `LastPushedLocalUtc` desaparece de la configuración.
  - Los triggers se instalan en `DatabaseInitializer.EnsureSchema`, fuera del atajo de `user_version`, y la sincronización lo revisa una vez por sesión. El respaldo antes de instalarlos por primera vez (`consultorio-…-antes-registro-de-cambios.db`) solo se hace si la base tiene datos; si falla, se anota en la bitácora y el programa abre igual.
  - `SyncChangeLog.MarkForUploadAsync` existe, sin quien la llame todavía (8.22, 8.A). Desde v1.39.0 la usa la combinación de pacientes (8.22).
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
- **Antes:** [T] V12, V14 y V15 con una copia real de ~20k filas. **Hechas el 2026-10-10** con `scripts/verify-turso-hrana.ps1` y 20k filas sintéticas con la forma de las tablas (los planes dependen de tablas e índices, no del contenido; así no sale ningún dato del consultorio). Resultados en `docs/TURSO_SYNC_ARCHITECTURE.md` §6:
  - V12: sin tope hasta 64 MB en una instrucción. El tamaño de página y "una tabla por petición" en la descarga completa se quedan para acotar memoria y tiempo, sobre todo en Android, no por Turso.
  - V14: un `batch` con `BEGIN`/`COMMIT` y `ROLLBACK` si un paso falla es atómico. Aplica la opción transaccional de abajo.
  - V15: confirmado F1 (26,666 filas leídas en la primera página con la consulta de hoy, ~2,000 con `>=` adelante) y F2 (2,006 contra 6 con el índice parcial).
- **Hecho en v1.20.4 (2026-10-10).** Decisiones al programarlo:
  - **Cursores en `DbMeta`**, `PullCursor:<tabla>` = "SyncedAtUtc|SyncId", uno por tabla y uno para las lápidas. Se guardan en la misma transacción que aplica la página (`SaveRemoteChangesAsync`), nunca adelante de lo aplicado. Las tablas sin nada nuevo avanzan hasta la marca de agua en una sola escritura al final. Restaurar un respaldo los borra.
  - **Al actualizar desde v1.20.3** no hay cursores: la marca de agua de la configuración (`LastPulledServerWatermark`) vale para todas las tablas esa vez, así nadie vuelve a bajar todo ni cuenta una descarga completa. Una tabla nueva (sin cursor cuando las demás sí tienen) empieza desde el principio.
  - La marca de agua sigue siendo `now - 5 s` dentro de la foto: una escritura que tomó su hora antes de la foto y terminó después todavía puede llegar.
  - Si la petición única de la bajada incremental no cabe o tarda demasiado, se cae a una tabla por petición. Una página se reduce a la mitad (hasta 10 filas) ante `RESPONSE_TOO_LARGE`, `TIMEOUT` o HTTP 413.
  - **Desempate:** las filas que la nube no aceptó se releen con sus lápidas en una sola petición. La fila de la nube se aplica si su marca es igual o más nueva que la local; si la local se volvió a editar entre la subida y la relectura, se conserva y sube en la siguiente. Es una regla nueva del contrato, `SyncRules.ServerRowAppliesAfterReject`, con sus vectores en `lww.json`.
  - **Operaciones completas:** una fila con referencia obligatoria y borrado en cascada a otra que sube en la misma sincronización va en el grupo de esa (la venta con sus renglones, el producto con sus lotes y movimientos, el paciente con sus consultas y citas, el servicio con sus insumos), y la lápida va con su `DELETE`. Un grupo nunca se parte entre lotes; uno más grande que el lote va solo. Los movimientos de una venta todavía no se enlazan con ella (no hay `SaleItemId` hasta 8.19).
  - Una sincronización sin cambios pasó de 16 peticiones a 1 (`BEGIN`, marca de agua, 14 páginas, perfil, `COMMIT`).
  - `FakeHranaServer` aprendió las peticiones `batch` (condiciones `ok`, `error`, `not`, `and`, `or` e `is_autocommit`, y `ROLLBACK` al cerrar si quedó una transacción abierta). Su revisión de planes falla por omisión, y un ordenamiento solo del último término ya no cuenta como leer todas las candidatas.
  - Una prueba de la consulta entre equipos daba de baja un producto en la tablet sin marca nueva. Desde el 8.5 ese cambio sube, la nube lo rechaza por empate y ahora el desempate lo regresa a la versión de la nube; la prueba lo guarda como algo que llegó de la nube.
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
- **Hecho en v1.20.5 (2026-10-10).** Decisiones al programarlo:
  - **La primera instrucción** (`SELECT` de la hora del servidor, la marca de agua y las claves `clinicId`, `generation`, `generationReady`, `protocol`, `minClientProtocol`, `fullUploadAtUtc` y `stampsRepairedAtUtc`) va en su propia petición cuando hay algo que subir, tablas con columnas nuevas o es la «Sincronización completa». Si no, es el primer paso del batch de la bajada: una sincronización sin cambios sigue siendo 1 petición, y nada se aplica antes de revisarla.
  - **Desfase:** hora del servidor contra la de aquí a la mitad del viaje, redondeada al milisegundo. Se guarda en `DbMeta.ClockOffsetMs` si cambia más de 250 ms. `ISyncClock` (`Services/SyncClock.cs`) lo lee una vez por base y lo recuerda; `StampSyncMetadata` marca con `NextLocalStamp(ahora, guardada, desfase)` y los triggers de 8.5 ya lo usaban. Se mide aunque la nube resulte ajena, para que el aviso de hora salga igual. El aviso va en el estado y en *Nube y dispositivos*.
  - **Marcas en el futuro:** en cada sincronización se reparan **todas** las filas locales y lápidas con marca más de 2 min adelante de la hora del servidor, no solo las pendientes. Una fila no pendiente con marca adelantada impediría aplicar las ediciones de otros equipos. Se hace con los triggers apagados: lo pendiente ya está anotado y lo demás no tiene por qué subir. En la nube se repara una sola vez por nube, con la clave nueva `_tpvc_meta.stampsRepairedAtUtc`.
  - **Protocolo:** con `minClientProtocol` mayor que 2, la sincronización se detiene antes de escribir ("Sincronización detenida: actualice TPVC en esta computadora", estado *En pausa*).
  - **Identidad:** con un `clinicId` distinto en la nube, nada se sube ni se aplica. Lo automático deja de intentar con esa URL hasta que se resuelva o cambie la URL. El estado es *En pausa*, con el título "esta nube es de otra base" y el detalle en un aviso. «Esta PC ya usaba esta nube» lee una vez los `SyncId` de cada tabla de la nube y solo une la base si todo lo que esta PC ya subió (lo anotado hasta `LastPushedSeq`) está en la nube. Si falta algo, no cambia nada.
  - **"Reemplazar con la nube" pasa a 8.8**, junto con "Unirse" y "Recuperar desde la nube". Borra todo lo local, y si la descarga se corta, el siguiente arranque volvería a sembrar el catálogo y lo duplicaría en la nube. `CloudJoined`, que lo impide, es de 8.8.
  - **Columnas nuevas:** en lugar de la revisión del contrato se guardan los nombres de las columnas con que se bajó cada tabla (`DbMeta.PullColumns:<tabla>`); así se sabe exactamente cuáles son nuevas. La primera vez (al actualizar a v1.20.5) se anotan las de hoy, sin bajar nada. La fila de la nube con la misma marca se aplica completa; la que aquí es más nueva recibe solo las columnas nuevas.
  - **Reporte:** las tablas `legacy_v1_*` solo se avisan; limpio es sin filas apartadas y con los mismos conteos aquí y en la nube. Si sale limpio, una transacción escribe `clinicId` (una sola vez), `protocol` y `minClientProtocol` (2, sin bajarlos), `manifest` (`SyncContract.BuildSchemaJson`, igual a `contracts/sync/schema.json`), `generation` y `generationReady` (si faltan) y `fullUploadAtUtc`. El `clinicId` queda también en `DbMeta`. El reporte se ve en *Nube y dispositivos*.
  - Los conteos, la revisión de los `SyncId` y la reparación única de la nube recorren tablas completas a propósito: empiezan con el comentario `TursoSyncService.FullScanMarker` y `FakeHranaServer` no los revisa.
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
- **Hecho en v1.20.6 (2026-10-10).** Decisiones al programarlo:
  - **Siembra:** `Initialize` siembra las 5 categorías solo si no hay ninguna y los 5 servicios solo si no hay ninguno, como antes, pero con `SyncIds.Seeded` y la marca `DatabaseInitializer.SeedStampUtc` (2000-01-01). Los insumos por defecto de un servicio solo existen con los productos demo, y también llevan `SyncId` fijo. Los triggers anotan lo sembrado para subir. Con `CloudJoined` no se siembra nada, tampoco los datos demo del asistente (`SeedDemoData`).
  - **Base intacta:** ninguna fila en las tablas que sincronizan, salvo categorías cuyo nombre sea de las sembradas y servicios con código SERV-001..005. Se revisa por nombre y código, no por `SyncId`: una base nueva de antes de v1.20.6 (catálogo con `SyncId` aleatorios) también puede unirse. Si no está intacta, el mensaje dice qué tiene ("pacientes: 3, productos: 9") y ofrece «Esta PC ya usaba esta nube» o «Reemplazar con la nube».
  - **Unirse** exige que la nube ya tenga `clinicId` (si no, primero la «Sincronización completa» de la PC que la usa). En una transacción, con los triggers apagados: vacía las 13 tablas (hijos primero), las lápidas, las filas apartadas y el registro de cambios; lleva `LastPushedSeq` al último `Seq` (`sqlite_sequence`), pone `CloudJoined`, el `clinicId` y `CloudDownloadPending`, y quita los cursores de bajada y `PullColumns:*`. También borra la marca de agua de la configuración. La bajada la hace la sincronización siguiente, con el candado normal: si se corta, la siguiente sigue donde iba.
  - **`DbMeta.CloudDownloadPending`:** mientras está puesta, la bajada no omite las filas con el `OriginDeviceId` de este equipo. Hace falta para recuperar una PC con la misma configuración (el mismo `DeviceId` que subió todo) y para «Reemplazar con la nube» en la misma PC. Se quita cuando la bajada termina.
  - **«Reemplazar con la nube»** es el mismo camino sin exigir base intacta. Además cambia el `DbInstanceId`. Se ofrece en el aviso de identidad, también cuando la base es de otra nube; la interfaz pide teclear CONFIRMAR. Los dos respaldan antes en la carpeta de respaldos (`…-antes-unirse-a-la-nube.db`, `…-antes-reemplazar-con-la-nube.db`); si el respaldo falla, no se borra nada.
  - `App.xaml.cs` ya no lee `IsFirstRun` para sembrar.
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
- **Hecho en v1.26.0 (2026-10-11).** Decisiones al programarlo:
  - **Protocolo 3.** `ClientProtocol` pasa a 3 (el 2 con la cerca). La «Sincronización completa» escribe `protocol = 3` y deja `minClientProtocol` en 2 (`MinCompatibleProtocol`): las PC con v1.25 o antes siguen sincronizando en la vida normal. Reemplazar la nube sube `minClientProtocol` a 3, porque un equipo sin la cerca escribiría en la nube restaurada.
  - **La cerca** (`SyncSchema.GenerationFenceSql`) usa `IS ?`, no `= ?`: una nube sin `generation` y un equipo sin generación (null) coinciden. Va en el upsert (dentro del `WHERE NOT EXISTS` que ya tenía), en la lápida (ahora `INSERT … SELECT … WHERE`) y en su `DELETE`. Cada lote de la subida y cada página de la bajada fuera de la foto inicial terminan con `SELECT` de la generación (`EnsureSameGeneration`); si cambió, el equipo se detiene (`GENERATION_CHANGED`) sin avanzar cursores. La bajada incremental ya la lee en la foto inicial.
  - **`generationPrevious`.** Una base sin generación guardada (la primera vez con esta versión) adopta la de la nube solo si la nube nunca se reemplazó (no existe `generationPrevious`). Si existe, no puede saber si lo suyo es de antes del reemplazo y hace lo mismo que con otra generación. Sin esta clave, un equipo que se actualizó después de un reemplazo subiría lo viejo.
  - **Al restaurar** con la nube configurada, `DataImportService` pone `DbMeta.RestoredWithCloud` y la sincronización se detiene (`RESTORE_PENDING`) hasta elegir en *Respaldos y datos*. Las dos salidas: «Reemplazar la nube con este respaldo» (antes «Revisar qué se perdería», con CONFIRMAR) y «Desconectar esta PC de la nube» (borra URL, token, `clinicId` y generación; no se permite a medio reemplazo).
  - **Orden del reemplazo** (`TursoSyncService.Generations.cs`): `DbMeta.ReplacingCloud` → la generación nueva con `generationPrevious` y `minClientProtocol = 3` → **después** se lee la nube y se guarda lo que se perdería (así nada de los demás equipos cae entre la lectura y el borrado) → `DELETE` de las 13 tablas y las lápidas con la cerca → subida completa → `generationReady` con la cerca → cursores de bajada en la marca de agua (no vuelve a bajar lo suyo) → reporte de 8.7. El plan decía guardar lo perdido antes de escribir la generación; se invirtió por esa ventana. La vista previa lee la nube una vez más, antes de confirmar.
  - **Lo que se perdería**: filas de la nube que aquí no están o son más nuevas, y lápidas de la nube de filas que aquí siguen («borrados que se deshacen»). Van a `backups/nube-antes-de-reemplazar-<fecha>.db` (tabla `SyncSavedChanges`) y a la tabla local del mismo nombre, para «Volver a aplicar». Si se corta, `DbMeta.ReplaceLostFile` evita guardarlo dos veces; si `generationReady` ya es la nueva, solo se hace la limpieza local.
  - **Los demás equipos** (`CheckGenerationAsync`): guardan lo anotado después del cursor y lo apartado al subir (cada fila en el formato de la nube, con su protocolo y la revisión del contrato) y, si el reemplazo no ha terminado, esperan (`GENERATION_PENDING`). Cuando termina: respaldan (`…-antes-de-bajar-la-nube-reemplazada.db`), se vacían como «Reemplazar con la nube» de 8.8 y bajan todo en la misma sincronización. «Volver a aplicar» aplica cada fila sin importar la marca y luego la marca como editada aquí (marca nueva, se anota y sube); los borrados se rehacen como borrados locales.
  - **Unirse** (8.8) se niega mientras `generation` ≠ `generationReady`.
- **Pruebas** (`TursoSyncEndToEndTests.Generations.cs`): restaurar detiene la sincronización; la vista previa cuenta lo de la tablet y lo de la PC; la nube queda igual al respaldo con el reporte limpio y los conteos del archivo iguales a los mostrados; la tablet guarda lo que no subió, baja todo una sola vez y lo vuelve a aplicar; un lote que llega con la generación vieja no escribe nada y el equipo espera; un reemplazo cortado a la mitad se termina sin que la tablet ni un equipo nuevo bajen la nube incompleta; desconectar deja la nube como estaba; adopción de la generación con y sin `generationPrevious`. Vectores de la cerca en `contracts/golden/lww.json`.
- **Cambia:** una restauración (`DataImportService`) con la nube activa pregunta:
  - **"Reemplazar la nube con este respaldo":**
    1. Antes de borrar, baja de la nube las filas que no están en el respaldo o son más nuevas, muestra los conteos por tabla ("se perderán 12 notas y 30 ventas de la tablet") y las guarda en un archivo `.db`.
    2. Guarda "reemplazo en curso" en `DbMeta`. Si TPVC se cierra, lo continúa al abrir.
    3. Escribe la generación nueva.
    4. `DELETE` remoto de las tablas sincronizadas y de las lápidas. `FolioCounters` no se toca (8.16).
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
- **Hecho en v1.27.0 (2026-10-11).** Decisiones al programarlo:
  - **El registro de la PC** (`TursoSyncService.Devices.cs`) va al final de la primera sincronización de cada sesión, y solo si esta base ya tiene `clinicId` (nube verificada o unida): reclama `primaryDeviceId` y se registra con el siguiente código `P` en una transacción. Una nube sin identidad no recibe nada. El código queda también en `turso_config.json` (`DeviceCode`) para 8.16.
  - **El código** se calcula en la misma instrucción: `'P' || (ifnull(max(CAST(substr(DeviceCode, 2) AS INTEGER)), 0) + 1)`, con índice único en `DeviceCode`. Quitar a un equipo sin fila (una PC anterior a v1.27) le inserta una ya quitada con su código, así que tampoco se reutiliza.
  - **«Esta PC reemplaza a la principal»** también quita la marca de quitada a esta PC (si la tenía) y no se ofrece en la principal. La principal anterior queda con el motivo `reemplazada`; al abrir, ve en *Equipos vinculados* que la quitaron y no vuelve a tomar la clave.
  - **Identificador clonado.** Además de DPAPI fallido, «cambia la máquina» es el `MachineGuid` de Windows (no cambia al renombrar la computadora), guardado como SHA-256 en `MachineFingerprint`; una configuración sin huella la adopta. El nonce se revisa solo después de que este proceso escribió el suyo (el primer latido de cada arranque trae el del arranque anterior). Quien lo detecta le regresa su nonce a la otra instalación (así solo una cambia), toma un `DeviceId` nuevo y **baja todo una vez** (`DbMeta.RedownloadAll`): mientras compartían el `DeviceId`, cada una omitía las filas de la otra. El aviso pide «Esta PC reemplaza a la principal» si esta era la principal.
  - **El latido** sale de un solo lugar (`HeartbeatStatementsAsync`): al empezar la sesión, al final de cada subida que mandó algo (con `LastPushUtc`), en la revisión de consultas cada 30 s y al leer la lista. Una sincronización sin cambios no escribe nada. `PendingCount` cuenta lo anotado después del cursor más lo apartado al subir; `OldestPendingUtc` es la marca más vieja de esas filas o lápidas.
  - **La consulta entre equipos** ya no corre su propio DDL: llama a `EnsureRemoteSchemaAsync`, que ahora tiene un candado. El celular, los quitados y un Android sin fila de `DeviceRegistry` no aparecen en «Enviar a…».
  - **Lista "Equipos vinculados"** en *Nube y dispositivos*: se lee al abrir la sección y con *Actualizar* (una petición con el latido). No muestra a los quitados; *Quitar* no se ofrece para esta PC ni para la principal.
- **Pruebas** (`TursoSyncEndToEndTests.Devices.cs`, 8 casos): compatibilidad por `HandoffProtocol` (Kotlin 0.3.0 con 1 sí; sin fila, quitado o con 0 no; una PC v1.19 por `AppVersion`); la primera PC es la principal con P1, otra recibe P2 y la reemplaza; los quitados no aparecen ni en la lista ni en «Enviar a…» y los códigos no se reutilizan; una nube de v1.26 recibe las columnas y el latido de v1.19 sigue funcionando; cada subida escribe `LastPushUtc` y lo pendiente, y una sincronización sin cambios no; dos instalaciones con el mismo `DeviceId` se detectan y la que lo nota baja todo una vez; un token que no se descifra o otra computadora dan un `DeviceId` nuevo; la limpieza de 6 h. Captura 20h en `UiSmokeTests`.
- **Cambia:**
  - Tabla `DeviceRegistry` (§2.3), escrita solo por la PC. Sus filas nunca se borran: revocar pone `RevokedAtUtc` y `RevokedReason`. El siguiente código de cada letra es el mayor registrado + 1, así que nunca se reutiliza.
  - Columnas nuevas en `RegisteredDevices` (§2.3), con `ALTER` explícito. Todas aceptan nulo, así que el latido de una PC v1.19 (upsert con columnas explícitas, `ConsultationHandoffService.cs:157-160`) sigue igual. Cada subida termina escribiendo `LastPushUtc`, `PendingCount` y `OldestPendingUtc` con la hora del servidor, en la misma instrucción, y el latido los repite.
  - **PC principal:** reclama `_tpvc_meta('primaryDeviceId')` con `INSERT … ON CONFLICT DO NOTHING` y se registra en `DeviceRegistry` con el código `P1`, que solo usará sin conexión (8.16); otra PC recibe `P2`… "Esta PC reemplaza a la principal", para una PC nueva que recuperó la nube, toma la clave y revoca a la anterior.
  - **Identificador clonado.** El `DeviceId` vive en `turso_config.json` (`Models/TursoConfig.cs:92`). Si se copia `%LocalAppData%\TPVC` a otra máquina, DPAPI falla (hoy da un token vacío en silencio) y el médico vuelve a pegar el token con el mismo `DeviceId`: las dos PC omitirían las filas de la otra. Se genera un `DeviceId` nuevo cuando falla DPAPI o cambia la máquina, y cada arranque escribe un `InstanceNonce`. Si el latido encuentra un nonce ajeno reciente con su `DeviceId`, avisa y genera uno nuevo.
  - `ToDevice`: compatible si `HandoffProtocol >= 1`; si está vacío, la regla de `AppVersion`. El rol sale de `DeviceRegistry` (de `RegisteredDevices` para las PC). Los revocados se excluyen.
  - Lista "Equipos vinculados": nombre, rol, código, "En línea / visto hace…", cambios pendientes y *Quitar*. `DevicePermissions` gana `CanViewClinicalRecords`, `CanShareDocuments`, `CanScheduleAppointments` y `CanRegisterStockEntries` (§4.6).
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
- **Hecho en v1.28.0 (2026-10-11).** Decisiones al programarlo:
  - **Dos carpetas sin WPF**: `Services/Documents` (lo que se imprime o comparte: `PrescriptionHtmlBuilder`, `LabOrderHtmlBuilder` con `SplitInColumns`, las piezas del membrete en `LetterheadHtml` y sus medidas en `LetterheadLayout`, que también usa la impresión con WPF; `TicketText` y `TicketHtml`) y `Services/Rules` (`CashCutRules`, `StockAlertRules`, `SuggestedPurchaseRules`, `VitalSignsRules`, `PatientFormRules`, `HandoffPayloadCodec`). La prueba de arquitectura prohíbe ahí WPF, ViewModels, la base y `DateTime.Now`/`Today`.
  - **Salidas idénticas**: el HTML de la receta y de la solicitud de laboratorio se comparó byte por byte con el de antes de moverlo (cuatro hojas, con y sin membrete y en los dos papeles).
  - **El ticket**: hoy la PC lo imprime como un FlowDocument con una tabla, no como texto. `TicketText` arma los mismos renglones en 42 columnas (Cant 4, Descripción 17 partida por palabras, P.U. y Total 9) y la impresión toma de ahí la cabecera, los datos, los encabezados, los importes, los totales y el pie, así que lo impreso y el texto ya no pueden diferir. Los importes usan es-MX explícito (antes, la cultura de Windows; en México da lo mismo). El efectivo recibido y el cambio no van, como en el impreso.
  - **Reglas que vivían en consultas a la base** (corte de caja, alertas, compra sugerida): la consulta sigue en `InventoryService` y las cuentas pasan a la regla. La compra sugerida desempata con el orden de es-MX explícito (antes, la cultura de Windows).
  - **Reloj**: `Models/ClinicClock` (el `TimeProvider` del consultorio) en la edad, `IsUpcoming`, `DaysRemaining`/el estado del lote, el día de la agenda y el resumen (métricas y alertas), más los métodos `…On(today)` para los vectores. `ClinicClock.Use` fija otro reloj solo en el flujo de la prueba (`AsyncLocal`), porque las pruebas corren en paralelo.
  - **Vectores** (`GoldenVectorTests.Documents.cs`): `cash-cut`, `alerts`, `suggested-purchase`, `prescription-sheet`, `prescription-html-fields` y `ticket-text`. Lo que depende de "hoy" se genera con un instante que es 12/10 en UTC y 11/10 en la Ciudad de México. Puntos medios: `Math.Round` de decimal al par (6.565 → 6.56, 0.195 → 0.20), el IVA del costo hacia arriba (0.145 → 0.15), el formato F2/N2 de decimal hacia arriba (0.125 → 0.13) y el de double según su valor binario (36.25 → 36.3, 9.125 → 9.13). Desempate con acentos: Abatelenguas, Ácido fólico, Agua inyectable.
- **Pruebas** (`DocumentsAndRulesTests.cs`): arquitectura de las dos carpetas; el ticket cabe en 42 columnas y su HTML dice lo mismo; `ClinicClock` fija "hoy" y lo restaura; avisos de signos y del IMC; la validación del formulario en su orden. `ConsultationHandoffTests` y `handoff-payload.golden.json` sin cambios.
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
- **Hecho en v1.29.0 (2026-10-11).** Decisiones al programarlo:
  - **Por hash y en trozos.** `ClinicAssets` lleva una fila por imagen (llave: su SHA-256), que nunca cambia; los bytes van en `ClinicAssetChunks` en trozos de 512 KiB, el mismo patrón que tendrán los resultados de laboratorio (8.12b): un escudo de 3 MB en una sola petición pasaría de los 30 s de espera con una subida lenta. La fila de `ClinicAssets` se escribe al final, así que su existencia dice que la imagen está completa.
  - **La imagen viaja con el perfil.** Elegir o quitar una imagen adelanta la fecha del perfil (antes era "de este equipo"). La PC con el perfil más nuevo sube primero lo que falte y luego el perfil con los hashes; al final borra las imágenes que ya no se nombran (solo las completas: una subida a medias de otra PC no se toca).
  - **Una PC nueva no quita las imágenes.** `CrestImageEditedHere` / `HeaderImageEditedHere` (solo en `clinic_profile.json`): si la imagen no se eligió ni se quitó en esta PC, su perfil más nuevo conserva el hash de la nube y la baja. Sin esto, una PC recién instalada que llena su asistente le quitaría el escudo a la tablet.
  - **`NULL` contra `''`.** `NULL` = ninguna PC con v1.29.0 escribió el hash (una nube de antes): la PC que tiene la imagen la sube y la anota con `UPDATE … WHERE … IS NULL`. `''` = sin imagen.
  - **Bajar solo si cambia el hash**, comparando en cada sincronización la fila del perfil que ya se lee. Se revisan el tamaño y el SHA-256; una imagen dañada no se usa ni se reintenta en esa sesión (cada intento costaría todos sus trozos).
  - El hash local sale del archivo al abrir TPVC (un perfil de antes lo calcula) y al elegir la imagen. Contrato revisión 6.
- **Pruebas** (`TursoSyncEndToEndTests.ClinicAssets.cs`): cambiar el escudo sube una sola vez (otro dato del perfil no la vuelve a subir), otro equipo la baja y después no lee `ClinicAssets`, otro escudo reemplaza al anterior en la nube y quitarlo también viaja; una PC que se une recibe las imágenes y su perfil nuevo no las quita; una nube de v1.28 recibe las columnas y la PC publica su imagen, y el upsert de v1.28 no toca los hashes; una imagen de 1.2 MB viaja en 3 trozos y una dañada no se usa.
- **Antes:** [T] V18, con `scripts/verify-turso-hrana.ps1 -Only V18`. **Hecha el 2026-10-11**: Turso responde el base64 estándar sin relleno (también el vacío), acepta los argumentos con y sin relleno y guarda los bytes exactos; un argumento que no es base64 hace fallar toda la petición con HTTP 400. La PC ya lee las dos formas: no hay que cambiar nada.
- **Cambia:**
  - Tabla `ClinicAssets` (tipo, hash, tipo MIME, contenido, `UpdatedAtUtc`). La PC la escribe solo cuando cambia una imagen.
  - `ClinicProfiles` gana `CrestImageHash` y `HeaderImageHash`.
  - Los equipos bajan una imagen solo cuando cambia su hash.
- **Prueba:**
  - Cambiar el escudo sube una sola vez.
  - Las bajadas normales no leen `ClinicAssets`.
  - Una PC que se une recibe las imágenes.
- **Riesgo:** bajo.

#### 8.12b · Resultados de laboratorio en la nube (agregado el 2026-10-11)
- **Hecho en v1.30.0 (2026-10-11).** Decisiones al programarlo:
  - **Sin `LabOrders`.** La orden ya vive en `PatientConsultations.LabOrderJson` (v1.25.0); `LabResults` se liga a la consulta con `PatientConsultationSyncId` opcional, que queda en nulo si se borra la consulta. Tampoco `ThumbSha256`: la lista no muestra miniaturas, y una columna se agrega después sin romper nada.
  - **Una fila, un archivo.** Varias fotos de un mismo resultado se juntan en un PDF con una página por foto (`JpegPdfWriter`, .NET puro: cada JPEG va tal cual con `/DCTDecode`), así Android solo abre PDF o JPEG. Las fotos se vuelven a guardar a 2000 px y calidad 80, derechas según el EXIF y sin metadatos (también sin la ubicación del GPS); el PDF va tal cual hasta 10 MB.
  - **Cada PC archiva todo**, no solo la principal (2 GB caben en cualquier disco), y el archivo es inmutable: quitar un resultado no borra su archivo. La PC sabe qué subir sin preguntarle a la nube (`LabFileStates.UploadedTo`), así que una sincronización sin resultados nuevos no toca `LabBlobs`.
  - **Fuera del candado de la sincronización.** La vuelta de los archivos corre después de cada sincronización buena, en segundo plano y con su propio candado: archivar 2 GB al unirse no detiene la sincronización.
  - **El tope sigue sobre lo que hay** (V20). Nadie sube lo que no cabe; la PC principal desaloja una vez al día si pasa de 1.8 GB, o al subir algo que no cabe, pero nunca algo más nuevo que lo que sube (si lo que sube es lo más viejo, se queda fuera). Solo desaloja lo que tiene verificado en su archivo **y** en el respaldo externo que se ve en ese momento (una USB desconectada no cuenta). El orden es por la fecha del resultado, con el índice `IX_LabResults_ContentSha256` de la nube. Lo que la nube desalojó a propósito no se vuelve a subir.
  - **Huérfanos en dos tiempos:** `OrphanSinceUtc` cuando ningún resultado lo nombra y se borra 30 días después (con la fecha de la creación se borraría de inmediato lo que un «Reemplazar la nube» deja sin nombrar).
  - **Freno de bytes distinto por plataforma:** en la PC, 2.5 GB de bajada y 500 MB de subida al día (alcanza para archivar los 2 GB el día que se une); en Android, 300 y 150 MB. Cuenta lo que viaja (el base64). Abrir un resultado a pedido no se frena.
  - **El espacio de toda la base** se mide con `(page_count − freelist_count) × page_size`, que en V20 dio lo mismo que `dbstat` sin recorrer tablas. Arriba de 4 GB no se sube ningún archivo.
- **Antes:** la orden de laboratorio ya existe desde v1.25.0 (`PatientConsultations.LabOrderJson`, contrato revisión 4). Se necesitan 8.9 (Reemplazar la nube no toca el contenido), 8.10 (solo la PC crea tablas) y el patrón de 8.12 (binario por hash, bajado bajo pedido). [T] V17, V19–V23 con `scripts/verify-turso-blobs.ps1`. **Hechas el 2026-10-11** (`TURSO_SYNC_ARCHITECTURE.md` §6):
  - No hay tope de petición hasta 32 MB, y cada trozo escribe 2 filas.
  - **Borrar sí libera cuota (V20):** `dbstat` baja en cuanto se borra y las páginas libres se reúsan. El tope de 2 GB queda como estaba.
  - Turso no comprime la bajada (+33%).
  - Un trozo de 512 KiB tarda menos de 1 s por el Wi-Fi de casa.
  - El `batch` con un BLOB es atómico.
  - V22 en el Wi-Fi del consultorio (el mismo día): igual que en casa, 0.6 s para subir un trozo y 0.4 s para bajarlo. Falta LTE; no detiene nada.
- **Cambia:** lo de `docs/EVALUACION_RESULTADOS_LABORATORIO.md` §6, elegido por el médico el 2026-10-10 («Turso + copia en la laptop»):
  - Tabla sincronizada `LabResults` (solo metadatos) y tablas solo de la nube `LabBlobs`/`LabBlobChunks`, en trozos de 512 KiB.
  - La laptop guarda una copia completa en `%LocalAppData%\TPVC\laboratorio\`, fuera de `consultorio.db` y de sus respaldos diarios.
  - Tope de 2 GB en la nube, compresión de las fotos al capturar (JPEG ~2000 px) y freno de bytes por día.
- **Prueba:** subir, cortar y continuar; bajar al abrir con su SHA-256; la laptop archiva todo; el tope deja de subir sin respaldo externo.
- **Riesgo:** medio (el almacenamiento de Turso, 5 GB en el plan gratuito, bloquea toda la base al pasarse).

#### 8.13 · Kit de conformidad de lectura
- **Hecho en v1.31.0 (2026-10-11).** Decisiones al programarlo:
  - **Grabaciones de quien lee.** El motor de la PC hace de tablet: una PC sube unos datos de ejemplo con `SyncId` fijos (`10000000-0000-4000-8000-…`) y se graba lo que hace el equipo que lee. Seis escenarios: primera descarga, incremental, empates en la llave, cambio de generación, `BLOCKED` y 401 (`TursoSyncEndToEndTests.Hrana.cs`). La sonda del esquema y el registro en `DeviceRegistry` que aparecen son de la PC; las notas de cada archivo lo dicen.
  - **Marcadores sin número para la hora** (`<time>`): dos marcas pueden coincidir o no según la corrida, y numerarlas haría cambiar el archivo. Los GUID al azar sí llevan número (`<id:N>`), porque nunca coinciden. Cada fila de los datos de ejemplo se guarda con su propia marca, y los `SyncId` crecen en el orden de subida: así el orden de las páginas es el mismo aunque dos filas empaten en `SyncedAtUtc`.
  - **`statements.json` en vez de SQL suelto.** Cada instrucción de lectura con sus argumentos y cuándo se usa, sacada de las constantes de la PC (`HandshakeSql`, `PageQuery`, `HeartbeatSql`, `RegistrationQueries`, `AssetMetaSql`…). La prueba las corre en el simulador, que rechaza un `SCAN` de una tabla sincronizada.
  - **401 desde ya.** El mapa de tokens del simulador (`AcceptedTokens`) se adelantó de 8.14 para grabar el 401. Desde V3 (2026-10-11) el cuerpo es el de Turso.
  - **El servidor HTTP es `HttpListener`** en `127.0.0.1` (no Kestrel ni ASP.NET), con `GET /health` y `GET /__stop`, y antes de escuchar siembra una nube con los datos de demostración y su «Sincronización completa». Funciona sin permisos de administrador en Windows y en Linux. Las variables están en `docs/FAKE_HRANA_SPEC.md`.
- **Cambia:**
  - `contracts/hrana/*.jsonl`: intercambios grabados por `FakeHranaServer`, con los valores generados reemplazados por marcadores. Primera sincronización, incremental, descarga completa por tabla, empates en la llave, cambio de generación, `BLOCKED` y 401.
  - El SQL de cada instrucción que Kotlin tiene que repetir va **literal** en `contracts/hrana/` (la revisión de consultas se agrega en 8.24). SQLite usa un índice parcial solo si el `WHERE` repite su condición, así que una consulta escrita de otra forma recorrería la tabla.
  - `docs/FAKE_HRANA_SPEC.md`: qué copia el simulador de Turso. Enteros como texto (si no, HTTP 400), un resultado por instrucción, sin transacción, un escritor a la vez, reloj inyectable, lecturas cobradas, tope de respuesta, `BLOCKED` y la revisión de planes.
  - `FakeHranaServer` expuesto como servidor HTTP de pruebas (`TPVC_FAKE_TURSO_HOST=1 dotnet test --filter TestCategory=FakeTursoHost`), para que la CI de Kotlin y la PC sincronicen contra el mismo servidor en un solo job. Vive en el proyecto de pruebas, escucha solo en 127.0.0.1 y nunca entra a `TPVC.exe`.
- **Prueba:** todo corre en la CI de C# en Linux; los planes de las consultas literales no tienen SCAN.
- **Riesgo:** ninguno.

#### 8.14 · Vincular la tablet y el celular con la URL y el token
- **Hecho en la PC en v1.32.0 (2026-10-11).** Decisiones al programarlo:
  - **El QR lleva el token de la PC.** Es el mismo `full-access` (Turso no distingue tokens ni anula uno solo); otro token creado aparte no daría más protección. Formato `tpvc-link:1:` + base64url del JSON `{"u","t","n"}` (`Services/Pairing/DeviceLinkCode.cs`, vector `contracts/golden/link-qr.json`). Corrección de errores L y 300 px en pantalla: un token real (~350 caracteres) da ≤ 100 módulos, 3 px cada uno. Se cierra solo a los 2 minutos y la imagen se descarta; nunca va al portapapeles ni al registro.
  - **Solo con la nube verificada** (`clinicId` en esta base, después de la «Sincronización completa»), como «Quitar» y «Esta PC reemplaza a la principal».
  - **Aprobar es escribir la fila de `DeviceRegistry`** con el rol que elige el médico (el equipo pide uno en su latido, pero puede quedar del otro) y el siguiente código de esa letra, en una transacción que lee el código asignado. Si otra PC ya lo aprobó, no cambia nada.
  - **Rechazar deja una fila ya revocada con código X** (`X1`, `X2`…, motivo `rechazado`): los códigos T y C son de los folios, y así un rechazo no los gasta. El equipo lo lee y no baja nada. Rechazar no corta el acceso a la nube: el mensaje pide anular los tokens si no se reconoce el equipo (8.15).
  - **Un 401 se traduce** a "Turso rechazó el token (HTTP 401): se anuló o no es de esta base…" y no borra nada. **V3 (2026-10-11)** mostró que a un token que no es un JWT (mal copiado o incompleto) Turso responde HTTP 400 `JWT error: InvalidToken`: TPVC lo trata igual que un 401 (mismo `ErrorCode`, "parece incompleto o mal copiado"), y Kotlin debe hacer lo mismo. El cuerpo real del 401 está en el simulador y en `contracts/hrana/unauthorized.jsonl`.
  - **QRCoder 1.8.0** (MIT): sin dependencias de dibujo en .NET 10; genera el PNG directo.
- **Decisión del médico (2026-10-11):** en lugar del QR por la red local (§4.4, descartada), cada equipo recibe la URL y un token como la PC. Se cae el servidor temporal en la PC, el Firewall y el protocolo cifrado que habría que mantener igual en C# y en Kotlin. Turso no deja anular un solo token: un equipo perdido se corta rotando todos y pegando el nuevo en los que quedan (8.15).
- **Antes:** [T] V3 (§4.2), hecha el 2026-10-11.
- **Cambia:**
  - En la tablet y el celular: pegar la URL y el token (copiados del panel de Turso) o escanear el QR de la PC. Se guardan envueltos con el Android Keystore, y el equipo se niega si no tiene bloqueo de pantalla seguro.
  - En la PC, *Equipos vinculados › Mostrar QR para la tablet o el celular*: la URL y el token de la PC (QRCoder, licencia MIT), con el aviso de no fotografiarlo ni compartirlo. Se cierra solo a los 2 minutos.
  - **Aprobar en la PC:** el equipo nuevo escribe su latido y espera. La PC muestra "Equipo nuevo: «Galaxy Tab S9». ¿Vincular como Tablet?", con los últimos 6 caracteres de su `DeviceId` en las dos pantallas. Al aceptar escribe su fila en `DeviceRegistry` (código `T1`, `C1`…). Hasta entonces el equipo no baja datos. Rechazar lo deja fuera de la lista.
  - Otra PC con Windows se une como hoy (URL, token y «Unirse a esta nube») y recibe su código (`P2`…).
  - `TursoClient` traduce el 401 a "Turso rechazó el token: pegue el token nuevo" (el simulador ya lo responde desde 8.13, con `AcceptedTokens`).
  - Vector `link-qr`: el texto del QR (prefijo y versión, URL, token y nombre del consultorio) para que Kotlin lo lea igual.
  - Capítulo "Tablet y celular" en el manual.
- **Prueba:**
  - La aprobación con un equipo simulado (su latido en el servidor simulado): sin aprobar no baja nada; rechazar no escribe `DeviceRegistry`.
  - Un 401 se traduce y no borra nada.
  - El QR se cierra solo y no queda en el portapapeles ni en el registro.
  - [W] Capturas en los 4 temas.
- **Riesgo:** bajo.

#### 8.15 · Quitar, equipo perdido y rotación
- **Hecho en la PC en v1.33.0 (2026-10-11).** Decisiones al programarlo:
  - **«Perdí este equipo»** va junto a *Quitar*, en los mismos equipos (no en esta PC, ni en la principal, ni en uno que espera la aprobación). Revoca con el motivo `perdido` y muestra los pasos para anular los tokens en *Equipos vinculados*, con *Abrir el panel de Turso*. V4 (2026-10-11): anular es instantáneo y el token nuevo sirve en cuanto se crea, así que el texto dice que los equipos dejan de sincronizar hasta recibir el nuevo, sin perder nada.
  - **Una PC marcada como perdida no borra nada.** Borrarse sin subir es para la tablet y el celular; en una PC, una revocación por error dejaría al consultorio sin datos. El corte de verdad es anular los tokens; *Equipos vinculados* de esa PC dice que se marcó como perdida.
  - **El 401 en la PC** (y el 400 de un token mal copiado, V3): no borra ni aparta nada; la sincronización automática deja de intentar mientras el token sea el mismo (`TursoSyncPauseReason.TokenRejected`) y el aviso pide pegar otro. Al guardar otro token la pausa se quita sola. Del token solo se guarda una huella (SHA-256 truncado, `TursoSettings.RejectedTokenHash`/`WorkingTokenHash`).
  - **La rotación se anota en la nube**, no en cada PC: `_tpvc_meta.tokensRotatedAtUtc`, con la hora de la nube, lo escribe la primera sincronización con otro token después de que Turso rechazó el que funcionaba (o después de «Perdí este equipo»). Un token que nunca funcionó no cuenta. Para no anotar dos veces la misma rotación, un equipo no mueve una marca posterior a su última sincronización buena con el token anulado. La lista marca "Pegue el token nuevo…" en los equipos con `LastSeenUtc` anterior a la marca. Un equipo que recibió el token nuevo antes que la PC aparece así hasta su siguiente latido.
- **Antes:** [T] V4, hecha el 2026-10-11 (anular es instantáneo; el token nuevo sirve al crearlo). V11 se resolvió el 2026-10-11: sin bitácora en el plan gratuito (§4.7).
- **Cambia:**
  - *Quitar* con aviso de cambios pendientes; el equipo sube lo pendiente antes de borrarse, o lo exporta.
  - "Perdí este equipo": revocar en `DeviceRegistry` y "Cortar acceso ahora" guiado (§4.7): rotar en el panel de Turso (anula todos los tokens), pegar el nuevo en la PC y en los demás equipos. Estado "Pegue el token nuevo" en la lista.
  - Reglas ante un 401 (§4.7).
- **Prueba:**
  - Después de rotar en el simulador, los tokens viejos reciben 401; con los nuevos pegados, la PC sigue sincronizando.
  - Volver a vincular con el mismo `DeviceId` conserva las filas pendientes.
  - Un 401 con cambios pendientes nunca borra la base.
  - *Quitar* con cambios pendientes los sube antes de borrar.
  - Un equipo que lee su fila revocada con motivo "perdido" se borra sin subir.
- **Riesgo:** medio-bajo.

**Corte: primera versión del celular.** Con 8.2–8.15 puede salir el celular (K0–K4, §7.5): solo baja datos (escribe su latido), se vincula pegando la URL y el token (o escaneando el QR de la PC) y comparte la receta y el ticket. Prueba en equipos reales Hrana, la vinculación y el PDF antes de escribir la tablet. Lo que escribe el celular (agendar y entradas de mercancía, K4b) llega después de 8.23. Cuando 8.20 suba `protocol` a 3, el celular se actualiza por la prueba interna de Play.

### Etapa 3 · Datos correctos con varios equipos que escriben

#### 8.16 · Folios consecutivos desde Turso, con letra sin conexión (A3)
- **Hecho en la PC en v1.34.0 (2026-10-11).** Decisiones al programarlo:
  - **El piso va en la misma instrucción.** En lugar de "si el número ya existe en la base local, sube el contador y vuelve a tomar", la instrucción lleva el mayor número local de la serie: `VALUES (?, ? + 1) ON CONFLICT(Series) DO UPDATE SET LastNumber = max(LastNumber, ?) + 1 RETURNING LastNumber`. Es una sola petición y sigue siendo atómica. Va literal en `contracts/hrana/statements.json` (`folio.take`), con `folio.raise` para la siembra y los folios tecleados.
  - **Solo la PC principal siembra** (el año y el día, al empezar cada sesión y en cada «Sincronización completa»). Lo que otra PC numeró sola le llega por la sincronización, y el piso de cada equipo cubre lo que todavía no llega. Así las grabaciones de lectura no dependen del año.
  - **Cuándo se toma el número.** Expediente: al guardar, si el folio probable que mostró el formulario no se cambió (un folio tecleado se respeta y, si es mayor, sube el contador sin esperar). Si quedó otro número, el aviso de guardado lo dice. Ticket: dentro de la transacción de la venta, después de revisar las existencias (2 s como máximo).
  - **Sin conexión** cuenta también la cuota agotada, el freno de consumo y el token rechazado: no se pregunta. `folios.json` va junto a `turso_config.json`. Una PC con la nube verificada y sin código de equipo no crea el folio y dice por qué.
  - **CNS en la PC sin letra.** La PC solo sabe que no hay conexión cuando habla con Turso, y CNS no es única ni se regulariza: sale de la hora, ahora por `FolioFactory.Consultation`. Kotlin sí le pone la letra sin conexión.
  - **Las importaciones numeran con la base local**; el piso y la siembra lo cubren.
  - **Índices:** `IX_Patients_RecordNumber` e `IX_Sales_Folio` pasan a normales. La migración vuelve a crear un índice que dejó de ser único (nunca al revés: con filas repetidas fallaría al abrir), y la huella del modelo cuenta si un índice es único. El aviso de folios repetidos está en *Nube y dispositivos* y se revisa en cada sincronización. Contrato revisión 8.
  - **Una respuesta que se pierde** después de que el contador subió deja un hueco en el consecutivo (el folio sale con letra). La regularización (8.17) no rellena huecos.
- **Antes:** [T] V16, hecha el 2026-10-11: la instrucción atómica sirve (40 peticiones a la vez, 40 números distintos sin huecos).
- **Cambia:**
  - Tabla `FolioCounters` (§2.3), creada por la PC. La PC siembra cada serie con el mayor número que ya existe al activar la nube y después de cada "Sincronización completa" (`… DO UPDATE SET LastNumber = max(LastNumber, excluded.LastNumber)`). Un número tecleado a mano mayor que el contador lo sube igual.
  - Una `FolioFactory(código, reloj, contador, existentes, último guardado)` pura reemplaza todos los puntos que generan folios: `InventoryService` 626, 1181 y 1334; `ActiveConsultation.cs:39`; `ConsultationViewModel.cs:365` y 462; `DataImportService.cs:817` y 888; `PatientFormViewModel.cs:593`.
  - **Con conexión**, EXP y TKT toman el siguiente número con una sola instrucción: `INSERT INTO FolioCounters (Series, LastNumber) VALUES (?, 1) ON CONFLICT(Series) DO UPDATE SET LastNumber = LastNumber + 1 RETURNING LastNumber` (si V16 falla, la alternativa de §4.2). La instrucción va literal en `contracts/hrana/`. Si el número ya existe en la base local (una PC vieja que numera sola), sube el contador al máximo y vuelve a tomar.
  - El número se toma al guardar, con 2 s de espera. El formulario del paciente sigue mostrando el siguiente probable al abrirlo (`PatientFormViewModel.cs:589`).
  - **Sin conexión** (sin red, sin respuesta en 2 s o con `BLOCKED`), el folio lleva el código del equipo de `DeviceRegistry`: `EXP-2026-T1-001`, `TKT-20261009-T1-0001`; la PC usa `P1`. El número de cada serie con letra es el mayor entre el último guardado **fuera de la base** (`folios.json` en `%LocalAppData%\TPVC`; en Android, un archivo que no se borra al volver a bajar todo) y el máximo local. Así restaurar un respaldo no repite uno ya impreso. Una PC sin código en `DeviceRegistry` no crea folios sin conexión: "Vincule esta PC desde la principal o márquela como la que la reemplaza".
  - Con la nube apagada, los folios salen como en v1.19.
  - CNS sigue saliendo de la hora (`ActiveConsultation.cs:39`); solo sin conexión lleva la letra (`CNS-20261009-T1-103015`), y nunca se regulariza.
  - `PatientRecord.RecordNumber` y `Sale.Folio` pasan de índice único a índice normal (`AppDbContext.cs:215-217`, 253-255), y el `-R` deja de aplicarse. Las filas viejas con `-R` se quedan. Un folio repetido (filas viejas o tecleado a mano) aparece en un aviso de la PC.
- **Prueba:**
  - Dos equipos con conexión toman números seguidos sin repetir.
  - Dos equipos sin conexión (`P1` y `T1`) crean pacientes, tickets y notas sin choques.
  - Los lectores ignoran los folios con letra.
  - Restaurar un respaldo viejo no repite el siguiente EXP, con conexión ni sin ella.
  - Dos filas con el mismo folio sincronizan sin renombrarse y aparecen en el aviso.
  - Con la nube apagada, nada cambia.
  - Vector `folios.json`.
- **Riesgo:** bajo-medio.

#### 8.17 · Regularización diaria de folios en la PC (A3)
- **Hecho en la PC en v1.35.0 (2026-10-11).** Decisiones al programarlo:
  - **Cuándo corre.** `Services/Folios/FolioRegularizationService` (sin WPF, con `TimeProvider`) se dispara después de cada sincronización buena (`SyncCompleted`), solo en la PC principal (`IsPrimaryOnCurrentCloud`) y con `AutoRun`, que enciende la ventana principal. El día hecho va en `DbMeta.FolioRegularizedDay`; un día sin abrir la laptop se recupera la siguiente vez.
  - **La nube al día.** Nada pendiente aquí (`GetPendingChangesAsync`) y ningún otro equipo no revocado con `PendingCount > 0` (lee la lista de equipos, como máximo cada 15 minutos mientras alguno tenga pendientes). Los que llevan 7 días o más sin verse con cambios pendientes aparecen en *Equipos vinculados*.
  - **La pantalla** (`IFolioRegularizationHost`, `ViewModels/FolioRegularizationHost.cs`): no corre con una consulta en curso, productos en la caja o el alta o edición de un paciente abierta, y se detiene entre un registro y otro si empieza una. Un paciente que `CanReplacePatientInstance` no deja cambiar (abierto en su ficha o en la consulta) se deja para la siguiente vez (resultado `Partial`: el día no se marca).
  - **El número** sale del mismo `folio.take` del paso 8.16, con el piso local de la serie normal. Antes de escribir el registro se guarda `FolioMap:<folio con letra>` en `DbMeta`: si se corta a la mitad, o si el registro vuelve con su folio con letra (otro equipo lo editó sin conexión antes de bajar el regularizado), recibe el mismo número y no se toma otro. Sin conexión se detiene.
  - **El guardado** cambia el folio, pone `OriginalFolio` (si no lo tenía) y, en un expediente, las citas que copian su folio, en una sola escritura; sube por el ciclo normal. Después avisa a las pantallas (`NotifyAllChanged` en el hilo de la interfaz), que recargan como después de una bajada.
  - **Búsqueda y reimpresión:** `RankPatients`, `GetPatientsAsync`, `IsRecordNumberTakenAsync`, `GetSaleByFolioAsync` y el historial de tickets comparan también `OriginalFolio`. El ticket reimpreso (texto, HTML y la impresora) lleva "FOLIO SIN CONEXIÓN: …" debajo del folio; la ficha lo muestra junto al folio y el detalle del ticket debajo. Contrato revisión 9.
  - **Falta:** el vector `rank-patients` es del paso 8.26; `folios.json` ya lleva los casos de los folios con letra.
- **Cambia:**
  - Columnas `Patients.OriginalFolio` y `Sales.OriginalFolio`. Aceptan nulo y viajan por el ciclo normal.
  - `FolioRegularizationService`, sin WPF y con `TimeProvider`, **solo en la PC principal**. Corre como máximo una vez al día (fecha en `DbMeta`), y solo si la nube está al día, ningún equipo no revocado reporta cambios pendientes (`PendingCount`; si uno lleva más de 7 días con cambios pendientes sin verse, la PC lo avisa en *Equipos vinculados*), no hay consulta en curso (`ConsultationService.HasActiveConsultation`) ni venta en curso (`PosViewModel.HasCartItems`). Tampoco corre con el alta o edición de un paciente abierta (`PatientForm.IsPatientModalOpen`) y salta al paciente que `CanReplaceSelectedPatientInstance` no deja reemplazar. Al terminar recarga la lista de pacientes como después de una bajada (`ForceFreshPatientIds`): antes del 8.21 una copia vieja en memoria devolvía el folio con letra; desde v1.38.0 ya no lo regresa, y la recarga y el salto del paciente abierto quedan para que la pantalla muestre el folio nuevo. Si un registro ya regularizado vuelve con su folio con letra, recibe el mismo número que ya tenía asignado, no uno nuevo. Si la laptop estuvo cerrada ese día, corre la siguiente vez que esté abierta y libre. Se detiene entre un registro y otro si empieza una consulta o una venta, y sigue en la siguiente oportunidad.
  - Busca los pacientes y tickets cuyo folio lleva código de equipo, les da el siguiente número de su serie (el año del EXP, el día del TKT) desde `FolioCounters` y guarda el folio con letra en `OriginalFolio`. Las citas del paciente que copian su folio (`PatientAppointment.PatientRecordNumber`, `InventoryService.cs:1490`) se actualizan en la misma operación. Todo sube por el ciclo normal; un número tomado y no guardado queda como hueco.
  - **La búsqueda encuentra los dos folios:** `RankPatients` (`PatientsHomeViewModel.cs:60`), `GetPatientsAsync` (`InventoryService.cs:1120`), `IsRecordNumberTakenAsync` (:1169) y la búsqueda de tickets (`GetSaleByFolioAsync`, :651, desde `PosViewModel.Records.cs:173`) comparan también `OriginalFolio`.
  - **Reimpresiones:** el ticket (`PrintingService.cs:120`, `TicketText`) muestra el folio nuevo y debajo "Folio sin conexión: TKT-20261009-T1-0001". La ficha del paciente y el detalle del ticket, igual.
- **Prueba:**
  - Un paciente y un ticket con letra quedan con el siguiente número y su `OriginalFolio`; buscar por cualquiera de los dos los encuentra.
  - No corre con una consulta abierta, con productos en la caja ni con cambios pendientes en otro equipo, y corre una sola vez al día.
  - Un día sin abrir la laptop se recupera al siguiente.
  - Una segunda PC nunca regulariza.
  - La reimpresión lleva los dos folios.
  - Los vectores `rank-patients` (8.26) y `folios.json` llevan casos con `OriginalFolio`, para la búsqueda en Kotlin.
- **Riesgo:** bajo-medio.

#### 8.18 · SyncIds y renglones con identidad en las hojas del paciente (A4)
- **Hecho en la PC en v1.36.0 (2026-10-12).** Decisiones al programarlo:
  - **Dónde vive.** `Services/Rules/PatientSheetRows` (sin WPF ni base): el `RowId`, con qué renglón de la cuenta se cargó cada insumo y servicio, y la traducción al recibir una consulta. `PatientRecord` pone los `RowId` al leer y al escribir cada hoja (los tres renglones implementan `IPatientSheetRow`). Contrato revisión 10: nota de formato en las tres columnas y en `PatientConsultations.PrescriptionsJson`, regla «Hojas del paciente» y el vector `contracts/golden/synced-json-columns.json`.
  - **`RowId` de un renglón viejo:** `uuid5(tpvc, "sheetrow:<SyncId del paciente>:<columna>:<posición desde 0>:<nombre tal cual>")`; la hoja es el nombre de su columna (`ActiveSuppliesJson`…) y el nombre (medicamento, insumo o servicio) va sin normalizar, así Kotlin no depende de cómo quita los acentos. Lo recibe también un renglón con el GUID vacío o con uno repetido en la misma hoja. Uno nuevo lo toma al azar. Leer no escribe nada: se guarda con la siguiente escritura de la hoja (cualquier guardado del expediente). Una PC anterior que reescribe la hoja tira los `RowId`, y al leerla cada renglón recibe el de su nueva posición.
  - **La receta lleva su `RowId` a la nota.** La nota de la consulta usa las mismas instancias que la hoja, así que `PrescriptionsJson` lo trae desde esta versión (no se usa; el contrato lo dice). La receta en curso del borrador (`ClonePrescription`) no lo lleva. La receta anterior (`ClonePrescriptionKeepingIdentity`) sí conserva el `RowId` y los campos desconocidos: en la memoria de la consulta, en su borrador y en la copia que viaja a otro equipo (`PreviousPrescriptions`), así al descartar la consulta, o al terminarla sin receta, vuelven los mismos renglones, también después de reabrir el programa o en el equipo que la recibió. Es un campo aditivo: sin `RowId` no se escribe, `PayloadVersion` sigue en 1 y las opciones del borrador ignoran lo que no conocen, así que una PC v1.35 y un borrador anterior siguen igual (el borrador de v1.18 se vuelve a escribir igual). `consultation-draft.golden.json` y `handoff-payload.golden.json` solo agregan ese `RowId`. Al recibir, la receta en curso toma las instancias del expediente, que ya traen su `RowId` por la sincronización.
  - **Insumos, en este orden:** `libre` primero (un cargo libre nunca lleva SyncId, así que da lo mismo que el plan lo ponga segundo); un `ChargedLineId` negativo, que es el cargo libre con que se cobra, tal cual, también en un producto (el que no existía al recibir la consulta): sale de un nombre, es igual en todos los equipos y Kotlin también lo lee así; el `ChargedLineSyncId`: el producto activo con ese SyncId o, si no hay, el de este equipo con ese SyncId aunque esté inactivo (`CatalogStore.ProductRefs` lleva el Id y el SyncId de todos los productos), así un producto desactivado a media consulta se sigue descontando como antes; el Id viejo positivo solo si ese producto activo tiene el mismo nombre normalizado (su nombre o "Nombre (Presentación)", lo que escribe la hoja); por nombre con la búsqueda de siempre (`FindCatalogProduct`, que pasó a `PatientSheetRows.FindProductByName`); si no, cargo libre. **Agregado, solo en la PC y con la cuenta abierta como prueba** (`AccountLegacySupplyLine`): un renglón viejo (sin clase ni SyncId) cuyo `ChargedLineId` tiene en la cuenta un renglón con su mismo nombre lo cargó este equipo con la versión anterior, y es ese renglón aunque el producto se haya renombrado o desactivado; si su Id no está en la cuenta (trae el de la PC que lo cargó), es el único renglón de producto de la cuenta con su mismo nombre (con ninguno o con varios se busca como arriba). Para servicios, lo mismo con `-ServiceId` y el nombre del renglón de la cuenta, y se descuentan los insumos por defecto de ese servicio aunque esté inactivo (`CatalogStore.AllServices`). El Id de otro equipo nunca coincide con un renglón de esta cuenta con el mismo nombre.
  - **Servicios** (el plan no les da clase): sin `ServiceSyncId` y con `ServiceId` 0 es un cargo libre, aunque después aparezca un servicio con ese nombre; si no, el servicio activo con ese SyncId, el Id viejo con el mismo nombre normalizado y el servicio activo con el mismo nombre normalizado.
  - **Quién escribe** (las líneas que citaba el plan ya se habían movido): `ConsultationAccount.ChargeSupply` anota la clase, el SyncId y el Id local al cargar; `ClinicalServicesSheetViewModel.SelectAndAddServiceModalSuggestionAsync` pone `ServiceSyncId` al crear el renglón (también con un nombre tecleado que coincide con el catálogo). + / − en la hoja de insumos: un renglón con el formato nuevo queda con el Id local de lo que movió y no cambia su clase ni su SyncId (un producto que aquí se cobra como cargo libre sigue siendo ese producto en los demás equipos); uno viejo recibe la clase y el SyncId, en su siguiente guardado y nunca con un relleno, y conserva su `ChargedLineId`, el de la PC que lo cargó. + en un cargo libre carga el mismo renglón libre que ya tiene el renglón, aunque se llame distinto.
  - **Recibir una consulta** (`ApplyReceivedConsultation`, ya tomada): `TranslateReceivedSheets` cambia a los Id de este equipo el `ChargedLineId` y el `ServiceId` de los renglones con el formato nuevo (un insumo con `ChargedLineKind`, un servicio con `ServiceSyncId`) y el folio de la consulta, en memoria; se guardan con la siguiente escritura del expediente. La clase y los SyncId no cambian. Un renglón viejo conserva los Id de la PC anterior que lo escribió, y ella los usa tal cual si la consulta le regresa; la excepción es un insumo viejo que aquí es un cargo libre que la cuenta recibida sí tiene (el producto no existe en este equipo): queda con ese Id negativo, que esa PC también entiende. Un insumo cargado como producto toma el Id del renglón de la cuenta recibida con su mismo SyncId, ya traducido: si ese producto no existe aquí, la cuenta lo cobra como el cargo libre del nombre de su renglón, que puede no ser el de la hoja (lo abrió un insumo por defecto), y el renglón queda apuntando a ese cargo. En lo demás, sin esa traducción (al reabrir el programa) los lectores encuentran lo mismo. La copia que viaja no lleva las hojas: llegan con el expediente.
  - **`[JsonExtensionData]`** en los tres renglones (`ExtensionData`): lo que la PC no conoce, también objetos, arreglos y nulos, se escribe después de sus campos. Una hoja que no se puede leer sigue quedando vacía, como antes.
  - **Pruebas:** `PatientSheetRowsTests` (dos `Fase6Clinic`; en la tablet los productos empiezan en 5001 y los servicios en 7001, así ningún Id coincide) y `GoldenVectorTests.Sheets.cs`. Las PC anteriores siguen leyendo `ChargedLineId` y `ServiceId` del equipo que escribió: entre dos PC viejas el error de A4 se queda.
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
- **Hecho en la PC en v1.37.0 (2026-10-12).** Decisiones al programarlo:
  - **El Kardex agrupa por operación.** El libro guarda un renglón por lote, pero el Kardex y los «Movimientos recientes» del resumen muestran uno por operación, como antes: `Services/Rules/KardexRows` une los renglones de una misma operación (mismo producto, tipo, `Date` exacta, `SaleItemId`, motivo y responsable, todos con `Delta` distinto de 0) en uno con lo que movió en total y la existencia de antes y de después (`InventoryService.GetKardexAsync`). Se juntan todas las partes con la misma hora, aunque sus Id locales queden intercalados (un ticket de dos renglones, filas que llegan de otro equipo en el orden de la nube), y la lectura sigue hasta tener completa la última hora que se muestra. `MovementsToday` cuenta operaciones con la misma llave (una fila anterior es su propia operación) y `ConsumedInConsultationToday` sigue sumando piezas. Así lo que ve el médico no cambia: el Kardex nunca mostró el lote, y contar renglones inflaría "N movimientos hoy" cada vez que una venta toca dos lotes. Dos renglones del mismo producto en un ticket siguen siendo dos (`SaleItemId`), y las filas anteriores nunca se unen.
  - **Qué verifica `StockLedger.VerifyAsync`.** Lo guardado contra el libro en cada lote (su cantidad contra sus movimientos) y en cada producto (su existencia contra todos sus movimientos); las piezas sin lote son la diferencia de los dos. Las bases de hoy ya traen diferencias que este paso no puede reconstruir (FEFO anotaba solo el primer lote, devoluciones a un lote adivinado, `SaveBatchAsync`), y con ellas el requisito del 8.20 ("cero diferencias varios días") nunca se cumpliría. Por eso se guarda una **foto** de esas diferencias (`DbMeta.StockLedgerBaseline`, JSON por `SyncId` de lote y de producto) una sola vez: al migrar (`EnsureSchema`, también al restaurar un respaldo anterior) y de nuevo al terminar de bajar una nube completa (unirse, reemplazar con la nube, otra generación), y la verificación reporta solo lo que cambió desde entonces. Un lote cerrado debe quedar en 0. Al arrancar, la bitácora dice "Libro de existencias: sin diferencias nuevas (…)" o lo avisa con la lista; no hay pantalla. Los saldos iniciales del 8.20 absorben la foto.
  - **Cerrar un lote.** `DeleteBatchAsync` escribe `Delta = −libro(lote)` con su lote, como dice el plan, y le resta al producto lo que el lote mostraba, con el mismo piso de antes (`Math.Min` con la existencia del producto, que no queda en negativo: un lote puede mostrar más que el producto si la ficha bajó la existencia). Lo que falte para que el libro del producto baje lo mismo (ese piso, o un lote de antes que muestra otra cantidad que su libro) va en un segundo movimiento sin lote con la misma hora y motivo: así el producto y su libro siempre bajan lo mismo y el Kardex muestra un solo renglón, como antes. Las dos partes llevan `SyncId` deterministas, `uuid5(tpvc, "close:" + SyncId del lote)` y `"close-rest:" + SyncId del lote`, como las devoluciones: dos equipos que cierran el mismo lote sin conexión escriben las mismas filas, y una parte que ya llegó de otro equipo no se vuelve a escribir ni a descontar. El lote queda con `Quantity` 0, `IsDepleted` 1 y `ClosedAtUtc` con la hora corregida por el desfase; cerrar otra vez no hace nada. Ya no hay borrado ni lápida de lotes.
  - **Lotes que se ven.** Una sola regla, `ProductBatch.IsAvailableExpression` (ni agotado ni cerrado; en memoria `IsAvailable`), para FEFO, Lotes, el resumen, las alertas y las consultas de `InventoryService`: un lote cerrado al que una PC anterior le devolvió una cancelación (no conoce `ClosedAtUtc`: le suma piezas y lo marca sin agotar) sigue oculto. `contracts/golden/alerts.json` lleva ese caso.
  - **Cancelar.** La regla sale de `Sale.LedgerVersion`. Nueva: una devolución por cada salida del ticket (por `SaleItemId`), a su lote y con su `SaleItemId`; si a un renglón le faltan salidas se niega con "Este equipo todavía no tiene todos los movimientos del ticket … Sincronice primero y vuelva a intentarlo." Vieja: una por producto con lo que suman sus renglones, sin `SaleItemId`, al lote de la primera salida (por `Date` y `SyncId`, igual en todos los equipos) cuyo motivo empieza con `Venta POS {folio} - ` o con su `OriginalFolio` (el formato existe desde v1.5.0, cuando se empezaron a guardar los tickets; con " - " para que `TKT-…-0001` no encuentre `TKT-…-00010`); sin ninguna, sin lote, en lugar de adivinar. Las devoluciones que ya existen se saltan una por una (no se vuelven a sumar) y el ticket queda cancelado. Una devolución a un lote agotado lo vuelve a abrir para FEFO; a uno cerrado le suma sus piezas y lo deja cerrado.
  - **Diálogo del producto.** `SaveProductAsync(producto, originalStock)`: el diálogo manda la existencia que mostró al abrirse. Si el campo no cambió, la existencia no se toca; si se tecleó otra, queda la tecleada y el ajuste se calcula contra lo guardado (no contra lo que mostró), sin lote. Sin `originalStock` (las demás llamadas) se compara con lo guardado, como antes.
  - **Otros caminos.** `RegisterMovementAsync`: una entrada con lote ahora también suma al lote (antes solo al producto) y una cantidad de 0 o menos se rechaza (un movimiento nuevo nunca lleva `Delta` 0); una salida con lote elegido empieza por él y sigue por FEFO. «Ingresar pedido» (v1.22) y recibir un lote ya dejaban un movimiento con su lote: ahora llevan `Delta`. El costo (v1.21) y laboratorio no tocan existencias. El alta de un producto con existencia negativa también deja su movimiento. Los productos demo dejan un movimiento por lote y otro sin lote para el resto: una base nueva no tiene diferencias.
  - **Actualizar con columnas nuevas.** Al bajar completa una tabla porque esta versión conoce columnas nuevas (paso 8.7), una fila que aquí es más nueva recibe solo esas columnas, pero ya no las que en la nube vienen en nulo (lo que dejó el `ALTER`), ni una referencia nueva en nulo: un lote cerrado antes de la primera sincronización conserva su `ClosedAtUtc`. Una fila que se apartó en esa bajada porque le faltaba su principal (la salida cuya venta llegó con otro renglón) se aplica al reintentar o al volver a leerla aunque tenga la misma marca que la de aquí: es la misma versión, y antes se perdía con su `Delta` y su `SaleItemId`.
  - **Contrato revisión 11:** `InventoryMovements.Delta` y `SaleItemSyncId` (referencia opcional que se vacía al borrar el renglón; la fila que llega antes que su renglón se aparta, paso 8.4), `MovementType.SaldoInicial = 8`, `Sales.LedgerVersion` y `ProductBatches.ClosedAtUtc` (`utc`), con su `note` y la regla «Existencias». Por la referencia nueva, `InventoryMovements` sube después de `Sales` y `SaleItems`. `contracts/golden/uuid5.json` lleva las dos reglas de las devoluciones y las dos partes del cierre de un lote con las funciones que usa la PC (`StockLedger.ReturnSyncId`, `LegacyReturnSyncId`, `CloseSyncId` y `CloseRestSyncId`). `stock-ledger.json` y `sale-fefo.json` quedan para el 8.20, como dice el plan: la regla del libro cambia con los conteos.
  - **Lo que queda para el 8.20.** Mientras las existencias viajen como valor, dos equipos que venden el mismo producto siguen perdiendo un descuento en lo guardado (el libro sí tiene las dos ventas, y la verificación lo reporta), y cerrar un lote mientras otro equipo vende de él sin conexión depende de qué fila del lote es más nueva: si es la de la venta, el lote vuelve a abrirse con lo que le quedaba (el movimiento sí sigue en su lote). Con las columnas derivadas del 8.20 una venta ya no cambia la fila del lote.
  - **Pruebas:** `StockLedgerTests` (secuencias al azar de recepciones, entradas, consumos, bajas con lote, ventas de varios renglones, cancelaciones, cierres, el diálogo con una venta a la mitad y pedidos, con lo guardado igual al libro después de cada una; ventas viejas por folio y folio con letra; cerrar un lote de antes) y `TursoSyncEndToEndTests.StockLedger.cs` (dos equipos que cancelan el mismo ticket dejan una devolución por salida; las devoluciones que llegaron primero cuentan la cancelación como hecha; una venta incompleta no se cancela; una venta que llega después de cerrar su lote sigue en él y la verificación lo marca). `SaveBatchAsync` se quitó; la prueba de lápidas usa una nota de consulta.
- **Cambia:**
  - En `InventoryMovement`: `Delta` con signo (0 marca una fila vieja, que sigue la regla de `IsEntry`), `SaleItemId` (viaja como `SaleItemSyncId`) y `MovementType.SaldoInicial = 8`, que va al final porque el enum se guarda como entero. Las ventas nuevas llevan `Sale.LedgerVersion = 1`.
  - `DeductFromBatches` regresa una asignación por lote más el resto sin lote. `RegisterQuickConsultationUsageAsync`, `RegisterSaleAsync` y la salida de `RegisterMovementAsync` escriben un movimiento por lote y otro con `BatchId = null` para el resto.
  - `CancelSaleAsync` revierte cada movimiento original en su propio lote:
    - la regla nueva o la vieja se elige por `Sale.LedgerVersion`, no por si la búsqueda encuentra algo;
    - nueva: encuentra los movimientos por `SaleItemId`; `SyncId` = UUIDv5(`"return:" + SyncId del movimiento original`);
    - vieja: por producto más "Venta POS {folio}" (o su `OriginalFolio`, 8.17) en `Reason`; `SyncId` = UUIDv5(`"return:" + SyncId de la venta + ":" + SyncId del producto`);
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
  - **Barrera.** Se niega a convertir si `RegisteredDevices` muestra otra `WindowsDesktop` vista en los últimos 30 días con una versión anterior: "Actualice la otra PC" o "Esa PC ya no existe". Corre solo en la PC principal, después de una sincronización completa y sin cambios pendientes en ningún equipo (`PendingCount`). Una PC v1.19 no lee `protocol`, y dos PC calcularían saldos iniciales sobre fotos distintas. Si una PC vieja aparece después: alerta y, si hace falta, rotación del grupo (§4.7).
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
- **Hecho en la PC en v1.38.0 (2026-10-14).** Decisiones al programarlo:
  - **Los campos** (`Services/PatientFields`): todas las columnas de `PatientRecord` en el modelo de EF menos `Id`, `SyncId`, `UpdatedAtUtc` y `CreatedAt`. Salen del modelo, así una columna nueva entra sola; `PatientFieldsTests` lo compara con el modelo y con las columnas que suben. `IsActive` es la baja lógica del expediente, no una lápida: es un campo más. Las tres hojas se comparan por su texto canónico, después de `Sync*Json` (`PatientRecord.CanonicalSheetJson`: leída como siempre, con el `RowId` determinista, y escrita con el formato de la PC): una hoja escrita antes del 8.18 o por otro equipo que dice lo mismo no cuenta como cambiada. Una hoja que cambió se escribe completa; la combinación por renglón es del 8.22.
  - **La foto vive con la instancia** (`PatientRecord.StoredSnapshot`, que no es columna ni viaja en ningún JSON). La toman `GetPatientsAsync`, `GetPatientSummariesAsync` y `GetPatientByIdAsync`, y se renueva con cada guardado. Cada carga de la lista toma la de todos los pacientes, así que es un arreglo por el lugar de cada campo (sin diccionario), y la lista de campos se arma al arrancar fuera del hilo de la interfaz (`PatientFields.Warm`), con las opciones de la app para que EF use el modelo que ya armó. Regla: es lo que la instancia tenía la última vez que estuvo al día con la base, campo por campo; lo editado es lo que difiere de ella, y la foto nunca se mueve sin la instancia. Así quienes ya llamaban `SavePatientAsync(paciente)` (las hojas, la ficha, el cierre de la consulta) pasan la foto tomada al cargar sin armarla. La forma del plan también existe: `SavePatientAsync(editado, base)` deja al día a `editado` y regresa la fila fresca (otra instancia, con su foto); la usan el formulario y descartar la consulta.
  - **Después de guardar** (`PatientFields.Refresh`, antes del aviso a las pantallas), un campo que la instancia no editó toma el valor de la fila: la próxima cita, la última visita, el folio regularizado, lo que bajó de otro equipo. La foto queda con la fila y la instancia con su marca. **Una hoja que no se editó y que en la base es otra no se cambia en memoria**: sus renglones son los objetos que ven las hojas, la cuenta de la consulta y "Se surte aquí", y cambiarlos a media consulta los soltaría. Su foto se queda como estaba (no se escribe) y la instancia no toma la marca de la fila. Con SQLite el guardado termina antes de que se suelte la copia, así que la recarga que lo sigue la deja; la recarga anota el Id de cada copia vieja que se quedó y, cuando ya nadie trabaja sobre ninguna (se cierra la consulta, por cualquier camino, o el formulario: `ReloadIfReleasedAsync`, que revisa cada Id y no el paciente seleccionado), se recarga y la ficha toma la hoja de la base. La copia del paciente en consulta se queda en la lista aunque esté seleccionado otro (un alta a media consulta), y "Volver a la consulta" la retoma: otra instancia partiría la consulta en dos. Si se edita esa hoja vieja antes, se escribe completa, como antes.
  - **Sin foto** (una instancia hecha en memoria, como en varias pruebas o la pantalla sin base) se escribe la fila completa, como antes, y desde ahí la instancia tiene su foto. Un expediente nuevo se da de alta igual que siempre.
  - **Sin cambios no se escribe nada**: la marca de la fila no se mueve y no hay nada que subir; la instancia sí se pone al día.
  - **El folio** se revisa contra los demás expedientes y se anota en la nube (`NoteManualAsync`) solo si se escribe; si el que quedaría guardado está vacío, recibe uno. Antes una copia con un folio repetido (los que llegan de dos equipos desde el 8.16) no podía guardar ni una hoja, y las hojas callaban el error.
  - **El formulario** guarda con la foto tomada al abrirlo, pero con lo que escribiría si nadie toca nada en los campos que solo normaliza (el teléfono con dígitos, "No determinado", "Normal", el nombre armado de sus partes): lo editado es lo que el médico cambió. Lo que solo normaliza se sigue escribiendo si en la base está igual que en la foto; si otro equipo lo cambió mientras el formulario estaba abierto, se queda el suyo (`PatientFields.ToWrite`).
  - **La nota de la consulta.** `AddConsultationNoteAsync` y la consulta en curso usan la misma regla para el resumen (`PatientRecord.ApplyConsultationSummary`), y la instancia marca esos campos como guardados (`PatientFields.MarkStored`). La receta corregida desde el historial (`UpdateConsultationPrescriptionAsync`) regresa los renglones que guardó, con sus `RowId`, y la ficha pone esos mismos. Sin eso, descartar la consulta regresaría el resumen solo en memoria: la instancia volvería a ser igual a su foto vieja y guardar no vería nada que escribir.
  - **Descartar la consulta** escribe lo que regresa (el resumen y la receta anterior) aunque la foto diga que no cambió (`StoredSnapshot.Without(...)`): el expediente queda exactamente como antes de empezarla, igual que antes del paso. Terminarla sin receta escribe la anterior solo si cambió.
  - **Los demás caminos, revisados.** La consulta recibida (`TranslateReceivedSheets`, la receta en curso de `ResumeConsultationOnRecord`) cambia la instancia en memoria: son ediciones y se escriben con el siguiente guardado, como decía el 8.18. El guardado automático, la consulta enviada y los resultados de laboratorio no escriben la fila. `SaveAppointmentAsync`, `DeletePatientAsync`, la regularización de folios, la importación del Excel y la bajada escriben la fila sin pasar por la instancia; ahora la copia abierta ya no los pisa. La regularización sigue saltando al paciente abierto, solo para que la pantalla no muestre el folio con letra.
  - **Contrato sin cambios** (revisión 11): la fila que sube es la misma, solo cambia qué columnas escribe la PC en su base. Los vectores no cambian. La tablet guarda igual desde K6 (alta y edición) y K7 (hojas y nota): `docs/PENDIENTES_ANDROID.md`.
  - **Lo que queda para el 8.22** (resuelto en v1.39.0, ver 8.22). La nube sigue con la última escritura por fila: una edición que la PC guarda antes de bajar la de otro equipo sube su fila completa y gana. Una hoja editada a la vez en dos equipos pierde los renglones de uno. Si un guardado de una hoja se cruza con el de la nota en el mismo instante, la copia puede mostrar el resumen de antes hasta la siguiente recarga (la base queda bien, y descartar la consulta lo escribe de todos modos).
  - **Pruebas:** `PatientFieldsTests` (la lista contra el modelo; cada campo se detecta solo; una hoja guardada desde la copia de la consulta conserva la cita que se agendó mientras tanto y la copia la muestra; lo que bajó con la consulta o el formulario abiertos sobrevive; el formulario escribe lo que se editó; la foto se mueve con la copia; sin cambios; folio regularizado; folio repetido; hoja que bajó; descartar con la nota guardada; sin foto y alta) y `TursoSyncEndToEndTests.PatientFields.cs` (la tablet cambia las alergias y agenda; la PC baja con su copia abierta y guarda una hoja: en la nube y en la tablet quedan las dos cosas).
- **Cambia:**
  - `SavePatientAsync(editado, base)` copia sobre la fila recién leída solo los campos donde editado ≠ base, y regresa la fila fresca para refrescar la copia del espacio de trabajo.
  - Quienes guardan pasan la foto tomada al cargar: el alta y edición, las hojas y el resumen de la nota.
- **Prueba:**
  - Guardar una hoja con una copia vieja conserva `NextAppointment*`.
  - `PacientesTests` y las pruebas de flujos pasan.
  - [W] Las capturas no cambian.
- **Riesgo:** alto, porque es guardar el paciente. Va solo.

#### 8.22 · Combinación por campo en pacientes (H1b)
- **Hecho en la PC en v1.39.0 (2026-10-14).** Decisiones al programarlo:
  - **Dónde vive.** Las reglas, puras, en `Services/Rules`: `PatientFieldStamps` (las marcas), `PatientMerge` (la combinación), `PatientSheetCanonical` (las hojas por renglón y su forma canónica), `CanonicalJson`, `PatientSummaryRules` (el resumen derivado) y `HandoffPayloadCodec.CheckArrival`. Lo que las une a EF: `Services/PatientFieldMerge` (el sello y aplicar el resultado) y `Services/PatientSummary` (recalcular). Contrato revisión 12, con los vectores `field-stamps`, `patient-merge`, `canonical-json`, `patient-summary` y `handoff-accept`.
  - **Las marcas.** `FieldStampsJson` es un objeto plano en forma canónica: `"<columna>"` (último cambio del campo; en una hoja, de su orden), `"<hoja>/<RowId>"` (renglón agregado o cambiado), `"<hoja>/<RowId>/quitado"`, `"#piso"` (abajo) y `"UpdatedAtUtc"`. Las pone `StampSyncMetadata` con la misma marca monótona que `UpdatedAtUtc`, en formato de 27 caracteres, solo en las columnas que EF va a escribir y cambiaron por su significado (con el 8.21, las editadas); en una hoja, solo los renglones cuyo texto canónico cambió (se comparan los textos, no las instancias: guardar `[Gasa 2, Venda 1]` sobre `[Gasa 1, Venda 1]` no toca la marca de la venda); el alta solo pone `"UpdatedAtUtc"` (lo que nadie ha editado desde el alta cuenta como la marca mínima, y nadie más lo tiene). Cubre sola `AddConsultationNoteAsync`, la regularización, la importación y la baja. Las derivadas y `FieldStampsJson` no son campos (`PatientFields.Metadata` la agrega: una copia nunca escribe sus marcas). Las llaves que no se conocen se conservan y se combinan por la marca mayor. Un texto dañado, que no es objeto o con una llave repetida cuenta como sin marcas (kotlinx se quedaría con la última llave y `System.Text.Json` lanza otra excepción que `JsonException`: así los dos leen lo mismo); la forma canónica de un objeto con llaves repetidas se queda con la última, como kotlinx.
  - **La llave `"UpdatedAtUtc"`, en lugar de "la marca máxima de sus campos".** La regla literal del plan ("si el `UpdatedAtUtc` de la fila es mayor que la marca máxima de sus campos") falla con el resultado de una combinación, cuya fila queda en `max(L, R) + 1 ms`: parecería una escritura sin marcas y le ganaría a una edición más nueva de otro equipo. Por eso las marcas dicen con qué `UpdatedAtUtc` se mantuvieron, y una fila es "sin marcas" si su `UpdatedAtUtc` es mayor (una PC v1.19–v1.38 sube la fila sin tocar la columna) o si no la tiene. Además, una marca nunca vale más que el `UpdatedAtUtc` de su fila: si la fila se reparó por estar en el futuro (8.7), sus campos cuentan con ella; la reparación local también reescribe las marcas en el futuro (`json_each` sobre `FieldStampsJson`).
  - **Escritura sin marcas y el piso (`"#piso"`).** Una fila sin marcas (una PC v1.19–v1.38, o la fila de antes de actualizar) escribió la fila completa en su `UpdatedAtUtc`: todo lo que tiene vale al menos eso. Esa hora no se puede perder cuando la fila recibe su primera marca, así que vive en la llave `"#piso"`: lo que no tiene marca propia vale al menos el piso. Cada versión se normaliza antes de combinar o comparar (`PatientFieldStamps.State`, vector `field-stamps.json` "state"): cada marca nunca mayor que su fila; el piso es el mayor entre `"#piso"` y, si es una escritura sin marcas, su `UpdatedAtUtc`; y las llaves conocidas (los campos, las tres hojas y sus renglones) que no rebasan el piso no se guardan. En la combinación, un campo, el orden de una hoja y cada renglón que una versión **tiene** valen al menos su piso, y un renglón que **no tiene** cuenta como quitado en el mayor entre su piso y su propia marca de quitado (el piso es un mínimo, no un tope: un renglón que quitó después de su piso sigue quitado en esa marca): es el LWW por fila de siempre, no una moneda al aire. El resultado lleva el piso mayor de los dos. Al guardar aquí sobre una fila sin marcas, su `UpdatedAtUtc` anterior queda como `"#piso"` y solo se marca lo editado; una fila completa marcada como modificada sin cambiar nada (`EntityState.Modified`: «Volver a aplicar», `RestampGenericAsync`) es una escritura de la fila completa y sube el piso a la marca nueva. Las llaves desconocidas no se tocan con el piso.
  - **Por qué el piso (revisión adversarial del 8.22, mismo v1.39.0).** La primera versión contaba la escritura sin marcas "en lo que difiere" con su `UpdatedAtUtc`, pero la perdía en cuanto la fila recibía su primera marca: (1) dos PC v1.38 que se actualizan una después de otra: la segunda, con su copia vieja sin marcas, le ganaba a la alergia que la primera escribió con v1.38 y revivía el renglón que había quitado, porque la fila de la primera ya tenía marcas (solo la del teléfono) y lo demás contaba como la marca mínima; (2) una PC sin conexión con v1.38 que edita, se actualiza y edita otro campo antes de sincronizar: su edición de v1.38 perdía contra la nube por el desempate ordinal. Con el piso las dos se conservan, y la primera sincronización después de actualizar no sube nada: dos filas sin marcas iguales dicen lo mismo (`TheFirstSyncAfterUpdatingFromV138_UploadsNothing…`). La misma revisión encontró que guardar una hoja marcaba todos sus renglones (se comparaban instancias de `JsonObject`), que una consulta nunca se aceptaba después de purgar un renglón quitado (se exigía la llave del renglón) y que una llave repetida rompía la lectura.
  - **Los quitados no viven en los arreglos.** Las PC v1.36–v1.38 y los lectores de las hojas (también esta app) toman cada elemento del arreglo como un renglón vivo, así que el arreglo solo lleva renglones vivos y la marca de quitado vive en `FieldStampsJson` (`AnOldFormatReader_SeesOnlyTheLiveRows`). Un renglón vive si su marca es mayor que la de quitado (iguales: quitado). La purga a los 90 días cuenta desde la marca más nueva del expediente, no desde el reloj: dos equipos purgan lo mismo y la purga sola nunca se vuelve una diferencia que rebota.
  - **El orden de una hoja.** La llave de la hoja es la marca de su orden y cambia solo cuando cambia la lista de RowId (agregar, quitar, reordenar). Gana el orden del lado con esa marca mayor y al final van, en su orden, los renglones vivos que solo tiene el otro. Con marcas iguales gana la lista de RowId **vivos** con el texto canónico mayor (comparar con los muertos también rompía la absorción). El contenido de un renglón, del lado con su marca mayor (iguales: el texto canónico mayor).
  - **Desempate y forma canónica.** Marcas iguales y valores distintos: el texto canónico mayor en orden ordinal (unidades UTF-16, no `Collator`: "Ácido" va después de "Agua"). La forma canónica: llaves ordenadas, sin espacios, escape mínimo, números sin ceros de más ni exponente (35.0 = 35), fechas de 27 caracteres, booleanos `true`/`false`; las hojas con **todos** sus campos conocidos (los que la PC omite cuando valen lo de siempre, también) y sin los Id locales `ChargedLineId` y `ServiceId`: Kotlin no los escribe y recibir una consulta los traduce, así que con ellos cada equipo vería un cambio que no existe y subiría sin fin. La edad con fecha de nacimiento se compara calculada al día en los dos lados.
  - **La copia que solo se bajó (al actualizar desde v1.38; solo la PC).** Una PC con v1.38 baja por LWW sin `FieldStampsJson` (no la conoce): si en la nube la fila ya tenía marcas, su copia se queda sin ellas y, al actualizar, la columna llega vacía. Esa copia no es una escritura de la fila completa: si en la primera bajada después de actualizar (la de la columna nueva) se combinara con su `UpdatedAtUtc` como piso, desharía lo que la nube ya tenía (una alergia marcada antes de esa versión en otro equipo) y subiría sin haber editado nada. Por eso, en esa bajada (`ApplyPageGenericAsync` con `FieldStampsJson` entre las columnas nuevas), una fila sin marcas **sin nada pendiente de subir** (ni anotada después del cursor de subida ni apartada al subir; sin cursor, todo cuenta como pendiente: `SyncChangeLog.ReadPendingPushAsync`) no se combina: sigue el LWW de fila y toma la de la nube completa, con sus marcas, si no es más vieja. Una fila sin marcas con una edición de v1.38 pendiente sí se combina con su piso (para eso es el piso). Después de esa bajada, una fila sin marcas es copia fiel de una escritura sin marcas de la nube (su piso es real) y se combina como siempre: si se tomara la de la nube, una subida posterior que no la incluye la borraría para siempre. Kotlin no lo necesita: empieza en la revisión 12 y nunca guarda una copia de v1.38.
  - **Cuándo sube.** Se compara por significado `{"stamps":…,"values":…}` (el estado normalizado y purgado, con su piso y sin la llave `"UpdatedAtUtc"`) contra la fila de la nube normalizada igual. Si difiere: `UpdatedAtUtc = max(L, R) + 1 ms`, la llave igual, y `MarkForUploadAsync` en la misma transacción que la página (`SaveRemoteChangesAsync` ganó ese parámetro); sube al final de la **misma** sincronización (una sola vez: lo que la nube rechace ahí se combina otra vez en la siguiente). Si no difiere, se queda la fila de la nube tal cual (su marca y su texto de `FieldStampsJson`); una hoja que dice lo mismo toma el texto de la nube, como antes con el LWW (así llegan los `RowId` que escribió otro equipo). «Volver a aplicar» (8.9) sigue escribiendo la fila tal cual y después la marca como fila completa (el piso sube). La nube sigue con LWW por fila.
  - **Converge.** La combinación es conmutativa e idempotente y absorbe; entre versiones con marcas (también con piso), lo que se sabe (valores, estado y qué renglones viven) es además asociativo y solo crece. El orden de los renglones no es asociativo (depende de qué versión ganó el orden), pero cuando todos saben lo mismo gana la lista de texto mayor y deja de cambiar. Pruebas de propiedades: 2 000 pares y tríos al azar (con y sin marcas, con piso), 300 intercalados al azar de ediciones y sincronizaciones entre 2 y 3 equipos, algunos con v1.38 que se actualizan a la mitad (convergen en ≤ 4 vueltas y después nadie vuelve a subir) y 100 con campos de cada equipo, que nunca pierden la última edición ni un renglón agregado; además, tres equipos de punta a punta con el servidor simulado.
  - **Resumen derivado.** `TableMap.DerivedColumns` (de `SyncSchema.DerivedProperties`, genérico para que el 8.20 agregue las existencias): `LastVisit` y `NextAppointment*`. Regla (`PatientSummaryRules`): la próxima cita es la primera que no está "Cancelada" ni "Atendida" desde hoy (el día del consultorio, `ClinicClock`), por fecha, hora y `SyncId`; la hora se lee con una regla propia (`H:mm`, `:ss` y AM/PM opcionales, lo demás 10:00) que Kotlin puede repetir, no con `DateTime.TryParse`. La última visita es la consulta más reciente; sin consultas no se calcula y se queda la que tenía (la del alta). Se recalcula con SQL directo después de cada `SaveChanges` que toca un expediente, una consulta o una cita (también al aplicar una página que bajó) y completo al abrir la base (`EnsureSchema`, así cuenta el día que pasó): sin marca ni registro (los triggers ya no vigilan las derivadas), el valor viaja como información con la siguiente subida de la fila. Al bajar, una derivada solo da el primer valor a una fila nueva. `SaveAppointmentAsync` ya no escribe el expediente: la copia abierta ya no se cambia por otra instancia después de agendar (cambió el archivo de avisos `notifications/agenda.txt`), y la ficha sigue mostrando la próxima cita de las citas.
  - **La copia abierta (8.21).** Una hoja editada ya no se escribe completa: `PatientSheetCanonical.Rebase` pone las ediciones de la copia contra su foto (agregar, cambiar, quitar) sobre la hoja de la base, en el orden de la copia y con los renglones que llegaron de otro equipo al final. Así el sello solo marca lo que hizo la copia, y un renglón de la tablet que bajó con la copia abierta no se vuelve "quitado". `PatientFields.Refresh`: una hoja editada que quedó distinta en la base se queda como está en memoria y su foto pasa a ser lo que la instancia tiene (si se quedara con la de la base, el siguiente guardado vería como quitados los renglones que la instancia no tiene); la instancia no toma la marca de la fila y `ReloadIfReleasedAsync` la recarga al soltarla.
  - **Consulta entre equipos.** La copia lleva `PatientFieldStamps` (el estado normalizado de `FieldStampsJson`, con su `"#piso"` y sin `"UpdatedAtUtc"`, como fecha ISO como el resto de la copia) y `ProgressNoteUpdatedAtUtc`, que no se escriben si faltan (el borrador de v1.18 y el golden del borrador no cambian; el de la copia enviada solo agrega esos campos). Quien recibe, ya sincronizado, revisa en este orden (`CheckArrival`): el expediente existe, su `UpdatedAtUtc` no es menor que el de la copia (se conserva), cada marca de la copia es menor o igual a la de aquí, que vale al menos el piso de aquí (en una escritura sin marcas, su `UpdatedAtUtc`); el par de un renglón quitado que aquí ya se purgaría (su marca de quitado antes de la marca más nueva de aquí − 90 días, y la del renglón si no la rebasa) no se exige: antes solo se saltaba la de quitado y la del renglón, ya purgada aquí, no dejaba aceptar nunca, la nota existe y con su versión. Si falta algo: "Intente de nuevo en unos segundos". Las marcas no se quedan en el borrador de quien la recibe.
  - **Aviso de otra PC.** `LinkedDevice.MergesAsWholeRow`: otra PC con Windows (o sin plataforma y sin rol de Android: v1.19–v1.26) vista en los últimos 30 días, sin quitar y con `ContractRevision` menor que 12 (o sin ella). *Equipos vinculados* dice en ella "Tiene una versión anterior de TPVC: lo que cambie en un expediente se combina como fila completa…". La lista ahora lee `ContractRevision`.
  - **Folios regularizados.** La prueba del 8.17 en que la laptop volvía a subir el folio con letra cambió: con marcas, la combinación conserva el folio regularizado y la edición de la laptop; solo una PC anterior (sin marcas) lo regresa, y al día siguiente recibe el mismo número.
  - **Lo que queda.** Entre una PC v1.39 y una anterior, lo de la anterior se combina como fila completa (el aviso lo dice; el 8.20 de todos modos exige actualizarla) y su subida más nueva puede tapar en la nube lo de un equipo con marcas hasta que este vuelva a combinar. Una subida de una PC anterior sobre una fila con marcas no toca `FieldStampsJson`: si su `UpdatedAtUtc` es mayor, la fila es una escritura sin marcas y su piso le gana a todo lo marcado antes, aunque esa PC no lo haya cambiado (es el LWW por fila de siempre). Una PC que se actualiza desde v1.38 y edita un expediente antes de su primera sincronización toma como piso el `UpdatedAtUtc` de la copia que bajó con v1.38 (el sello no sabe que solo la bajó): le gana a una edición con marcas de otro equipo anterior a esa copia que la copia no tenía, como lo habría hecho v1.38 al subir la fila completa. Un equipo que estuvo más de 90 días sin conexión con una edición de un renglón ya purgado lo revive. El resumen derivado en la nube es solo información y puede ir atrasado (el día cambia a medianoche; se recalcula al abrir y con cada guardado). `FieldStampsJson` agrega ~1–3 KB por expediente editado.
  - **Pruebas:** `PatientMergeTests` (por campo, marca mínima, desempate con acentos, escritura sin marcas, marca en el futuro, llaves desconocidas, mismo significado con otro texto, renglones en los dos lados, quitar contra editar en los dos sentidos, lector viejo, purga, el sello, solo los renglones que cambiaron, quitar o editar un renglón contra editar otro, llaves repetidas, la misma fila sin marcas, copia vieja sin marcas contra la fila recién marcada, primera escritura marcada, conmutativa, idempotente y absorbente, asociativa en lo que se sabe, intercalados al azar con PC v1.38 que se actualizan sin subidas repetidas y donde una edición con marcas solo la reemplaza una escritura igual o posterior, revisión de la copia y la copia con un quitado ya purgado aquí), `PatientSummaryTests` (orden clínico, hora, última visita, agendar sin escribir el expediente, la nota marca solo lo que cambia, guardar marca solo lo editado, el piso de una fila sin marcas y «Volver a aplicar»), `TursoSyncEndToEndTests.PatientMerge.cs` (alergias sin conexión y teléfono en los dos órdenes, un renglón en cada equipo, quitar contra editar, quitar o editar un renglón en la PC y otro en la tablet, PC v1.38, la primera sincronización después de actualizar no sube nada, dos PC v1.38 que se actualizan una después de otra, una edición sin conexión con v1.38 seguida de la actualización y otra edición, una copia bajada con v1.38 con la nube todavía en esa versión y con la nube ya en otra, llaves desconocidas, marcas en el futuro, próxima cita igual en los dos, tres equipos al azar, la copia abierta, el aviso de otra PC) y `ConsultationHandoffTests.AcceptingWithAnOlderLocalField_AsksToWait_UntilTheSendersChangesArrive`. Cada una falla sin su parte del cambio.
- **Cambia:**
  - Columna `Patients.FieldStampsJson` = {campo: marca}, mantenida por `StampSyncMetadata`. Cubre sola `AddConsultationNoteAsync` y `SaveAppointmentAsync`.
  - Al bajar, `PatientRecord` se salta el LWW por fila (`TursoSyncService.cs:823`) y usa `PatientMerge.Merge`, que es pura:
    - por campo gana la marca mayor; una marca que falta cuenta como mínima;
    - **escritura sin marcas:** si el `UpdatedAtUtc` de la fila es mayor que su llave `"UpdatedAtUtc"` o no la tiene (una PC v1.19–v1.38 o una escritura que no pasó por el sello), su `UpdatedAtUtc` es su piso: todo lo que tiene vale al menos eso, y lo que no tiene cuenta como quitado al menos con él (o con su propia marca de quitado, si es mayor). El piso se guarda en `"#piso"` cuando la fila recibe su primera marca. Equivale al LWW de hoy, en lugar de una moneda al aire;
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
  - Una escritura de v1.19 gana con su `UpdatedAtUtc`, también después de que la fila recibe su primera marca, y la primera sincronización después de actualizar no sube nada.
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
  - Sincronización rápida (≤ 3 s) antes de cobrar, con red. Sin red se cobra igual (respuesta 2): folio con letra (8.16) y existencias que pueden quedar en negativo con aviso (8.20). Lo mismo en la tablet.
  - Sube `contract`. **`contracts/sync/schema.json` queda congelado como base de la tablet.** A partir de aquí solo se admiten cambios aditivos.
- **Prueba:**
  - Pruebas de las reglas y de la detección después de bajar.
  - Se puede cobrar sin conexión, con folio con letra.
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
  - `contracts/scenarios/*.json`: pasos por estado (editar, sincronizar en el equipo A o B, desfase o salto de reloj, corte a media subida) con las filas esperadas en la nube y en cada equipo, por `SyncId`. Cubren los escenarios de varios equipos de 8.4, 8.5, 8.9, 8.16, 8.17, 8.19, 8.20 y 8.22. No dependen del texto del SQL, salvo las consultas literales de 8.13 y 8.24.
  - `contracts/hrana/*.jsonl` de escritura: subida, conflicto de empate, lápida contra edición, cerca de generación, el contador de folios, y la consulta entre equipos: enviar, aceptar, respuesta perdida y recuperar.
  - Vectores de la consulta entre equipos: copia de trabajo con reloj fijo y las fechas tal cual (la de `TestAssets/fase6` las cambia por marcadores), traducción (`FindCatalogRef`), `linked-device.json` y `handoff-accept.json`.
- **Prueba:** todo corre en la CI de C# en Linux.
- **Riesgo:** ninguno.

#### 8.26 · Vectores de reglas del negocio
- **Cambia:** `contracts/golden/` con la forma y las reglas de 8.11:
  - búsqueda: `text-search`, `rank-patients` (con `OriginalFolio`), `rank-products` (con el prefijo `3*`), `cie10-catalog` y `cie10-search`, `catalog-matching`;
  - seguridad e identidad: `allergy-check`, `patient-identity`, `date-mask`;
  - ficha y agenda: `chart-antecedents`, `chart-timeline`, `latest-vitals`, `vitals-evolution`, `agenda`;
  - consulta: `vitals`, `pediatric-dose`, `consultation-account`, `sheet-charges`, `checkout`.
- **Prueba:** cambiar una regla sin regenerar rompe la CI.
- **Riesgo:** bajo.

### Pasos opcionales

| Paso | Cuándo | Qué |
|---|---|---|
| 8.A Unir expedientes duplicados | Si aparecen duplicados | Solo en la PC. Se elige el expediente que queda y los campos, se reasignan consultas y citas, y el otro queda `IsActive = false` con `MergedIntoSyncId`. Cualquier equipo reasigna después de bajar las filas hijas que lleguen tarde, y lo registra para subir (8.5). Los números de expediente nunca se reutilizan. |
| 8.C Avisos con la pantalla apagada | Si el médico lo pide | Servicio de notificaciones (por ejemplo Firebase). Es infraestructura nueva. |

El antiguo 8.B (una sola serie de folios) pasó al núcleo: pasos 8.16 y 8.17.

---

## 7. App Android en Kotlin (fase siguiente; aquí solo el esqueleto)

**Ubicación:** carpeta `android/` en el mismo repositorio, para leer `contracts/` directamente. Flujo de CI propio, `android.yml` (`ubuntu-latest`, JDK 17, pruebas de JVM y lint, sin emulador). minSdk 26. Una sola app para la tablet y el celular; el rol sale de `DeviceRegistry`.

### 7.1 Módulos

Se empieza con cuatro módulos de Gradle y se dividen cuando crezcan:
- `:sync`: lee `contracts/sync/schema.json`, cliente Hrana, motor v2 (cola de salida, subida, bajada, LWW, lápidas, filas apartadas, combinación de pacientes, recálculo de existencias), cliente de la consulta entre equipos, y vinculación en la red local (QR, conexión a la PC, HMAC, AES-GCM y credencial). Una prueba compara las entidades de Room contra el manifiesto.
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
| QR | CameraX + ML Kit (modelo incluido en la app) | Lee el QR de la PC con la URL y el token (8.14). Funciona sin descargar el modelo de Google Play. Si falla, se pegan a mano. |
| Credencial | Android Keystore + `javax.crypto` (AES-256-GCM) para envolver la URL y el token | Sin depender de `EncryptedSharedPreferences`. Desde la decisión del 2026-10-11 no hay conexión a la PC ni protocolo de vinculación (8.14). |
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
  1. `SELECT Key, Value FROM _tpvc_meta`, sin DDL. `clinicId` debe coincidir con el de la credencial, `minClientProtocol` debe ser ≤ el suyo, y `fullUploadAtUtc` y `generationReady` deben existir.
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
  - Al terminar, escribe `RegisteredDevices.LastPushUtc`, `PendingCount` y `OldestPendingUtc`.
- **Bajada:**
  - La marca de agua es la hora del servidor menos 5 s, tomada antes de leer y guardada tal cual, sin interpretarla.
  - Llave (`SyncedAtUtc`, `SyncId`), con la forma de F1. Incremental: todas las tablas en una petición; página de 100 en Patients y PatientConsultations y de 500 en las demás, que se reduce a la mitad ante un error de tamaño o de tiempo.
  - Omite lo propio salvo en una descarga completa.
  - Lee su fila de `DeviceRegistry` en cada sincronización: rol y revocación (§4.7).
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
  - Escribe `InventoryMovements`, con `Delta`, un movimiento por lote y devoluciones deterministas. El celular escribe además el lote de una entrada de mercancía (`ProductBatches`) y su movimiento `EntradaCompra`.
  - Recalcula las columnas derivadas, con los conteos como ancla, después de cada escritura y de cada bajada.
  - Nunca escribe Products, saldos iniciales, conteos ni columnas derivadas, ni ProductBatches fuera de una entrada de mercancía.
- **Folios:** con conexión, el siguiente número de `FolioCounters` con la instrucción literal de `contracts/hrana/` (8.16). Sin conexión, o sin respuesta en 2 s, con su código de `DeviceRegistry` y el último número de la serie con letra guardado fuera de la base. Nunca regulariza: eso solo lo hace la PC (8.17). La búsqueda de pacientes y de tickets compara también `OriginalFolio`. La fecha del folio y "hoy" salen del reloj anclado, en la zona del consultorio. Sin índice único de folio y sin renombrar nunca.
- **Hojas del paciente:** formato de 8.18, con `RowId`. Conserva los campos que no conoce. No escribe `ChargedLineId`.
- **Freno:** como máximo 2 descargas completas al día y un tope diario estimado de lecturas y escrituras, igual que la PC (8.2).
- **Nunca:**
  - corre DDL;
  - siembra;
  - escribe catálogo, configuración, `ClinicProfiles`, `ClinicAssets`, `_tpvc_meta` o `DeviceRegistry`;
  - reutiliza un código ni renumera un folio;
  - sube a otra generación;
  - borra cambios sin subir: antes los sube o los exporta (§4.7).
- **Consulta entre equipos (solo la tablet):**
  - Mismas instrucciones y orden que la PC (`ConsultationHandoffService`): sincronizar antes; marca local antes del `INSERT`; recuperar con cerca; guardar la ficha antes del `UPDATE` condicionado; borrar la copia por ficha solo después de guardar.
  - Al aceptar, exige las marcas por campo que trae la copia (8.22).
  - Escribe `HandoffProtocol = 1` en su latido.

### 7.4 Roles en la app

| | Tablet | Celular |
|---|---|---|
| Pantallas | Inicio (citas de hoy, recientes, búsqueda), ficha, alta y edición, agenda, consulta completa (receta, insumos, servicios, cuenta, terminar y cobrar), consulta entre equipos, punto de venta, inventario de solo lectura, resumen y alertas | Resumen, corte de caja, alertas, compra sugerida, expedientes de solo lectura, agenda e inventario con entradas de mercancía |
| Compartir | Receta y ticket en PDF | Receta y ticket en PDF |
| Escribe | Sí (§4.6), también sin conexión: cobra con folio con letra, y una existencia puede quedar en negativo con aviso | Citas, lotes y movimientos de entrada, con sus lápidas (K4b). Nada clínico, ni recetas ni ventas |
| Revisión de consultas y latido | Al frente | Solo el latido, al frente |

### 7.5 Orden de trabajo y tamaño

Entre paréntesis, los pasos de la PC que necesita cada uno.

**Celular (primera versión):**
1. **K0.** Esqueleto con los cuatro módulos, CI, textos en español y tema.
2. **K1.** Contrato y reglas del celular, con sus vectores: reportes, receta y ticket (8.3, 8.11).
3. **K2.** Cliente Hrana y motor de lectura: primer arranque, descarga completa, cursor por página, recálculo, generación y freno (8.6, 8.7, 8.9, 8.13). Datos de prueba con un token pegado en las compilaciones de desarrollo.
4. **K3.** Vinculación y seguridad: pegar o escanear la URL y el token, esperar la aprobación de la PC, Keystore, SQLCipher, bloqueo de pantalla, revocación y reglas del 401 (8.14, 8.15).
5. **K4.** Celular: resumen, corte, alertas, compra sugerida, expedientes, inventario (existencias y lotes) y agenda de solo lectura, y compartir la receta y el ticket (8.11, 8.12). Solo baja datos y escribe su latido. **Primera versión del celular.**

**Tablet:**
6. **K5.** Motor de escritura: cola de salida, LWW, desempate, filas apartadas, cerca de generación, combinación de pacientes, movimientos, folios (del contador, y con letra sin conexión) y WorkManager. Pasa los escenarios contra el servidor simulado (8.16–8.23, 8.25).
7. **K6.** Inicio, ficha, agenda con aviso de empalme, alta y edición con aviso de duplicado; la búsqueda encuentra también el folio sin conexión (8.17, 8.26).
8. **K7.** Consulta: las secciones en el orden de la PC con la cuenta a la derecha, hojas con `SyncId` y `RowId`, receta con plantillas y calculadora pediátrica, terminar y cobrar (nota, venta, movimientos por lote, cita "Atendida"), también sin conexión con el folio con letra, y compartir receta y ticket. Mismos pasos y textos que la PC: la meta es que no se note la diferencia. La pantalla se queda encendida durante la consulta.
9. **K8.** Consulta entre equipos: aviso, "Continuar aquí", "Enviar a…" y solo lectura mientras está ofrecida (8.10, 8.24, 8.25). **Con K5–K8, primera versión de la tablet.**
10. **K9.** Punto de venta en la tablet, con `RankProducts` y el prefijo `3*`, también sin conexión. La búsqueda de tickets compara también `OriginalFolio`.
11. **K10.** Accesibilidad, tema oscuro y revisión de textos. Más adelante, impresión ESC/POS por Bluetooth con `TicketText`.

**Celular, segunda versión:**

12. **K4b.** Agendar con aviso de empalme (la agenda de K6) y registrar entradas de mercancía: lote y movimiento de entrada en el libro. Necesita el motor de escritura de K5 y, en la PC, 8.19–8.20 (libro de existencias) y 8.22–8.23 (resumen de citas derivado, empalmes); desde 8.22 agendar ya no escribe el paciente. Puede salir en cuanto existan K5 y K6, antes que la primera versión de la tablet.

**Tamaño estimado** (gruesa, para un desarrollador de tiempo completo):

| Parte | Tiempo |
|---|---|
| Reglas y vectores (K1 y las de la tablet) | 4–6 semanas |
| Motor de lectura y escritura (K2, K5) | 6–9 semanas |
| Vinculación y seguridad (K3) | 2–3 semanas |
| Pantallas (K4, K4b, K6–K9) | 13–19 semanas |
| Pulido y pruebas en equipos reales | 3–4 semanas |
| **Total Kotlin** | **7–10 meses** |
| Pasos de la PC 8.2–8.26 (25 versiones; 8.20, 8.21 y 8.22 de riesgo alto o medio-alto) | ~3–4 meses más |

El celular (K0–K4) es la parte más corta y puede empezar en cuanto exista 8.15. Además hay un costo permanente: todo cambio a `GetTableMaps` o a una regla con vectores pide otra versión de la app.

### 7.6 Cómo se prueba sin equipos

- **Vectores:** JUnit lee los mismos archivos de `contracts/golden/` que C#.
- **Intercambios grabados:** `MockWebServer` reproduce `contracts/hrana/*.jsonl`. Las peticiones de Kotlin se comparan por significado, no byte por byte, salvo el SQL, que debe ser el literal.
- **Escenarios de varios equipos:** la CI de Kotlin levanta el servidor simulado de 8.13 y corre `contracts/scenarios/` con el motor de la PC (C#) y el de Kotlin sobre el mismo servidor. Por ejemplo: la PC y la tablet venden la última pieza; dos equipos cancelan el mismo ticket; alergias en la tablet y teléfono en la PC; un renglón en cada equipo en la misma hoja; la PC reemplaza la nube mientras la tablet sube; la PC regulariza un folio con letra mientras la tablet edita al paciente. El mismo job corre la revisión de planes sobre las consultas de Kotlin.
- **Base local:** Room en la JVM con el controlador SQLite incluido (`androidx.sqlite`). SQLCipher solo se comprueba en el equipo real.
- **Pantallas:** Robolectric para los estados clave de Compose (sin conexión, "N cambios sin sincronizar", "Sincronización pausada", confirmaciones, solo lectura).
- **En equipos reales**, solo lo que no se puede simular: leer el QR con la cámara, vincular en la red del consultorio (Firewall de Windows, aislamiento de clientes del Wi-Fi), el PDF de la receta junto al de la PC, la hoja de compartir, Keystore y StrongBox, los tiempos del segundo plano con Doze y la suspensión de Samsung, y un día completo en el consultorio. Va en la sección D de `PENDIENTES_ANDROID.md`.

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
  - "Orden sugerido": pasos 8.2–8.15; el celular (K0–K4); pasos 8.16–8.26; la tablet (K5–K10) y el celular que escribe (K4b).
- **Sección A**, párrafo de entrada. Revertir "la tablet y el celular no tienen base de negocio… resueltos por diseño". Queda: "siguen siendo bloqueantes; la tablet escribirá en su base y sincronizará por Turso; el celular baja todo y escribe citas y entradas de mercancía".
- **Columna "Cómo queda":**
  - A1: libro de movimientos, lotes cerrados y conteos como ancla, 8.19–8.20. Conservar la corrección "los lotes no se pueden reconstruir con los movimientos de hoy".
  - A2: 8.8. Conservar lo de los pacientes demo.
  - A3: consecutivo desde `FolioCounters` en Turso; sin conexión, la letra del equipo (`EXP-2026-T1-001`), que la PC regulariza una vez al día dejando `OriginalFolio` para buscarlo; índice normal sin `-R` (8.16, 8.17; respuesta 1 del médico). Conservar la corrección de CNS.
  - A4: 8.18, con `RowId` y **sin relleno de los JSON existentes**. Corregir el texto actual, que dice que "se completan al arrancar". Conservar el error latente entre dos PC.
  - A5: 8.24.
  - A6: sigue vigente, con el reporte de 8.7; la primera subida por `Seq` (8.5) reescribe todo una vez.
- **B1:** se queda Kotlin. Quitar "la app no habla con Turso ni reimplementa el protocolo v2" y "OkHttp con el certificado fijado". Queda: "implementa el motor v2 y la consulta entre equipos; la conformidad se comprueba con `contracts/` (8.3, 8.11, 8.13, 8.25, 8.26)". Pila en el plan §7.
- **B2:** sigue retirado. Lo reemplazan los generadores puros (8.11) y el contrato exportado (8.3), ya no "la API de la red".
- **B3:** quitar que los ViewModels "corren en la PC" sin pantalla y la opción `RemoteConsultationHost`/`ConsultationEngine`. Son solo la referencia de comportamiento.
- **B4:** el rol sale de `DeviceRegistry`, que solo escribe la PC. Con el nivel C todos los tokens son completos y los límites por rol los aplica la app (8.10, 8.14). Matriz en el plan §4.6.
- **C1:** QR limitado a la red local: direcciones y puerto de la PC, secreto de 128 bits y ventana de 10 min, sin token; código de confirmación en los dos equipos; credencial cifrada con AES-256-GCM; respaldo a mano (8.14). Quitar el certificado y el servidor permanente. En la app: K3.
- **C2:** la receta para compartir se dibuja con `PdfDocument` desde el modelo `PrescriptionSheet` (8.11) con el membrete de `ClinicAssets` (8.12); para imprimir, la plantilla portada de `PrescriptionHtmlBuilder` en WebView. Ticket con `TicketText`. ESC/POS en K10.
- **C3a:** volver al contrato de `ConsultationHandoffs` en Turso. El texto anterior a `0d9e12f` vale, sin la frase de reutilizar con MAUI, más compatibilidad por `HandoffProtocol` (8.10), cadencia (8.24) y marcas por campo en la copia (8.22). En la app: K8.
- **C3:** "la tablet trabaja sobre su base local (Room + SQLCipher) y sincroniza al volver la red; indicador «N cambios sin sincronizar»". La tablet cobra también sin conexión (respuesta 2 del médico): folio con letra y existencias que pueden quedar en negativo con aviso.
- **C4:** se queda Compose. Quitar "llegarán ya calculados por la PC" y "el cálculo será un comando a la PC": Kotlin porta las reglas con los vectores de 8.26.
- **C6:** el PDF se arma en el propio equipo, sin la PC (8.11, 8.12). Quitar "Sin la PC puede volver a compartir…". En la app: K4 (celular) y K7 (tablet).
- **D, lista "Fase 8".** Reemplazar firewall, IP, aislamiento de clientes y "la PC no se duerme" por:
  - aviso de hora (8.7);
  - unirse sin duplicados (8.8);
  - restaurar con la nube activa: la lista de lo que se perdería y "Volver a aplicar" (8.9);
  - vincular con QR en la red del consultorio, con el mismo código de confirmación en los dos equipos, el aviso del Firewall de Windows y el respaldo a mano (8.14);
  - "Quitar" con cambios pendientes y "Perdí este equipo", con la rotación en el panel de Turso (8.15);
  - un folio hecho sin internet sale con letra, al día siguiente tiene su número, y buscar cualquiera de los dos lo encuentra (8.16, 8.17);
  - las existencias no cambian al convertirlas y aparece "Saldo inicial" (8.20);
  - lo de la tablet llega a la PC en menos de 30 s (8.24);
  - la última pieza vendida en dos equipos sin internet queda en −1 con aviso;
  - receta en PDF por WhatsApp desde el celular y la tablet, igual a la de la PC;
  - agendar y registrar una entrada de mercancía desde el celular (K4b);
  - TPVC excluido de la suspensión y de la optimización de batería en la tablet Samsung;
  - un día completo con la tablet: notas, tickets, folios y existencias cuadran en los tres equipos.

**`docs/TURSO_SYNC_ARCHITECTURE.md`**
- **§1:** quitar los puntos "solo respaldo", "solo la PC habla con Turso" y `SharedPcs`. Queda: los tres equipos sincronizan por Turso, cada uno con su base, y la PC es dueña del esquema remoto.
- **§2:** conservar los límites vigentes (500M, 10M, 5 GB, 100 bases, 3 GB, 1 día). Agregar el costo de `ALTER` y `CREATE INDEX` y el comportamiento de `BLOCKED`. Reemplazar la tabla de uso por la de §3.2 de este plan, con F1, F2 y el freno de consumo. La nota sobre las constantes viejas apunta a 8.2.
- **§3:** cambiar el diagrama de la red local por la estrella de §2.1. Matriz de `DevicePermissions` más los cuatro permisos nuevos (8.10). Quitar "la PC los aplicará en cada ruta", `PairedDevices` y las referencias a la "pregunta 4 del plan" (ya respondida: el celular ve expedientes, agenda citas, revisa inventario y registra entradas de mercancía; el corte de caja de la tablet queda oculto). Agregar `DeviceRegistry` y que con el nivel C todo lo hace cumplir la app (§4.6).
- **§4:** quitar "deja de ser el contrato de la app Android". Es el contrato de los dos motores y se fija en `contracts/sync/schema.json` (8.3).
  - §4.2: quitar "modo Backup, sin latido". Agregar que solo la PC crea o migra el esquema, las tablas y columnas nuevas, las claves de `_tpvc_meta`, `FolioCounters` y el `timeKind` de las fechas.
  - §4.3: conservar el párrafo de los hechos (filas omitidas, reloj atrasado). Cambiar el plan: filas apartadas, también las de referencia opcional (8.4); cursor `Seq` guardado en `DbMeta` como `LastPushedSeq` (8.5); descarga eficiente con cursor por página (8.6); hora del servidor (8.7). Quitar `LastBackedUpSeq`, "detiene el cursor", "N cambios sin respaldar" y "no se descarga".
  - §4.4: conservar "sin transacción". Folios: consecutivo desde `FolioCounters`, letra del equipo solo sin conexión y regularización diaria en la PC con `OriginalFolio`; índice normal y sin `-R` en adelante (8.16, 8.17). Agregar combinación por campo (8.22), existencias como libro (8.19–8.20) y cerca de generación (8.9).
  - §4.5: el contrato vale PC ↔ PC y PC ↔ tablet. Quitar `LanHandoffs`, "solo `SharedPcs`" y "capacidad `handoff.v1` (paso 8.23)". Conservar la corrección de que la revisión no lee la hora del servidor. Agregar cadencia e índice parcial (8.24), `HandoffProtocol` (8.10) y marcas por campo en la copia (8.22).
- **§5:** "Hoy: paquete en claro (se retirará en el paso 8.17)" pasa a "se retira en 8.2". Reemplazar el QR por la red del borrador con el flujo de §4.4 de este plan (8.14): la red local solo entrega la credencial, una vez, sin servidor permanente ni certificado.
- **§6:** nivel C (sin token de la API). El token de la PC no sale de la PC; el token para tablet y celular se crea a mano en el panel, se pega una vez en la PC y viaja cifrado al vincular. La base del consultorio, sola en su grupo; cortar acceso es rotar el grupo y pegar los tokens nuevos; las pruebas de rotación en otro grupo o cuenta; el resultado de V3, V4, V7, V9–V12 y V14–V16.
- **§7:** reescribir "Respaldo y recuperación". Quitar el modo `Backup`, `SharedPcs`, `lan.db` y la base aparte de cifras. Conservar la descripción de cómo se comporta hoy la restauración. Agregar: generación con cerca y `generationReady`, "Reemplazar la nube" o "Desconectar", la lista de lo que se perdería y "Volver a aplicar", la nueva descarga de los demás equipos, "Recuperar desde la nube" = unirse (8.8–8.9), el contador de folios en Turso, que "Reemplazar la nube" no toca, y el último número de las series con letra guardado fuera de la base (8.16), la restauración a un punto de 1 día y los 14 respaldos locales.

**`docs/PLAN_REDISENO_UI.md`**
- Principio 8: se queda (Kotlin no reutiliza los ViewModels: son su referencia).
- Tabla de fases:
  - fila E: "✅ Entre dos PC hecha (v1.17) · la tablet, con su app en Kotlin y el mismo contrato por Turso";
  - fila 8: "Datos listos para tres equipos con Turso · Plan".
- Fase E: quitar el párrafo "Plan de la Fase 8 … red del consultorio … `LanHandoffs`" y el "(entre dos PC)" de *Cómo llega*. *Cobro*: la venta de la tablet sube como movimientos del libro (8.19–8.20). *Pendiente*: "la tablet implementa el mismo contrato (K8)".
- Notas al final de la Fase 7: QR limitado a la red local (8.14) y PDF armado en el equipo (8.11, 8.12).
- Sección Fase 8: título "Datos listos para tres equipos con Turso (plan)" y su párrafo de entrada según §1 de este plan. Tabla de pantallas de la PC:
  - "Equipos vinculados" (lista con cambios pendientes, *Quitar*, "Perdí este equipo", "Esta PC reemplaza a la principal");
  - el campo del token para tablet y celular, y "Vincular tablet o celular" (QR de 10 min en la red local, código de confirmación, respaldo a mano);
  - "Unirse a una nube existente", "Recuperar desde la nube" y "Esta PC ya usaba esta nube";
  - la pregunta al restaurar ("Reemplazar la nube" / "Desconectar"), con la lista de lo que se perdería, y "Volver a aplicar";
  - "N cambios sin sincronizar";
  - el aviso de hora;
  - "Sincronización pausada: consumo anormal" y "Se agotó la cuota mensual de Turso";
  - la insignia "−1 · revisar inventario" y el lote negativo;
  - "Empalme" en la agenda;
  - el banner de duplicados y el aviso de folios repetidos;
  - "Folio sin conexión: …" en la ficha y en el ticket.

  Quitar "Uso de la nube", "Probar conexión (red)", "Consulta en curso en la tablet" y el aviso al cerrar.

**`docs/ARQUITECTURA_TECNICA.md`**
- **§1, principio 5:** reemplazar "La PC también será el servidor del consultorio" por: "tres equipos con su propia base, sincronizados por Turso (protocolo v2); la PC es la única que crea o migra el esquema remoto y es la referencia del protocolo (plan Fase 8)".
- **§6:** quitar `Services/Lan/` y `contracts/lan-api/v1/`. Conservar `android/`, `contracts/golden/` (8.3, 8.11, 8.26) y `Services/Documents/` (8.11). Agregar `Services/Pairing/` (8.14), `contracts/sync/` (8.3), `contracts/scenarios/` (8.25), `contracts/hrana/` (8.13, 8.25) y `docs/FAKE_HRANA_SPEC.md` (8.13). La línea de `PLAN_FASE8_VARIOS_EQUIPOS.md` dice "datos listos para tres equipos con Turso".
- **§7:** conservar el job de Linux (paso 8.2, no 8.3) y agregar el paso que copia `Microsoft.WindowsDesktop.App`, porque el proyecto de pruebas es `net10.0-windows`. Vectores en 8.3, 8.11 y 8.26. Cambiar "Ejemplos del contrato (lan-api)" y "Deriva de OpenAPI" por: deriva de `schema.json`, escenarios, intercambios grabados con el SQL literal, y los instrumentos de `FakeHranaServer` (reloj, lecturas cobradas, planes, tope de respuesta, `BLOCKED`, mapa de tokens), con el servidor HTTP de pruebas solo en 127.0.0.1.
- **§8:**
  - conservar `SyncChangeLog` (8.5), ahora con `SyncParkedRows` (8.4), `SyncApplyState`, `MarkForUpload`, triggers con hash y `UseSqlReturningClause(false)` (no `HasTrigger`);
  - `DbMeta`: `clinicId`, `generation`, `DbInstanceId`, `LastPushedSeq`, desfase, revisión del contrato por tabla, `StockLedgerVersion`, `CloudJoined`, "reemplazo en curso" y la fecha de la última regularización de folios (8.17);
  - quitar `LanRequestLog`;
  - agregar `OriginalFolio` (8.17), `FieldStampsJson`, `RowId` en las hojas, `Delta`, `SaleItemId`, `LedgerVersion`, `ClosedAtUtc` y `Conteo`;
  - "Guardado de pacientes por campo": guardar solo lo que cambió (8.21) y combinación con marcas por campo y por renglón (8.22), en lugar de `PatientPatch` con comparar y poner;
  - "Servidor" pasa a "Varios equipos": LWW por fila con la hora del servidor, combinación por campo en pacientes, existencias como libro con conteos, y folios consecutivos con contador en Turso, letra sin conexión y regularización diaria.
- **§9:** quitar "puede correr sin pantalla en la PC (8.21)", la subsección "Sin pantalla en el servidor" y la mención en la tabla de puertos.
- **§10:** cambiar "Servidor del consultorio" por "Varios equipos con Turso (plan Fase 8)": resumen de `SyncChangeLog`, `SyncParkedRows`, `StockLedger`, `PatientMerge`, `FolioFactory`, `FolioRegularizationService`, `SyncScheduler`, `DeviceRegistry` y la vinculación en la red local, con enlace a este plan.

**`README.md`**
- Línea de Turso: "Protocolo v2: la PC, la tablet y el celular sincronizan por Turso; esquema remoto y garantías". Quitar "Turso queda solo como respaldo".
- Línea de la Fase 8: título "Fase 8: datos listos para tres equipos con Turso". Descripción: "plan para que los tres equipos (app Android en Kotlin) escriban sin perder datos, vinculación por QR y pasos en la PC". Conservar el ícono de la línea.

**También:**
- `CHANGELOG.md`, en `[Sin publicar]`: cambiar la entrada "varios equipos en la red local" por este plan.
- `docs/MANUAL_DE_USUARIO.md` tendrá el capítulo "Tablet y celular" en 8.14 (vincular en la red del consultorio y el aviso del Firewall, equipos vinculados, restaurar con la nube), ampliado en 8.15 (perdí este equipo y rotar los tokens) y 8.17 (folios sin conexión).

---

## 9. Respuestas del médico (2026-10-09)

Ya no queda ninguna pregunta que bloquee un paso.

1. **Folios.** Un solo consecutivo por serie, tomado de Turso. Sin conexión, el equipo crea el folio con su letra (`EXP-2026-T1-001`) y lo sube así. La PC, una laptop, lo regulariza en segundo plano una vez al día, cuando no hay consulta ni venta en curso: le da el siguiente número disponible y deja asociado el folio con letra, para que la búsqueda lo encuentre (8.16, 8.17).
2. **Cobrar en la tablet sin internet: sí.** La meta es no notar diferencia entre usar la PC o la app. Una existencia puede quedar en negativo con "revisar inventario" y se corrige con un conteo (8.20, K7).
3. **El celular** ve expedientes, agenda citas, revisa inventario y registra entradas de mercancía. No escribe notas, recetas ni ventas (§4.6, K4b).
4. **Cuenta de Turso: sin token de la API.** Nivel C: tokens creados a mano en el panel, sin vencimiento, que solo se cortan rotando el grupo. El QR es solo una facilidad y queda limitado a la red local; si no funciona, la URL y el token se teclean a mano (§4.3–§4.5).

**Supuesto por confirmar** (no bloquea): el celular conserva el resumen, el corte de caja y las alertas de existencias que ya le da `DevicePermissions` (`AndroidMobileAdmin`, `Models/TursoConfig.cs:42-47`).
