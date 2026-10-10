# Fase 8 · Datos listos para tres equipos con Turso

Estado al 2026-10-10 (TPVC v1.20.0). **Hecho: el paso 8.2 (v1.20.0).** Lo demás todavía es plan. Aquí se fijan la arquitectura, el contrato que tendrán que cumplir la PC y la app en Kotlin, y el orden de los pasos. El código viene después, un paso por versión. Cada versión deja la PC funcionando sola y trae pruebas que corren en Linux con el servidor Turso simulado (`FakeHranaServer`, en `tests/TPVC.Tests/TursoSyncEndToEndTests.cs`), sin equipos Android ni una base real de Turso. Para que eso funcione en la CI hace falta el paso 8.2: hoy el proyecto de pruebas es `net10.0-windows` y no arranca en Linux sin el runtime de escritorio.

Esta versión **reemplaza el borrador del mismo día** que se publicó en este archivo (commit `0d9e12f`). Ese borrador convertía la PC en servidor de la red local y en la única que escribe, y dejaba Turso solo como respaldo. El médico lo descartó el 2026-10-09 después de ver que el uso real de Turso cabe de sobra en el plan gratuito (§3). Esta versión ya trae las correcciones de dos revisiones técnicas.

El mismo día el médico respondió las cuatro preguntas abiertas (§9), y esta versión ya las incorpora: un solo consecutivo de folios, con la letra del equipo solo sin conexión y una regularización diaria en la PC; la tablet cobra sin internet; el celular ve expedientes, agenda citas, revisa inventario y registra entradas de mercancía; y la cuenta de Turso no tiene token de la API, así que se usa el nivel C (tokens creados a mano); el QR, que es solo una facilidad, queda limitado a la red local (y no hay base de vinculación).

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
| 9 | **La cuenta de Turso no tiene token de la API de la plataforma.** El QR es solo una facilidad y queda limitado a la red local. | Nivel C: tokens creados a mano en el panel, sin vencimiento, que solo se cortan rotando el grupo. Sin base de vinculación: la PC entrega la credencial por la red local, cifrada con un secreto del QR (§4.3–§4.5). |

**Pedidos del médico que cubre este plan:**
- Vincular la tablet leyendo un código QR, en la red del consultorio (paso 8.14).
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
 │ 13 tablas sincronizadas, SyncTombstones, ClinicProfiles, ClinicAssets,       │
 │ RegisteredDevices, DeviceRegistry, ConsultationHandoffs, FolioCounters,      │
 │ _tpvc_meta                                                                   │
 └──────────────────────────────────────────────────────────────────────────────┘
  Solo al vincular, una vez: la PC entrega la credencial por la red local (§4.4).
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
| Entrega de la credencial al vincular | (a) base de vinculación en Turso, con código de 10 dígitos y credencial sellada; (b) la PC la entrega por la red local, cifrada con un secreto del QR | (b) | El médico limitó el QR a la red local. Sin base aparte ni token en el QR; el secreto nunca viaja por la red y la ventana dura 10 min (§4.4, §4.5). |
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
| Peticiones al día | ~8,700 | ~23,000 | ~12,800 (PC ~7.9k, tablet ~4.9k, celular ~40) | El plan no limita peticiones |
| Filas leídas al mes | 0.4M–4.2M | hasta ~12M | 2M–4.5M: cada revisión lee 4–11 filas (con el índice parcial) y hay ~600 bajadas al día de ~25 filas | 0.4–0.9% de 500M |
| Filas escritas al mes | ~45k (el latido es 1,200 al día) | ~120k | ~50k: al día, ~975 latidos (960 de la PC y la tablet, cada 60 s, y ~15 del celular), ~400 filas de datos y ~300 `LastPushUtc` | ~0.5% de 10M; hasta ~1.5% si Turso cuenta las entradas de índice (V10) |
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

### 4.2 Qué ofrece Turso y qué se usa

| Pieza | Qué hace | Aquí |
|---|---|---|
| Token de base o de grupo (JWT) | Se crea en el panel o con la CLI. Vencimiento propio (por defecto "never"), autorización `full-access` o `read-only`. | Nivel C: el de la PC y uno para tablet y celular, `full-access` y sin vencimiento, creados a mano (§4.3). |
| Permisos finos por tabla y acción | Documentados para la CLI y para JWKS. | No se usan. Sin la API la PC no puede crear un token por equipo, y la revocación sería de todos modos rotar el grupo. Los límites por rol los aplica la app (§4.6). |
| Revocación | No hay revocación por token. `turso db tokens invalidate` rota las llaves de **todo el grupo**, con una breve interrupción. | La base del consultorio va sola en su grupo. "Cortar acceso ahora" es esta rotación (§4.7). |
| Token de la API de la plataforma | Dejaría a la PC crear y rotar tokens (nivel A). | La cuenta no lo tiene (§9). |
| JWKS propio | En beta, solo con Clerk y Auth0. | No se usa: pide un servidor. |
| Plan gratuito | Sin lista de IP permitidas. | La bitácora de auditoría está por confirmar (V11). |

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

V1, V2, V5, V6, V8 y V13 se retiraron: solo servían al nivel A o a la base de vinculación. El resultado se anota en `docs/TURSO_SYNC_ARCHITECTURE.md` §6.

### 4.3 Credenciales: nivel C

| Quién lo tiene | Token | Dónde vive |
|---|---|---|
| PC | Su propio token `full-access`, pegado a mano como hoy. **Es la única que cambia el esquema.** | Cifrado con DPAPI. Nunca sale de la PC. |
| Tablet y celular | Un solo "token para tablet y celular", `full-access`, creado a mano en el panel y pegado una vez en la PC. | En la PC, con DPAPI. Viaja cifrado solo al vincular (§4.4). En el equipo, envuelto con el Keystore. |
| Otra PC con Windows ("Unirse con código") | El token para tablet y celular, por la misma vinculación en la red local. | Con DPAPI. Se vuelve a vincular después de una rotación. |
| QR de vinculación | Ninguno: direcciones, puerto, `PairingId`, secreto y vencimiento. | En la pantalla de la PC, 10 min. |

- Los dos tokens no vencen y los dos mueren con una rotación del grupo (§4.7). Un vencimiento sin renovación automática solo dejaría a la tablet con cambios atorados.
- Turso no distingue roles: los dos tokens pueden todo, y los límites por rol los aplica la app (§4.6).

### 4.4 Vinculación por QR en la red local

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

1. **Revocar** con motivo "perdido": la PC pone `RevokedAtUtc`. Si el equipo se conecta antes de la rotación y lee su fila, borra su base, su credencial y sus borradores sin subir nada.
2. **"Cortar acceso ahora"** (marcado por defecto). Sin token de la API la PC no puede rotar sola, así que guía al médico:
   - rotar los tokens del grupo en el panel de Turso o con `turso db tokens invalidate`, con una breve interrupción (V4);
   - crear dos tokens nuevos, el de la PC y el de tablet y celular, y pegarlos en la PC, que sincroniza en ese momento;
   - los demás equipos reciben 401 y muestran "Vuelva a vincular" sin perder sus cambios pendientes. Se vinculan otra vez con un QR nuevo en la red local: el que tenía cambios pendientes conserva su base y los sube; el que no tenía nada pendiente ya se borró (regla 3) y vuelve a bajar todo (~20–30k lecturas).
3. **Reglas ante un 401** (con el nivel C no hay vencimientos: un 401 es una rotación):
   - con cambios pendientes, el equipo se bloquea y los conserva hasta volver a vincular;
   - sin nada pendiente (casi siempre el celular), se borra solo tras dos 401 con 10 min de diferencia.
4. **Límites:**
   - Si el plan gratuito no tiene bitácora de auditoría (V11), hay que suponer que el equipo perdido pudo leer todo hasta la rotación.
   - Se recomienda borrarlo también con "Encontrar mi dispositivo".
   - Sin rotar, el equipo perdido conserva el acceso: no hay otro límite.
5. **Si se pierde la PC:** rotar desde el panel de Turso, instalar la PC nueva con "Recuperar desde la nube" (8.8) y los tokens nuevos, marcarla como "Esta PC reemplaza a la principal" (8.10) y volver a vincular la tablet y el celular.

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
  - `[TestCategory("Windows")]`: `UiSmokeTests`, `UiPerfTests`, dos de Apariencia y dos convertidores de WPF. Ninguna prueba usa DPAPI real (fuera de Windows el token se guarda como `B64:`).
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

#### 8.14 · Vincular por QR en la red local
- **Antes:** [T] V3 (§4.2).
- **Cambia:**
  - Campo para pegar una vez el "token para tablet y celular" (§4.3), creado a mano en el panel, guardado con DPAPI.
  - `FakeHranaServer` con un mapa de tokens: uno desconocido o rotado recibe 401. `TursoClient` lo traduce a "Turso rechazó la credencial: vuelva a vincular o pegue un token nuevo".
  - `Services/Pairing/LanPairingHost`, sin WPF y con `TimeProvider`: `TcpListener` en un puerto al azar, solo en las direcciones privadas de la PC y solo mientras dura la ventana de 10 min (§4.4). Un equipo por ventana; tres HMAC inválidos la cierran.
  - QR con QRCoder (licencia MIT) y el flujo de §4.4: secreto de 128 bits, HMAC del equipo, código de confirmación y credencial con AES-256-GCM.
  - Respaldo "Teclear a mano" (URL y token), con la aprobación del equipo nuevo en la PC.
  - "Unirse con código" para otra PC, que recibe su código (`P2`…).
  - `PairingReferenceClient` en C# dentro de las pruebas, como especificación ejecutable para Kotlin.
  - Vectores `pairing-qr`, `pairing-hmac`, `pairing-credential` y `confirmation-code`.
  - Capítulo "Tablet y celular" en el manual, con el aviso del Firewall de Windows (permitir TPVC en redes privadas) y la red del consultorio marcada como privada.
- **Prueba:**
  - El flujo completo, con la lista de direcciones inyectada (en las pruebas, 127.0.0.1).
  - Un HMAC inválido no recibe nada, y tres cierran la ventana.
  - Rechazar no manda la credencial.
  - Una ventana vencida ya no escucha; un cifrado alterado se rechaza.
  - Ni el QR ni lo que pasa por el socket llevan el token en claro.
  - Nunca escucha en una dirección pública ni en `0.0.0.0`.
  - Un 401 se traduce y no borra nada.
  - [W] Capturas en los 4 temas.
- **Riesgo:** medio (criptografía, red local y experiencia de uso). Lo mitigan los vectores y el respaldo a mano.

#### 8.15 · Quitar, equipo perdido y rotación
- **Antes:** [T] V4 y V11.
- **Cambia:**
  - *Quitar* con aviso de cambios pendientes; el equipo sube lo pendiente antes de borrarse, o lo exporta.
  - "Perdí este equipo": revocar en `DeviceRegistry` y "Cortar acceso ahora" guiado (§4.7): rotar en el panel de Turso, pegar los dos tokens nuevos en la PC y volver a vincular los demás. Estado "Vuelva a vincular" en la lista.
  - Reglas ante un 401 (§4.7).
- **Prueba:**
  - Después de rotar en el simulador, los tokens viejos reciben 401; con los nuevos pegados, la PC sigue sincronizando.
  - Volver a vincular con el mismo `DeviceId` conserva las filas pendientes.
  - Un 401 con cambios pendientes nunca borra la base.
  - *Quitar* con cambios pendientes los sube antes de borrar.
  - Un equipo que lee su fila revocada con motivo "perdido" se borra sin subir.
- **Riesgo:** medio-bajo.

**Corte: primera versión del celular.** Con 8.2–8.15 puede salir el celular (K0–K4, §7.5): solo baja datos (escribe su latido), se vincula con QR en la red local, y comparte la receta y el ticket. Prueba en equipos reales Hrana, la vinculación y el PDF antes de escribir la tablet. Lo que escribe el celular (agendar y entradas de mercancía, K4b) llega después de 8.23. Cuando 8.20 suba `protocol` a 3, el celular se actualiza por la prueba interna de Play.

### Etapa 3 · Datos correctos con varios equipos que escriben

#### 8.16 · Folios consecutivos desde Turso, con letra sin conexión (A3)
- **Antes:** [T] V16.
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
- **Cambia:**
  - Columnas `Patients.OriginalFolio` y `Sales.OriginalFolio`. Aceptan nulo y viajan por el ciclo normal.
  - `FolioRegularizationService`, sin WPF y con `TimeProvider`, **solo en la PC principal**. Corre como máximo una vez al día (fecha en `DbMeta`), y solo si la nube está al día, ningún equipo no revocado reporta cambios pendientes (`PendingCount`; si uno lleva más de 7 días con cambios pendientes sin verse, la PC lo avisa en *Equipos vinculados*), no hay consulta en curso (`ConsultationService.HasActiveConsultation`) ni venta en curso (`PosViewModel.HasCartItems`). Tampoco corre con el alta o edición de un paciente abierta (`PatientForm.IsPatientModalOpen`) y salta al paciente que `CanReplaceSelectedPatientInstance` no deja reemplazar. Al terminar recarga la lista de pacientes como después de una bajada (`ForceFreshPatientIds`): hasta 8.21 una copia vieja en memoria devolvería el folio con letra. Si un registro ya regularizado vuelve con su folio con letra, recibe el mismo número que ya tenía asignado, no uno nuevo. Si la laptop estuvo cerrada ese día, corre la siguiente vez que esté abierta y libre. Se detiene entre un registro y otro si empieza una consulta o una venta, y sigue en la siguiente oportunidad.
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
| QR | CameraX + ML Kit (modelo incluido en la app) | Funciona sin descargar el modelo de Google Play. Como respaldo, la URL y el token se teclean a mano. |
| Vinculación y criptografía | `java.net.Socket` hacia la PC, solo durante la vinculación; Android Keystore + `javax.crypto` (HmacSHA256, HKDF con HmacSHA256, AES-256-GCM) | Sin depender de `EncryptedSharedPreferences`. Se comprueba con los vectores y el `PairingReferenceClient` de 8.14. |
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
4. **K3.** Vinculación en la red local y seguridad: QR, conexión a la PC con el secreto (HMAC), código de confirmación, credencial cifrada, respaldo a mano, Keystore, SQLCipher, revocación y reglas del 401 (8.14, 8.15). Si la versión de Android pide permiso para la red local, se pide al vincular.
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
