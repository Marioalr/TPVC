# Fase 8 · Datos listos para varios equipos en la red local

Estado al 2026-10-09 (TPVC v1.19.0, rama `fase-6/cierre`). **Por ahora es solo el plan.** Aquí se decide la arquitectura, el contrato de la red local y el orden de los pasos. El código empieza después, con un paso por versión, y cada versión deja la PC funcionando sola. Este plan reemplaza lo que dicen los documentos anteriores sobre tres temas: .NET MAUI, Turso como canal principal y la vinculación con el token de Turso. La lista de cambios está en §7.

**En una frase:** solo la PC escribe. La tablet y el celular son apps en Kotlin que le piden todo a TPVC por HTTPS en la red del consultorio. Turso queda como copia de respaldo y solo la PC le sube datos.

---

## 1. Decisiones del médico y supuestos

| # | Decisión | Qué implica |
|---|---|---|
| 1 | Hay tres equipos: la PC con Windows, una tablet Android y un celular Android. El 99% del tiempo están en la misma red del consultorio (Wi-Fi o cable). | La red local es el canal principal. |
| 2 | Turso es solo respaldo y una salida de emergencia poco frecuente. Por eso se eligió el plan gratuito. | Turso se usa lo mínimo y sin sondeos continuos. |
| 3 | La app Android se escribe en **Kotlin nativo**, no en .NET MAUI. | En Android no se reutiliza código C#. El código de la PC es la referencia; lo que debe salir igual en los dos lados se comprueba con vectores de prueba compartidos (§5, paso 8.14). |
| 4 | La Fase 8 cubre dos cosas: dejar los datos seguros para varios equipos y preparar todo lo que va antes de las pantallas en Kotlin. En este momento solo se escribe el plan y la documentación. | El código sigue después, paso por paso (§5). |
| 5 | La receta impresa no lleva línea de firma (ya decidido). | La receta de la tablet sale del mismo HTML que la de la PC. |

**Pedidos anteriores que este plan cubre:**
- Vincular la tablet leyendo un código QR.
- Compartir la receta y el ticket en PDF desde cualquier lugar donde se vean.
- Continuar una consulta en otro equipo (hoy ya funciona entre dos PC por medio de Turso).

**Reglas de la consulta que se conservan en la tablet:** la consulta nunca manda al POS, "Se surte aquí" viene marcado por defecto y no existe el botón "terminar sin cobrar".

**Supuestos**
- La PC es el único equipo que funciona completo por sí solo, siempre. Con el servidor apagado, TPVC funciona igual que en v1.19.
- TPVC queda abierto en la PC durante el horario del consultorio (pregunta 3 de §8).
- **El 1% fuera de la red:**
  - La tablet en el consultorio, cuando el Wi-Fi se cae o la PC está cerrada: lee lo que tiene guardado y sigue escribiendo la consulta abierta. Terminar y cobrar esperan a la PC, salvo que el médico pida otra cosa (pregunta 1).
  - El celular en casa: muestra las últimas cifras que guardó. Cifras en vivo solo si el médico las pide (pregunta 2).
  - Si la PC se descompone: se recupera en una PC nueva desde Turso y los equipos se vuelven a vincular.
- En v1 no hay segunda PC con Windows. El modo actual de compartir la nube entre computadoras queda congelado y marcado como no recomendado.
- Red: la PC, la tablet y el celular están en la misma subred y el Wi-Fi no aísla a los clientes entre sí. Se recomienda reservar la IP de la PC en el módem.

---

## 2. Arquitectura elegida

### 2.1 Fuente de verdad

- **`consultorio.db` en la PC** (`AppDbContext.DbPath`) es la única base de negocio.
  - Solo la PC pone `UpdatedAtUtc` y asigna los folios EXP, TKT y CNS.
  - Solo la PC ejecuta el código de existencias: `RegisterSaleAsync`, `CancelSaleAsync`, FEFO (`DeductFromBatches`), `EnsureStock`.
  - Solo existen los `Id` enteros de la PC. Los demás equipos se refieren a los registros por `SyncId`.
- **La tablet y el celular no escriben filas.** Mandan comandos (alta de paciente, cita, venta, terminar consulta…) y la PC los ejecuta con el mismo código que usan sus pantallas.
- **Qué guarda cada equipo Android.** Ninguno tiene base de negocio ni token de Turso; todo va cifrado (§6).
  - Tablet: respuestas de la PC guardadas por recurso, el índice de pacientes para buscar sin conexión, el borrador de la consulta abierta y la cola de comandos sin confirmar.
  - Celular: reportes y expedientes vistos.
- **Lo nuevo en la PC:**

  | Dónde | Qué | Por qué ahí |
  |---|---|---|
  | `consultorio.db` | `SyncChangeLog`: registro de cambios llenado por triggers de SQLite, con un renglón por registro y una secuencia (`Seq`) que solo sube. | Sirve de cursor para el respaldo y para los avisos a los equipos. Registra también los borrados en cascada. |
  | `consultorio.db` | `DbMeta(Generation)`: un GUID que cambia en cada restauración. | Sirve para que los equipos sepan que la base cambió por completo. |
  | `consultorio.db` | `LanRequestLog`: registro de comandos ya aplicados. | Tiene que confirmarse en la misma transacción que el efecto del comando, y SQLite en modo WAL no confirma de forma atómica entre dos archivos. Al restaurar, este registro regresa junto con los datos, que es lo correcto. |
  | `lan.db` (archivo aparte, `LanDbContext`) | `PairedDevices`, `PairingCodes`, `LanAccessLog` (bitácora de accesos), `LanHandoffs` y `HostedConsultations`. | Una restauración de `consultorio.db` nunca revive un equipo quitado ni olvida uno nuevo. No entra en la huella de Turso ni en `EnsureSchema`. |

### 2.2 Flujo de datos

```
 Tablet / Celular (Kotlin)                      PC (TPVC.exe)
 ┌───────────────────────┐   HTTPS en la red   ┌──────────────────────────────┐
 │ caché cifrada         │ ──────────────────► │ Servidor del consultorio     │
 │ borrador de consulta  │  comandos (POST)    │  (Kestrel, Services/Lan)     │
 │ cola de comandos      │ ◄────────────────── │        │                     │
 └───────────────────────┘  datos listos para  │ InventoryService, Patients…  │
            ▲               mostrar (GET)      │        │                     │
            └──── avisos por espera larga ──── │ consultorio.db ── triggers ─►│ SyncChangeLog
                                               │        │                     │
                                               └────────┼─────────────────────┘
                                                        ▼ solo subida, ≤ 1 vez cada 5 min
                                                   Turso (copia de respaldo)
```

- **Leer.** La pantalla pide un recurso. La PC lo devuelve ya armado, con orden, textos en es-MX y etiquetas en español, y lo calcula con las funciones que ya existen (`RankPatients`, `BuildTimeline`, `BuildSlots`, `GetDailyCashCutAsync`…). El equipo lo muestra y lo guarda con su ETag.
- **Escribir.** El equipo manda un comando con una clave de idempotencia generada en el equipo. La PC hace tres cosas:
  1. Valida el permiso y las reglas.
  2. Ejecuta el servicio de siempre y anota el comando en `LanRequestLog` en la misma transacción.
  3. Responde con el resultado.

  Los eventos actuales de `InventoryService` refrescan las pantallas de la PC.
- **Al momento.** Cada cambio sube el `Seq`. La petición de espera larga de cada equipo regresa en menos de 1 s con lo que cambió, y el equipo vuelve a pedir lo que tiene en pantalla.
- **Consulta en la tablet.** La consulta vive en la PC: borrador, cuenta y reglas. La tablet escribe el texto y los signos en su borrador y los manda en tandas. Lo que toca dinero, existencias o seguridad (receta, insumos, servicios, alergias, cobro) se manda como comando. Detalle en §3.8.
- **Respaldo.** La PC sube a Turso lo que el registro marca después de su cursor. No descarga nada en uso normal.

### 2.3 Papel de Turso: respaldo y emergencia

- **Nuevo ajuste `TursoConfig.CloudMode`** (paso 8.2):
  - `Backup`, el modo por defecto:
    - Solo sube datos: como máximo una subida cada 5 min mientras haya cambios, más una al cerrar, con tope de 5 s.
    - No descarga.
    - No revisa `ConsultationHandoffs` (hoy lo hace cada 4 s en Pacientes y cada 30 s fuera de ahí, `ConsultationHandoffViewModel`).
    - No manda latido cada 30 s (`ConsultationHandoffService.HeartbeatInterval`).
    - Registra el equipo en `RegisteredDevices` una sola vez por arranque.
  - `SharedPcs`: el comportamiento actual, congelado sin cambios. Solo sirve para dos PC, no recibe trabajo nuevo y está marcado como no recomendado.
  - **Migración:** queda en `Backup`, salvo que `RegisteredDevices` tenga otra PC con Windows vista en los últimos 30 días. Con el servidor del consultorio encendido, la nube siempre queda en `Backup`.
- **Emergencia.** En una PC nueva se usa "Recuperar desde la nube": descarga completa a una base vacía, sin sembrar nada, y después se vinculan los equipos otra vez (paso 8.7).
- **Restaurar un respaldo local** pregunta "Reemplazar la copia de la nube con este respaldo" (paso 8.7).
- **Opcional:** una segunda base de Turso solo con cifras, sin expedientes, para el celular fuera del consultorio (paso 8.A, si el médico lo pide).

**Uso estimado frente al plan gratuito.** Límites vigentes del plan gratuito, por mes (<https://turso.tech/pricing>, consultado el 2026-10-09): 500M filas leídas, 10M filas escritas, 5 GB, 100 bases y 3 GB de sincronizaciones de réplicas (no se usan). Supuestos: 10 h abierto, 25 pacientes al día y unos 300–600 registros cambiados al día.

| Concepto | Hoy (v1.19, solo la PC) | Fase 8 (modo respaldo) | Proporción del plan |
|---|---|---|---|
| Peticiones al día | ~8,700, más del 90% del sondeo de consultas entre equipos y el latido (~23,000 si los tres equipos hicieran lo mismo) | ~50–120 | El plan no limita peticiones |
| Filas escritas al mes | Solo el latido: 26k–36k por equipo, más los datos | ~9k–18k | 0.1–0.2% de 10M |
| Filas leídas al mes | Hasta millones con tres equipos | ~30k–60k (revisiones al insertar), más la verificación a pedido | < 0.02% de 500M |
| Almacenamiento | — | Decenas de MB al año | < 5% de 5 GB a 5 años |
| Bases | 1 | 1 (2 con el paso 8.A) | de 100 |
| Equipos que hablan con Turso | La PC (y la tablet en el diseño anterior) | Solo la PC | — |

**Costos de una sola vez:**
- La primera subida con el cursor nuevo (8.6) reescribe todo una vez: unas 20k filas, 0.2% del mes.
- Reemplazar la copia de la nube después de restaurar cuesta lo mismo.
- Recuperar desde la nube lee todo una vez.

El contador de uso de la app (`RecordUsage`) sigue siendo una estimación. La cifra real está en el panel de Turso.

### 2.4 Alternativas descartadas

| Alternativa | Por qué no |
|---|---|
| Cada equipo con su base, sincronizados entre sí con un servidor compatible con Turso en la PC | Siguen escribiendo varios equipos. A1–A4 y H1–H5 se tendrían que resolver con reglas sutiles: libro de existencias, combinación campo por campo, folios por equipo. Además habría que portarlas exactas a Kotlin, unas 6–8 semanas antes de la primera pantalla. Un error ahí pierde datos clínicos sin aviso, y todos los equipos llevarían el token completo de Turso. |
| Tablet y celular escriben directo en Turso (protocolo v2 actual) | Cada operación cruza internet, gasta cuota y batería, y el token compartido permite leer y escribir todo. A1–A4 siguen sin resolverse. |
| Copia completa de las tablas en el equipo, más una cola con escrituras sin conexión | Obliga a portar unas 20 familias de reglas (cuenta, hojas, cobro, línea de tiempo…). Las escrituras sin conexión regresan parte de los conflictos. Queda como extensión 8.B si el médico necesita cobrar sin la PC. |
| .NET MAUI reutilizando el C# | El médico decidió Kotlin. |
| Una segunda PC como segundo servidor | No se admite. Una segunda PC sería cliente del servidor en una fase futura. |

---

## 3. Contrato de la red local (versión 1)

### 3.1 Formato general

- Rutas y claves en inglés; mensajes para mostrar en español.
- JSON UTF-8 con claves en camelCase.
- **Instantes** (`...Utc`, `serverTimeUtc`): ISO-8601 en UTC con `Z`, tomados del reloj de la PC.
- **Fechas y horas del negocio** (fecha de consulta, cita, corte): `yyyy-MM-dd`, `HH:mm` o `yyyy-MM-ddTHH:mm:ss`, sin zona, siempre en la hora del consultorio (`America/Mexico_City`). Android usa esa zona para el negocio aunque el equipo tenga otra.
- **Dinero:** texto decimal (`"350.00"`) junto con el texto listo para mostrar (`"$350.00"`).
- **Lo que sigue reglas es-MX llega ya formateado por la PC:** edad, insignia de alergias, línea de signos, IMC, totales.
- El cliente ignora los campos que no conoce, lo que permite cambios aditivos.
- El formato de filas de Turso (`TursoClient.FormatArg`) no forma parte de este contrato. Kotlin no lo necesita en v1.

### 3.2 Descubrimiento y vinculación por QR

1. En la PC: *Configuración → Nube y dispositivos → Vincular tablet o celular*. El médico elige el rol: "Tablet (consulta, agenda y cobro)" o "Celular (resumen, corte de caja y expedientes)". La PC muestra un QR durante 10 min con el texto "Esperando a que el equipo lea el código…".
2. **Contenido del QR** (unos 220 caracteres):
   ```
   tpvc://pair?v=1&s=<serverId>&h=192.168.1.20&h=10.0.0.7&n=CONSULTORIO-PC&p=47820
             &k=<SHA-256 del SPKI del certificado, base64url>&c=<código de un uso, 26 caracteres base32 = 128 bits>
             &r=tablet|phone&x=<vence, unix>
   ```
   **El QR no lleva el token de Turso ni datos del consultorio.**
3. El equipo lee el QR y se conecta a la primera dirección que responda. Comprueba que el certificado coincide con `k` y luego llama:
   ```
   POST /api/v1/pair   (sin token)
   { "code", "deviceName", "model", "appVersion", "protocol": 1, "capabilities": [...] }
   ```
4. La PC pregunta "¿Vincular «Tablet Samsung» como Tablet?" con *Vincular* y *Rechazar*. Esto cubre el caso de un QR fotografiado por otra persona dentro de sus 10 minutos.
5. **Respuesta `201`:**
   ```
   { "deviceId" (GUID "N"), "deviceSecret" (32 bytes aleatorios, base64url), "deviceCode" ("T", "C", "T2"…),
     "role", "permissions": {...}, "serverId", "generation", "clinic": { "name", "doctorName" }, "serverTimeUtc" }
   ```
   - La PC guarda solo el SHA-256 del secreto en `PairedDevices`.
   - El `deviceCode` queda reservado para los folios sin conexión (paso 8.B).
   - La pantalla de la PC cambia a "Tablet vinculada: <nombre>".
6. **Reglas del código:**
   - Sirve una sola vez y vence en 10 min.
   - Tras 5 intentos fallidos se invalida.
   - Se aceptan como máximo 5 vinculaciones por minuto.
   - "Generar otro código" emite uno nuevo.
7. **Volver a encontrar la PC, en este orden:**
   1. La última dirección que funcionó.
   2. Las direcciones del QR y la lista que da `/me`.
   3. "Volver a leer el código QR".

   El descubrimiento automático por mDNS queda opcional (8.D). El manual recomienda reservar la IP de la PC. La identidad siempre se prueba con el certificado fijado más el `serverId`, nunca con la dirección.

### 3.3 Autenticación, permisos y revocación

- **Token:** cada llamada, salvo `/hello` y `/pair`, lleva `Authorization: Bearer tpvc1.<deviceId>.<secret>`. La PC compara el SHA-256 en tiempo constante. Los fallos repetidos se contestan con demoras crecientes.
- **Encabezados:**
  - `X-TPVC-Protocol: 1`
  - `X-TPVC-Client: tpvc-android/<versionName>`
  - `X-TPVC-Generation: <generation>`
  - `X-TPVC-Device-Time: <ISO>`: si difiere más de 2 min de la PC, la PC lo anota y el equipo avisa "La hora de la tablet no coincide con la PC".
- **Permisos:**
  - El rol se elige en la PC y vive en `PairedDevices`. Nunca se confía en lo que diga el equipo.
  - Cada ruta declara su permiso de `DevicePermissions`, más dos nuevos: `CanViewClinicalRecords` y `CanShareDocuments`.
  - La PC aplica los permisos. Hoy solo son informativos: en código solo se usa `GetRoleDescription`.
- **Bitácora de accesos** (`LanAccessLog` en `lan.db`): equipo, método, ruta, `SyncId` del paciente si aplica, estado y hora. Se conserva al menos 5 años, el mismo plazo mínimo del expediente según la NOM-004.
- **Origen:** solo se aceptan conexiones desde direcciones privadas (RFC 1918 y link-local).
- **Revocación:** en "Equipos vinculados → Quitar" se marca `RevokedAtUtc`. La siguiente llamada del equipo recibe `401 device_revoked` y la app borra su token, su caché y sus borradores. Si tenía pendientes sin enviar, avisa cuántos se perdieron. Funciona como borrado remoto de un celular perdido.
- **Cambiar rol** manda un aviso por `/events` y la app vuelve a leer `/me`.

### 3.4 Transporte: HTTPS en la red local

**Decisión:**
- HTTPS/1.1 con TLS 1.2 o 1.3, servido por **Kestrel dentro de TPVC.exe** en el puerto TCP 47820 (configurable).
- Certificado autofirmado ECDSA P-256, válido 10 años, creado por la PC la primera vez. Se guarda como PFX en `%LocalAppData%\TPVC\lan\` con su contraseña protegida por DPAPI, igual que el token en `TursoSettingsService`.
- Los clientes confían solo en el SHA-256 del SPKI que traía el QR: sin autoridad certificadora y sin revisar el nombre del equipo (la IP puede cambiar).
- Si se reinstala la PC, el certificado cambia y hay que volver a vincular. Así está diseñado.

| Opción | Veredicto | Por qué |
|---|---|---|
| Kestrel dentro de TPVC.exe con publicación autónoma | **Elegida** | Es una pila HTTP probada: TLS, keep-alive, límites y espera larga. Corre en Linux para las pruebas. Necesita el runtime de ASP.NET Core, y hoy TPVC se publica dependiente del runtime (`--self-contained false` en `scripts/build-installer.ps1`). Por eso la publicación autónoma va antes, sola (8.15). |
| Kestrel dependiente del runtime | No | Tras una actualización, TPVC.exe no abriría en una PC sin el runtime de ASP.NET Core. |
| `HttpListener` | No | Fuera de localhost necesita reservar la URL y enlazar el certificado con `netsh` como administrador. TPVC se instala por usuario, sin elevar. |
| Servidor HTTP propio sobre `SslStream` | No | Interpretar HTTP con código propio es lo más riesgoso para datos de pacientes. |
| HTTP sin TLS | No | El Wi-Fi puede ser el mismo que usan los pacientes, y Android bloquea el texto claro por defecto. |

**Límites del servidor:** 16 KB de encabezados, 4 MB de cuerpo, 30 s de inactividad y 16 conexiones.

### 3.5 Recursos

| Método y ruta | Permiso | Qué hace |
|---|---|---|
| `GET /api/v1/hello` | ninguno | `{serverId, protocol:{min,max}, capabilities[], serverTimeUtc}`. Antes de autenticar no da nada más: ni direcciones ni versión. |
| `POST /api/v1/pair` | código del QR | Vincula el equipo (§3.2). |
| `GET /api/v1/me` | cualquiera | Equipo, rol, permisos, `generation`, `seq` actual, direcciones de la PC, versión de TPVC y hora. |
| `GET /api/v1/events?after=<seq>&wait=25` | cualquiera | Espera larga (§3.6). |
| `GET /api/v1/home` | `CanViewClinicalRecords` | Citas de hoy y los 12 pacientes recientes. |
| `GET /api/v1/patients?q=` | `CanViewClinicalRecords` | `RankPatients` en la PC, máximo 30: nombre, EXP, edad, insignia de alergias, teléfono, última visita. |
| `GET /api/v1/patients/index` | `CanViewClinicalRecords` | Lista compacta (unos 200 bytes por paciente) para buscar sin conexión, con ETag. |
| `GET /api/v1/patients/{syncId}` | `CanViewClinicalRecords` | Ficha lista para mostrar: identidad, `version`, antecedentes, línea de tiempo, últimos signos, evolución, receta activa, hojas con `SyncId` y próxima cita. Con ETag. |
| `GET /api/v1/agenda?from=&to=` | `CanViewClinicalRecords` | Citas y horarios libres (`BuildSlots`). |
| `GET /api/v1/catalog` | `CanManageClinicalRecords` o `CanProcessSales` | Productos con existencias, servicios con sus insumos, plantillas, reglas de dosis pediátrica, CIE-10 y cuota de consulta. Con ETag. |
| `GET /api/v1/reports/dashboard` · `cash-cut?date=` · `alerts` · `suggested-purchase` | `CanViewDashboard` · `CanViewCashCut` · `CanViewStockAlerts` | Los reportes de `InventoryService`, calculados en la PC. |
| `GET /api/v1/inventory/products` · `batches` · `movements?count=` | `CanViewStockAlerts` | Solo lectura. |
| `GET /api/v1/sales?date=` · `/sales/{folio}` | `CanProcessSales` o `CanViewCashCut` | Tickets. |
| `GET /api/v1/clinic/profile` | cualquiera | Datos del consultorio, con ETag. |
| `GET /api/v1/documents/prescription?note=<syncId>` · `?consultation=<folio>` | `CanShareDocuments` | HTML de la receta con el membrete incrustado (`PrescriptionHtmlBuilder`, 8.13), media carta horizontal. |
| `GET /api/v1/documents/ticket/{folio}?format=html\|text` | `CanShareDocuments` | Ticket en HTML o en texto de 42 columnas. |
| `POST /api/v1/patients` | `CanManageClinicalRecords` | Alta. La PC asigna el EXP. |
| `PATCH /api/v1/patients/{syncId}` | `CanManageClinicalRecords` | `{changes:[{field, before, after}]}` (§3.7). |
| `POST /api/v1/appointments` · `PATCH /api/v1/appointments/{syncId}` | `CanManageClinicalRecords` (el celular, según la pregunta 4) | Agendar, mover (vuelve a "Programada") o cambiar de estado. |
| `POST /api/v1/sales` | `CanProcessSales` | Venta de mostrador. Responde `{folio, total, change, documents}`. |
| Consulta | §3.8 | |
| Consulta entre equipos | §3.9 | |

### 3.6 Avisos: espera larga

`GET /api/v1/events?after=18440&wait=25` contesta en cuanto hay algo nuevo, o a los 25 s vacío:

```json
{
  "generation": "3f2a…",
  "seq": 18452,
  "hasMore": false,
  "serverTimeUtc": "2026-10-09T16:42:01.123Z",
  "changes": [
    { "seq": 18451, "topic": "patients", "syncId": "8c1d…", "deleted": false },
    { "seq": 18452, "topic": "appointments", "syncId": "a07e…", "deleted": false }
  ],
  "handoffs": [ { "id": "…", "folio": "CNS-20261009-104210", "patientName": "…", "from": "PC", "state": "enviada" } ],
  "consultations": [],
  "device": null
}
```

- **Temas:** `patients` (incluye notas), `appointments`, `inventory`, `sales`, `catalog` y `profile`. El perfil vive en un archivo JSON, así que el aviso lo emite `ClinicProfileService` al guardar.
- **Uso en el equipo:** marca como vencido lo que tiene guardado y vuelve a pedir lo que está en pantalla.
- **Arranque:** el cursor inicial es el `seq` que da `/me`. No hace falta descargar todo.
- **Cursor viejo:** `410 cursor_expired` cuando ya se depuraron borrados de más de 90 días. El equipo borra su caché y toma el `seq` actual.
- **Generación distinta:** `409 generation_changed`.
- Se elige espera larga en lugar de SSE porque cada respuesta es una petición completa. Eso facilita grabarla como ejemplo del contrato, poner tiempos de espera en Android y reintentar.
- **Solo corre con la app al frente.** Los avisos de consulta enviada solo llegan así, y la app lo dice.

### 3.7 Reglas

**Idempotencia, con Ids generados en el equipo**
- Todo `POST`, `PATCH` y `PUT` lleva `Idempotency-Key: <UUID v4 generado en el equipo>`. El comando queda en la cola hasta recibir una respuesta definitiva.
- Las altas llevan el `syncId` del registro nuevo, generado en el equipo. La PC lo usa como `SyncId`. Así la tablet puede referirse al registro antes de tener respuesta, y un reintento no duplica.
- La PC guarda `(deviceId, Idempotency-Key)` en `LanRequestLog`, con el hash del cuerpo y la respuesta, **en la misma transacción que el efecto**. Por eso terminar la consulta pasa a ser una sola transacción (8.12).
  - Misma clave y mismo cuerpo: devuelve la respuesta guardada, sin una segunda venta.
  - Misma clave con otro cuerpo: `422 idempotency_mismatch`.
- El registro se depura a los 7 días. Pasados 6 días, el equipo no reintenta un comando por su cuenta: lo deja en "Pendientes con problema".
- La PC aplica los comandos de cada equipo de uno en uno, en orden.
- Un comando creado bajo otra generación recibe `409 generation_changed`. El equipo nunca lo reenvía solo: muestra "La PC restauró un respaldo: revise estos pendientes".

**Concurrencia**
- **Paciente:** comparar y poner por campo (`PatientPatch`, 8.9). Cada `{field, before, after}` se aplica solo si el valor actual es `before`. Si ya es `after`, no hace nada. Si no es ninguno de los dos, es conflicto. Las hojas JSON se combinan por renglón usando su `SyncId`.
- **Citas:** `If-Match: <version>`.
- **Consulta:** tiene un solo dueño, así que no hay combinación.

**Versiones**
- Va en la ruta: `/api/v1/`.
- Dentro de v1 solo hay cambios aditivos: campos opcionales nuevos, comandos nuevos anunciados en `capabilities` y columnas nuevas.
- Un cambio incompatible abre `/api/v2/`, que convive con v1 durante al menos dos versiones de TPVC.
- Un cliente viejo recibe `426 protocol_unsupported`: "Actualice la app de TPVC en este equipo".
- La versión de la app Android es independiente. La compatibilidad de la consulta entre equipos se decide por la capacidad `handoff.v1`, no por `AppVersion ≥ 1.17.0`, que queda solo para el camino de Turso entre dos PC.

**Errores** (`application/problem+json`: `{type, title, status, detail?, errors?:[{field,message}], data?}`; `title` viene en español, listo para mostrar)

| Código | Estado | Cuándo |
|---|---|---|
| `device_revoked` | 401 | Equipo quitado. |
| `forbidden_role` | 403 | Le falta el permiso. |
| `validation` | 422 | Errores por campo con los mensajes de hoy (`PatientFormRules`). |
| `idempotency_mismatch` | 422 | Misma clave con otro cuerpo. |
| `patient_changed` | 409 | Conflicto de campo (`data.conflicts:[{field, yours, current}]`). |
| `appointment_overlap` | 409 | Horario ocupado. Se puede reenviar con `allowOverlap: true`. |
| `possible_duplicate` | 409 | Paciente parecido (`data.candidates`). Se puede reenviar con `confirmedNotDuplicate: true`. |
| `consultation_open_elsewhere` | 409 | El paciente ya tiene una consulta abierta (`data.owner`). |
| `consultation_taken_over` | 409 | La PC tomó la consulta con "Continuar aquí". |
| `stock_insufficient` | 409 | "No hay existencias suficientes de Paracetamol (quedan 2)". |
| `generation_changed` | 409 | La PC restauró un respaldo. |
| `cursor_expired` | 410 | Cursor de avisos demasiado viejo. |
| `protocol_unsupported` | 426 | App vieja. |
| `server_busy` | 503 | Reintentar según `Retry-After`. |

### 3.8 Consulta desde la tablet

La consulta vive en la PC, con su borrador en `%LocalAppData%\TPVC\equipos\<deviceId>\consulta-en-curso.json`, en el mismo formato `ConsultationDraft` del guardado automático y del envío entre equipos. La tablet tiene su propio borrador para el texto que la PC todavía no confirma.

| Ruta | Qué hace |
|---|---|
| `POST /consultations` `{patientSyncId, appointmentSyncId?}` | La PC asigna el folio CNS con la regla de `ActiveConsultation`, sin repetir el de otra consulta abierta. Devuelve `201 {folio, snapshot}`. Si el paciente ya tiene consulta abierta: `409 consultation_open_elsewhere`. |
| `GET /consultations/mine` | Para retomar si Android cerró la app. |
| `PUT /consultations/{folio}/draft` `{clientSeq, fields{…}}` | Texto (motivo, interrogatorio, exploración, diagnóstico, plan, notas, indicaciones), signos y sección. Solo se aplica un `clientSeq` mayor. Se manda tras la pausa del guardado automático (1.5 s, o como máximo cada 10 s, como `AutosaveOptions`). Devuelve `{ackSeq, derived:{imcText, vitalWarnings[]}}`. |
| `POST /consultations/{folio}/commands` `{command, confirm?}` | Comandos: `prescription.add/update/remove/setDispense/repeatPrevious`, `supply.add/setUnits/remove`, `service.add/setUnits/remove`, `account.setQuantity/remove` y `dose.calculate`. Devuelve `{snapshot, notices[], needsConfirmation?}`. |
| `POST /consultations/{folio}/finish` `{payment:{method, cashReceived}}` | `ConsultationFinishService` en una transacción: nota, resumen del paciente, hojas, venta con FEFO y cita "Atendida". Devuelve `{noteFolio, ticketFolio?, receipt, documents}`. Con la cuenta vacía cierra como cortesía, igual que en la PC. |
| `POST /consultations/{folio}/discard` `{confirm:true}` | Descarta la consulta. |

- **Confirmaciones.** Por ejemplo, una alergia o repetir la receta anterior. El primer intento contesta "No", sin ningún efecto, y devuelve `needsConfirmation {title, message}`. La tablet pregunta al médico con "Sí" y "No", y si es "Sí" reenvía con `confirm: true`.
- **`ConsultationSnapshot`:**
  - `folio`, `version`, `owner` y `startedAt`.
  - Paciente: nombre, edad, insignia de alergias y alergias.
  - `fields` y `derived`.
  - Renglones de receta con `dispenseInClinic` y `pending`, y la receta anterior.
  - Insumos y servicios, marcando los de una consulta anterior.
  - Cuenta: cuota, renglones, total y total en texto.
  - `canFinish` y `reasons`.
- **Contrato y motor van separados.** El contrato no copia el interior de los ViewModels. El motor que lo cumple en la PC se decide en 8.21 con criterios explícitos, y Kotlin no cambia con esa decisión.

### 3.9 Consulta entre equipos sobre la red

Se conservan los estados y reglas de `docs/TURSO_SYNC_ARCHITECTURE.md` §4.5: `enviada`, `aceptada` y `cancelada`; `AcceptToken` para un solo ganador; recuperar con cerca; la copia se borra solo después de guardar. Ahora viven en `LanHandoffs` (`lan.db`) y la PC decide en su propio proceso, con un candado. Entre la PC y la tablet la copia de trabajo no viaja por la red, porque las dos consultas están en la PC: solo cambia el dueño.

| Ruta | Qué hace |
|---|---|
| `POST /consultations/{folio}/handoff` `{to:"pc"}` | La tablet manda su consulta a la PC y queda en solo lectura. |
| `GET /handoffs/incoming` | Lo que le ofrecieron a este equipo. Lo mismo llega en `events.handoffs`. |
| `POST /handoffs/{id}/accept` `{acceptToken}` | La tablet acepta una consulta que le mandó la PC. Devuelve `{won: true\|false}`. |
| `GET /handoffs/{id}` | Estado, y si ganó la ficha de este equipo. Sirve cuando se perdió la respuesta de aceptar. |
| `POST /handoffs/{id}/recover` | Quien la mandó la recupera antes de que la acepten, con la misma instrucción con cerca de hoy. |

- **De la PC a la tablet:**
  1. "Enviar a…" lista los equipos vinculados en línea que tienen `handoff.v1`.
  2. La PC arma el borrador con `HandoffPayloadCodec` (8.13) y su consulta queda en solo lectura, como hoy.
  3. La tablet ve el aviso "La PC te envió la consulta de … · Continuar aquí".
  4. Al aceptar, la PC crea la consulta de la tablet con ese borrador y cierra la suya sin descartarla.
- **De la tablet a la PC:** la PC muestra el aviso y, con "Continuar aquí", la carga con el mismo mapeo que usa hoy (`MapIncomingDraftAsync`, que pasa a `HandoffPayloadCodec`).
- **Si la tablet se apaga a media consulta:**
  - La PC muestra el aviso "Consulta en la tablet sin conexión desde 10:42 · Continuar aquí", con el borrador hasta el último envío confirmado.
  - Si la tablet regresa después, recibe `409 consultation_taken_over` y muestra el texto que no alcanzó a enviar, para copiarlo.
- El camino por Turso (`ConsultationHandoffs`) queda solo en el modo `SharedPcs`.

### 3.10 Qué hace cada equipo cuando la PC no responde

La tablet reintenta con espera creciente de 2 a 60 s, y otra vez cada vez que cambia el Wi-Fi.

| Situación | Tablet | Celular |
|---|---|---|
| El Wi-Fi se cae unos segundos | Sigue escribiendo la consulta. Al volver manda un solo `PUT` con todo lo que la PC no había confirmado, después los comandos en orden y después refresca. | Muestra lo último que tiene, con su hora. |
| La PC dormida o TPVC cerrado | Aviso "Sin conexión con la PC · datos de las 10:42". Lee lo guardado. En la consulta abierta se pueden escribir texto y signos, y agregar renglones de receta, que quedan como "pendiente de revisar alergias y existencias". Cuenta, insumos, servicios, "Terminar consulta", alta, citas y ventas se desactivan con el motivo "Se necesita la PC". | Solo lectura de lo guardado. |
| Fuera del consultorio | Igual que la fila anterior. Cobrar sin la PC solo existe si se construye 8.B (pregunta 1). | Últimas cifras guardadas. En vivo solo con 8.A (pregunta 2). |
| La PC descompuesta | Lectura de lo guardado. En la PC nueva: "Recuperar desde la nube" y volver a vincular. | Igual. |
| Equipo quitado | Borra token, caché y borradores. | Igual. |
| Sin ver la PC 30 días | Borra la caché. | Igual. |

La app de la tablet **nunca** hace esto sin la PC en v1: dar de alta pacientes, iniciar consultas, vender, cobrar, cancelar tickets, agendar o generar una receta nueva. Sí puede volver a compartir los PDF ya generados.

---

## 4. Cómo quedan los bloqueantes y los demás riesgos

| # | Riesgo | Solución en esta arquitectura | Paso |
|---|---|---|---|
| A1 | **Existencias como valores.** Con dos equipos que venden, gana la última escritura y se pierde un descuento. Los lotes no se pueden reconstruir con los movimientos de hoy. | Solo la PC toca existencias (`RegisterSaleAsync`, FEFO, `EnsureStock` contra el stock real). Turso solo guarda una copia. Ya no hace falta reconstruir desde movimientos ni sembrar saldos iniciales. | 8.12, 8.20 · opcional 8.C (un movimiento por lote, para un Kardex exacto) |
| A2 | **Siembra.** Cada instalación siembra categorías y SERV-001..005 con `SyncId` distintos. Además, `App.xaml.cs` pasa `seedDemoData: !isFirstRun` y siembra los pacientes demo en cualquier arranque posterior con la tabla vacía. | Kotlin no tiene base de negocio, así que no siembra. En la PC: pacientes demo solo en modo demo explícito, `SyncId` fijos (UUIDv5) para el catálogo sembrado en bases nuevas, y "Recuperar desde la nube" sin sembrar. | 8.4, 8.7 |
| A3 | **Folios por equipo.** EXP y TKT son consecutivos por equipo; CNS usa la hora (`ActiveConsultation`). El `-R1` solo se agrega en el equipo que recibe. | La PC asigna EXP, TKT y CNS dentro del comando. Nunca hay `-R`. El `deviceCode` queda reservado para folios sin conexión, solo si se construye 8.B. | 8.20, 8.21 · 8.B |
| A4 | **Id locales dentro del JSON** (`ChargedLineId`, `ServiceId`). Además, un error latente hoy entre dos PC: tras recibir una consulta, + / − o quitar en las hojas busca el renglón con el Id del otro equipo. | `ChargedLineSyncId` y `ServiceSyncId` en cada renglón; los lectores los prefieren; la recepción traduce también las hojas. La API solo habla en `SyncId`. | 8.8 |
| A5 | **No hay sincronización periódica.** | Avisos por espera larga en menos de 1 s. Turso sale del camino en vivo. En modo respaldo se quitan la revisión cada 4 s y el latido. | 8.2, 8.19 |
| H1 | **Se guarda la fila completa del paciente.** `SavePatientAsync` usa `SetValues`, y `CanReplaceSelectedPatientInstance` conserva copias viejas en memoria. | `PatientPatch` campo por campo, en la PC y en la API. Las hojas se combinan por renglón. | 8.9 |
| H2 | **Citas empalmadas.** | Revisión dentro de la transacción de `SaveAppointmentAsync`: la PC pregunta y la API responde `409 appointment_overlap`. | 8.10 |
| H3 | **Pacientes duplicados.** | `FindPossibleDuplicatesAsync` (nombre normalizado y fecha de nacimiento): la PC avisa y la API responde `409 possible_duplicate`. | 8.10 |
| H4 | **Borrados contra ediciones y descartes silenciosos.** | Un solo escritor. Los triggers registran también los borrados en cascada (`SaleItems` al borrar una `Sale`, comprobado con sqlite3). El cursor del respaldo no avanza sobre una fila que falla. | 8.5, 8.6 |
| H5 | **Relojes.** Además, si el reloj de la PC se atrasa después de sincronizar, lo que se guarde antes de la siguiente sincronización queda con una marca anterior al cursor `LastPushedLocalUtc` y no sube nunca (esa sincronización mueve el cursor a la hora atrasada). | Solo cuenta el reloj de la PC. El cursor pasa a ser `Seq`, que no depende del reloj. Se avisa si el equipo difiere más de 2 min. | 8.6, 8.20 |
| H6 | **Restaurar con Turso activo.** Hoy la nube conserva las filas más nuevas que el respaldo y la descarga normal omite las que escribió esta misma PC: las dos copias quedan distintas, y una «Sincronización completa» regresaría esas filas y desharía la restauración. Lo que escribió otra PC sí regresa en la siguiente descarga. | Generación nueva, opción de reemplazar la copia de la nube y nunca descargar en modo respaldo. | 8.7 |
| H7 | **Borradores solo locales.** Si el equipo que mandó la consulta muere, la marca de "enviada" queda atorada. | El borrador de la tablet se refleja en la PC; el estado del envío vive en la PC; "Continuar aquí". | 8.22, 8.23 |
| H8 | Preferencias locales. | Sin cambio: son de cada equipo. | — |
| H9 | **El membrete no se sincroniza.** | La PC genera el HTML de la receta con las imágenes incrustadas (`ClinicProfileService` ya las incrusta). | 8.19 · opcional 8.E |
| H10 | **El paquete de vinculación lleva el token completo de Turso en claro, y los roles no se aplican.** | Secreto por equipo y revocable. El token de Turso nunca sale de la PC. Permisos en la PC, confirmación en la PC y bitácora de accesos. Se retira `GenerateAndroidPairingPayload`. | 8.17 |
| H11 | `EnsureStock` e `IsRecordNumberTakenAsync` solo revisan los datos del propio equipo. | Con una sola base, las revisiones son correctas. | 8.20 |
| — | La subida omite, con solo un aviso, la fila cuya referencia obligatoria no tiene `SyncId`, y nunca la reintenta. | El cursor se detiene y la pantalla dice "N cambios sin respaldar". | 8.6 |
| — | En la recuperación desde la nube, una fila sin su principal se pierde. | Queda estacionada y se reintenta. | 8.7 |
| — | El comentario de `TursoClient.ExecuteBatchAsync` dice "transaccional" y no lo es. | Se corrige el comentario. La subida es idempotente. | 8.2 |
| — | Los límites del plan gratuito están viejos (`TursoQuotaSummary`, `TursoSyncViewModel.RefreshQuotaDisplay`). | 500M lecturas, 10M escrituras, 5 GB. | 8.2 |
| — | La compatibilidad depende de `AppVersion ≥ 1.17.0`. | Capacidad `handoff.v1` para los equipos de la red. | 8.23 |
| N1 | **La PC duerme o TPVC se cierra.** | `SetThreadExecutionState`, aviso al cerrar, "Abrir TPVC al iniciar Windows" y guía de energía. Una laptop con la tapa cerrada o en Modern Standby puede soltar el Wi-Fi igual: la tablet lo explica ("La PC está dormida o cerrada"). | 8.18 |
| N2 | **Firewall, red marcada como Pública o Wi-Fi con aislamiento de clientes.** | Botón "Permitir en el firewall" (una sola elevación), aviso de red Pública, "Probar conexión" y guía. El aislamiento no se arregla con código: se apaga en el módem o la PC va por cable. | 8.18 |
| N3 | **Cambia la IP de la PC.** | Orden de búsqueda de §3.2 y reservar la IP. | 8.17 · 8.D |
| N4 | **Avisos de `InventoryService` desde hilos del servidor.** Las pantallas de la PC suponen el hilo de la UI. | Despachador inyectable. | 8.11 |
| N5 | **Escrituras del servidor y de la UI al mismo tiempo.** | WAL, `busy_timeout`, `SqlitePoolOpenGuard` (se queda) y prueba de estrés. | 8.20 |
| N6 | **Kestrel necesita ASP.NET Core.** | La publicación autónoma va primero y sola. | 8.15 |
| N7 | **Se pierde la respuesta de terminar o vender.** | Clave de idempotencia guardada en la misma transacción que el efecto. | 8.12, 8.20 |
| N8 | **Equipo perdido con datos de pacientes.** | Caché cifrada, bloqueo con huella o PIN, bloqueo de pantalla obligatorio, `allowBackup=false`, borrado al quitarlo o tras 30 días. | 8.17, §6 |
| N9 | **App y PC con versiones distintas.** | `/api/v1`, capacidades y `426`. | 8.16 |
| N10 | **El motor de la consulta sin pantalla puede ser frágil.** | Prueba de concepto con criterio de sí o no, con un motor extraído como plan B. | 8.21 |
| N11 | **Restaurar y los datos de vinculación.** | `lan.db` aparte. Al restaurar: las vinculaciones se quedan, la generación cambia y se cancelan los envíos y consultas de la tablet que ya no coinciden, mostrándolos al médico. | 8.7, 8.16 |

---

## 5. Pasos en la PC

Cada paso es una versión publicable (vX.Y.Z), deja la PC funcionando sola y trae sus pruebas. Las pruebas corren en Linux salvo las marcadas [W], que necesitan la CI de Windows o capturas. Del 8.16 en adelante todo queda detrás del interruptor "Conectar tablet y celular por la red del consultorio", apagado por defecto. La Etapa 1 vale la pena aunque nunca exista la app: menos Turso, respaldo correcto, guardados sin pisarse y el error de A4 corregido.

### Etapa 1 · La PC sola, más segura y con Turso mínimo

#### 8.1 · Documentación
- **Cambia:** este plan y las correcciones de §7 en los cuatro documentos. No toca código.
- **Prueba:** la CI de Windows sigue verde.
- **Riesgo:** ninguno.

#### 8.2 · Turso solo como respaldo
- **Cambia:**
  - `TursoConfig.CloudMode` (`Backup` / `SharedPcs`) con la regla de migración de §2.3.
  - En `Backup`:
    - `ConsultationHandoffViewModel` no arranca su revisión ni el latido.
    - "Enviar a…" oculta los equipos de la nube.
    - `RegisterDeviceAsync` corre una vez por arranque.
    - Subida agrupada con `TimeProvider` en `TursoSyncService`: el primer cambio arma 2 min, luego como máximo una subida cada 5 min, y una al cerrar con tope de 5 s.
    - No hay descarga.
  - Ajuste en pantalla: "Uso de la nube: Solo respaldo (recomendado) / Compartir entre computadoras".
  - Límites correctos en `TursoQuotaSummary` (`Models/TursoConfig.cs`) y en `TursoSyncViewModel.RefreshQuotaDisplay`.
  - Se corrige el comentario de `ExecuteBatchAsync`.
- **Prueba:**
  - Con `FakeHranaServer.RequestCount`:
    - 10 min simulados en Pacientes en modo `Backup`: cero lecturas de `ConsultationHandoffs` y cero latidos.
    - 50 cambios en 10 min: 3 subidas o menos, y ninguna descarga.
  - En `SharedPcs`, `ConsultationHandoffTests` y `TursoSyncEndToEndTests` pasan sin cambios.
  - Prueba de la regla de migración.
- **Riesgo:** bajo. Baja de ~8.7k a ~100 peticiones al día. Quien tenga dos PC queda en `SharedPcs`.

#### 8.3 · CI de Linux
- **Cambia:**
  - `[TestCategory("Windows")]` en las pruebas que crean tipos de WPF o necesitan DPAPI real (`UiSmokeTests` y las que fallen en Linux).
  - Job `ubuntu-latest` en `ci.yml` con `dotnet test --filter "TestCategory!=Windows&TestCategory!=Rendimiento"`, dos veces: en UTC y con `TZ=America/Mexico_City`. Se puede porque el proyecto de pruebas ya compila en Linux con `EnableWindowsTargeting` (`Directory.Build.props`).
  - La CI de Windows sigue mandando para WPF.
- **Prueba:** el job nuevo pasa y el de Windows no cambia.
- **Riesgo:** bajo. Alguna prueba que hoy pase en Linux por casualidad se marca cuando aparezca.

#### 8.4 · Siembra segura (A2)
- **Cambia:**
  - Pacientes demo solo en un modo demo explícito, nunca con la nube o el servidor configurados (`App.xaml.cs`, `DatabaseInitializer.Initialize`).
  - En bases nuevas, `SyncId` fijos para las 5 categorías y SERV-001..005 (UUIDv5 de `tpvc:category:<nombre>` y `tpvc:service:<código>`).
  - Bandera `RestoreFromCloudPending`: con ella no se siembra nada (la usa 8.7).
- **Prueba:**
  - Inicializar dos veces con la tabla de pacientes vacía: no aparecen EXP-2026-001..004.
  - Dos bases nuevas tienen los mismos `SyncId` de catálogo.
  - Con la bandera, la base queda vacía.
- **Riesgo:** bajo. Las bases existentes conservan sus `SyncId`; cambiarlos rompería la copia de Turso.

#### 8.5 · Registro de cambios en la base
- **Cambia:**
  - `SyncChangeLog(Seq INTEGER PRIMARY KEY AUTOINCREMENT, EntityType, SyncId, Deleted, UNIQUE(EntityType, SyncId))` con triggers `AFTER INSERT/UPDATE/DELETE` en las 13 tablas `ISyncEntity`. Se generan desde la misma lista que usa `TursoSyncService`. `INSERT OR REPLACE` deja un renglón por registro, con un `Seq` nuevo en cada cambio.
  - `DbMeta(Generation)`.
  - `EnsureSchema` crea los triggers y llena el registro si falta, también en un respaldo viejo restaurado, y los incluye en la huella de `PRAGMA user_version`.
  - Se declara `HasTrigger` (o `UseSqlReturningClause(false)`) en esas entidades para que EF Core no use `RETURNING` en tablas con triggers.
  - Antes de crear los triggers se toma un respaldo con `BackupService`.
- **Prueba:**
  - Insertar, editar y borrar dan un `Seq` creciente con un renglón por registro.
  - Borrar una `Sale` registra sus `SaleItems`.
  - Los `SaveChanges` de EF siguen funcionando.
  - Un respaldo anterior a 8.5 queda con triggers y con el registro completo.
- **Riesgo:** medio, porque toca el esquema. Va solo.

#### 8.6 · Respaldo a Turso por secuencia
- **Cambia:**
  - Cursor `LastBackedUpSeq` en lugar de `LastPushedLocalUtc`. Sube la fila actual, o su borrado si el registro dice `Deleted`.
  - La primera vez sube todo una vez; las inserciones son idempotentes.
  - Una fila que no se puede subir detiene el cursor y muestra "N cambios sin respaldar".
  - "Verificar copia en la nube": `COUNT(*)` por tabla, local contra remota, en una sola petición. Corre a pedido y después de restaurar, no cada semana.
- **Prueba (FakeHranaServer):**
  - Con el reloj atrasado después de subir, lo nuevo sí sube.
  - Un borrado en cascada llega a la nube.
  - El cursor no pasa una fila que falla.
  - Un día simulado (25 pacientes, ~300 cambios, 10 h) queda en menos de 300 peticiones y menos de 1,000 filas escritas.
- **Riesgo:** medio. La primera subida reescribe todo una vez (~0.2% del mes).

#### 8.7 · Restaurar y recuperar
- **Cambia:**
  - Toda restauración (`DataImportService`) pone una `Generation` nueva y reconstruye el registro de cambios.
  - Con la nube activa, pregunta "Reemplazar la copia de la nube con este respaldo": borra y sube completo. Si no hay red, queda pendiente con `TursoNeedsRebase`.
  - "Recuperar desde la nube" en una base vacía:
    1. Usa la bandera de 8.4, así que no siembra nada.
    2. Descarga en orden de dependencias.
    3. Estaciona y reintenta las filas sin su principal.
    4. Pone una generación nueva.
  - Cada reescritura de una sola vez lleva respaldo previo, una bandera idempotente y su renglón en la bitácora.
- **Prueba (FakeHranaServer):**
  - Restaurar y reemplazar deja la copia remota igual a la local, sin las filas posteriores al respaldo.
  - Recuperar desde la nube da la base original: mismos conteos, un solo SERV-001 y ningún paciente demo.
- **Riesgo:** medio. Reemplazar la copia de la nube borra datos remotos, así que la confirmación debe ser clara.

#### 8.8 · Ids globales en las hojas del paciente (A4)
- **Cambia:**
  - `PatientSupplyItem.ChargedLineSyncId` y `PatientServiceItem.ServiceSyncId`, junto a los campos actuales.
  - Los lectores prefieren el `SyncId`; si no hay, usan el Id y luego el nombre.
  - Los JSON existentes se completan al arrancar, con respaldo previo.
  - La recepción de una consulta traduce también las hojas, lo que corrige el error latente en `SuppliesSheetViewModel` y `ConsultationAccount`.
- **Prueba:**
  - Dos `Fase6Clinic` con Id distintos: recibir una consulta y usar + / − y quitar cambia el renglón correcto de la cuenta.
  - Vector `synced-json-columns.json`.
- **Riesgo:** medio, porque es un formato que viaja. Los campos son aditivos.

#### 8.9 · Guardar solo lo que cambió (H1)
- **Cambia:**
  - `PatientPatch` puro (§3.7) y `InventoryService.SavePatientChangesAsync`, que aplica en una transacción y devuelve los conflictos.
  - `SavePatientAsync` deja de copiar el objeto completo.
  - Los guardados de la PC usan la foto tomada al cargar (`PatientSession`): alta y edición (`PatientFormViewModel`), hojas y resumen de la nota.
  - Si hay conflicto: "El expediente cambió en otro equipo; se recargó …".
- **Prueba:**
  - Una hoja guardada con una copia vieja ya no borra lo que escribió `SaveAppointmentAsync` en `NextAppointment*`.
  - Dos cambios al mismo campo dan conflicto.
  - Parches en campos distintos conmutan (prueba de propiedades).
  - `PacientesTests` pasan.
- **Riesgo:** alto, porque es guardar el paciente. Va solo.

#### 8.10 · Agenda sin empalmes y aviso de duplicados (H2, H3)
- **Cambia:**
  - `SaveAppointmentAsync` revisa empalmes, sin contar las canceladas ni la misma cita, y lanza `AppointmentOverlapException`. La agenda pregunta: "Ya hay una cita a las 10:30 con … ¿Agendar de todos modos?".
  - `FindPossibleDuplicatesAsync`. El alta avisa: "Ya existe un expediente parecido: … (EXP-…)".
- **Prueba:**
  - Empalme, citas seguidas, cancelada ignorada y mover la misma cita.
  - Duplicado con y sin acentos.
  - [W] Captura de los avisos.
- **Riesgo:** bajo.

#### 8.11 · Avisos de cambios por despachador
- **Cambia:**
  - `InventoryService.Raise` pasa por un `IChangeEventDispatcher` inyectable: `Dispatcher.BeginInvoke` en WPF y en el mismo hilo en las pruebas.
  - `TursoSyncService.RunOnUiThread` usa el mismo.
- **Prueba:** un aviso lanzado desde un hilo de fondo llega por el despachador falso.
- **Riesgo:** bajo.

#### 8.12 · Terminar la consulta en una sola transacción
- **Cambia:**
  - `ConsultationFinishService.FinishAsync(ConsultationFinishRequest)`, sin WPF, guarda en un solo `SaveChanges`:
    - la nota;
    - el parche del resumen del paciente;
    - las hojas;
    - la venta (el núcleo de `RegisterSaleAsync`, recibiendo un `AppDbContext` existente), con FEFO y movimientos;
    - la cita "Atendida".
  - La PC lo usa en lugar de los pasos de hoy (`ConsultationViewModel`, `CheckoutViewModel`, `PatientsWorkspace.Flows`).
  - Un ámbito de comando opcional agrega el renglón de `LanRequestLog` en la misma transacción.
- **Prueba:**
  - Los escenarios de `ConsultationWorkspaceTests` y de Fase 6 no cambian.
  - Con una falla inyectada antes de confirmar, no queda nada guardado.
  - Un solo ticket por consulta.
  - [W] Las capturas quedan iguales.
- **Riesgo:** alto, porque es el flujo más sensible. Va solo y se usa en la PC un tiempo antes de que la tablet dependa de él.

#### 8.13 · Generadores puros y reloj inyectable (puede ir en dos versiones)
- **Cambia:**
  - `Services/Documents/PrescriptionHtmlBuilder`, que sale de `PrintingService.BuildPrescriptionHtml`.
  - `TicketText` (42 columnas) y `TicketHtml`, a partir de `SaleReceipt`. Muestran "Recibido" y "Cambio", como el ticket en pantalla.
  - `VitalSignsRules`: aviso de signos (hoy la propiedad `VitalSignsWarning`) e IMC.
  - `PatientFormRules`: validación con los mensajes de hoy, `ParseFullName`, `TryParseBirthDate` e `IsValidEmail`.
  - `HandoffPayloadCodec`: armar, traducir y `FindCatalogRef`, que salen de `ConsultationHandoffViewModel`.
  - `TimeProvider` donde hoy se lee la fecha directamente: edad, `IsUpcoming`, `DaysRemaining` y resumen.
- **Prueba:**
  - Salida idéntica a la de hoy (HTML de la receta y ticket impreso).
  - `ConsultationHandoffTests`, `PatientsWorkspaceReuseTests` y `handoff-payload.golden.json` sin cambios.
  - Prueba de arquitectura: `Services/Documents` sin WPF.
- **Riesgo:** bajo, porque solo mueve código.

#### 8.14 · Vectores compartidos para Kotlin
- **Cambia:**
  - Carpeta `contracts/golden/` en la raíz. Cada archivo tiene la forma `{rule, culture: "es-MX", today, now, cases:[{name, input, expected}]}`.
  - `GoldenVectorTests` reescribe los archivos con `TPVC_WRITE_GOLDEN=1`; si no, compara contra ellos.
  - Los que usa Kotlin en v1:
    - `text-search`
    - `rank-patients`
    - `rank-products` (con el prefijo `3*para`)
    - `vitals` (`FormatTA`, texto del IMC, avisos y línea de signos)
    - `date-mask`
  - Los que protegen lo que sirve la PC:
    - `prescription-html-fields` (valores `data-field`)
    - `ticket-text`
    - `patient-form-validation`
    - `synced-json-columns`
    - `handoff-payload` (se mueve desde `TestAssets/fase6`)
  - Reglas para escribir los vectores:
    - Todo redondeo lleva un caso de punto medio, porque .NET usa redondeo bancario: 5.625 da 5.6.
    - Los desempates llevan nombres con acentos.
    - Se generan en UTC y en `America/Mexico_City`.
- **Prueba:** cambiar una regla sin regenerar los archivos rompe la CI.
- **Riesgo:** bajo.

### Etapa 2 · Servidor del consultorio (apagado por defecto)

#### 8.15 · Publicación autónoma
- **Cambia:**
  - `--self-contained true` (win-x64) en `scripts/build-installer.ps1` y `.sh`, `release.yml` y `perf-compare.yml`. `TPVC-Setup.exe` no cambia.
  - Todavía no escucha nada.
  - Se mide el tamaño de la descarga.
- **Prueba:**
  - `InstallerTests`.
  - Revisión en la CI de que el paquete trae el runtime.
  - [W] Actualizar una instalación v1.19 con `AppUpdateService` y abrirla.
- **Riesgo:** medio, porque la descarga crece (decenas de MB; se medirá). Plan B documentado: que el instalador revise `Microsoft.AspNetCore.App` antes de actualizar.

#### 8.16 · Servidor base
- **Cambia:**
  - `FrameworkReference Microsoft.AspNetCore.App`.
  - `Services/Lan/LanServerHost` (Kestrel), `LanCertificateStore`, `lan.db` con `LanDbContext`, y `lan.json` con el interruptor (apagado significa que nada escucha).
  - Filtro de origen privado y límites de §3.4.
  - Solo `GET /api/v1/hello`.
- **Prueba:**
  - Kestrel en `127.0.0.1:0` con TLS real: un `HttpClient` con el pin correcto responde y uno con otro pin falla.
  - El certificado es el mismo tras reiniciar.
  - Un origen público (simulado) se rechaza.
  - Prueba de arquitectura: `Services/Lan` sin WPF (se amplía `PatientsArchitectureTests`).
  - [W] SChannel acepta el PFX ECDSA con TLS 1.2 y 1.3.
- **Riesgo:** medio, por TLS en Windows. Se prueba temprano.

#### 8.17 · Vincular por QR y equipos vinculados
- **Cambia:**
  - `LanPairingService` y el QR con QRCoder (licencia MIT).
  - Confirmación en la PC.
  - `POST /api/v1/pair`.
  - Autenticación por token y filtro de permisos por ruta.
  - `DevicePermissions` con `CanViewClinicalRecords` y `CanShareDocuments`, con la matriz confirmada con el médico (pregunta 4).
  - `LanAccessLog`.
  - Lista "Equipos vinculados": en línea o visto hace…, Quitar, Cambiar rol.
  - Se retira el modal del paquete viejo (`GenerateAndroidPairingPayload`, `OpenPairingModal`).
  - Capítulo nuevo del manual.
- **Prueba:**
  - Vinculación completa por loopback.
  - Código vencido, reusado o equivocado, y bloqueo tras 5 fallas.
  - Equipo quitado recibe `401 device_revoked`; sin permiso, `403`.
  - Prueba por reflexión: toda ruta declara su permiso.
  - Vectores `pairing-uri.json` y `permissions.json`.
  - [W] Capturas del modal y de la lista en 4 temas.
- **Riesgo:** medio, porque es seguridad.

#### 8.18 · Integración con Windows
- **Cambia:** `ILanPlatform`, con falsos para las pruebas, para:
  - "Permitir en el firewall de Windows": una sola llamada elevada a `netsh advfirewall firewall add rule … profile=private remoteip=localsubnet program=…\TPVC.exe`.
  - Aviso si la red está marcada como Pública.
  - `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` mientras el servidor esté encendido y haya equipos vinculados. La pantalla sí puede apagarse.
  - Aviso al cerrar si un equipo se vio en los últimos 10 min: "La tablet y el celular dejarán de recibir cambios hasta que vuelva a abrir TPVC. ¿Cerrar de todos modos?".
  - "Abrir TPVC al iniciar Windows" (clave Run de HKCU), propuesto encendido al vincular el primer equipo.
  - "Probar conexión": direcciones, puerto, perfil de red y firewall.
- **Prueba:**
  - Los falsos registran las llamadas.
  - [W] Prueba de humo del perfil de red.
- **Riesgo:** bajo en código; alto en la operación del consultorio (§4, N1–N3).

#### 8.19 · Lectura, avisos y documentos (puede ir en dos versiones)
- **Cambia:**
  - Rutas de lectura de §3.5 y `/events` sobre `SyncChangeLog`.
  - `contracts/lan-api/v1/openapi.json`, generado con `Microsoft.AspNetCore.OpenApi`.
- **Prueba:**
  - Rutas contra un `Fase6Clinic` sembrado, con `TZ=America/Mexico_City`.
  - La espera larga despierta con un guardado en menos de 1 s.
  - `409 generation_changed`, `410 cursor_expired`, ETag y `304`.
  - Filtrado por rol.
  - Prueba de deriva del `openapi.json`.
  - Ejemplos grabados en `contracts/lan-api/v1/examples/*.json` con `TPVC_WRITE_CONTRACTS=1`.
- **Riesgo:** bajo. **Con este paso ya se puede construir la app del celular.**

#### 8.20 · Escrituras con idempotencia
- **Cambia:**
  - `LanRequestLog` y una cola por equipo en la PC.
  - Altas y cambios de pacientes (con 8.9, 8.10 y `PatientFormRules`), citas y ventas.
  - Aviso de reloj del equipo.
- **Prueba:**
  - Misma clave: misma respuesta, sin segunda venta.
  - Misma clave con otro cuerpo: `422`.
  - Respuesta perdida: un middleware corta la conexión después de confirmar; con el reintento queda un solo TKT y un solo descuento de existencias.
  - Un `409` por cada tipo de conflicto.
  - `422` con mensajes en español por campo.
  - Prueba de estrés: escrituras del servidor y de la UI al mismo tiempo (`SqlitePoolOpenGuard`, WAL, `busy_timeout`).
- **Riesgo:** medio.

#### 8.21 · Motor de la consulta de la tablet: prueba de concepto con decisión
- **Cambia:** el contrato de §3.8 se cumple dentro del proceso, todavía sin HTTP, de una de dos formas:
  - **(a) `RemoteConsultationHost`:** un `PatientsWorkspace` sin pantalla por cada consulta de la tablet, con:
    - su propio `ConsultationService` y su borrador por equipo;
    - un hilo serial propio;
    - diálogos que contestan según `confirm`;
    - sin impresión y sin nube.

    Se empieza por aquí, porque `PatientsWorkspaceReuseTests` ya corre una consulta completa sin WPF y `NotificationParityTests` ya tiene dos `ConsultationService` en el mismo proceso.
  - **(b) `ConsultationEngine`:** se extrae de `ConsultationAccount` y las hojas, y trabaja sobre el borrador. Se usa si (a) no pasa los criterios.
  - En los dos casos, `OpenConsultationRegistry` lleva la consulta de la PC y las de la tablet, y no deja abrir dos veces al mismo paciente ni repetir un folio CNS.
- **Criterios de sí o no (son las pruebas de este paso):**
  1. La misma consulta por el motor y por la PC deja filas idénticas: nota, hojas, venta, movimientos y cita "Atendida".
  2. Contestar "No" a una confirmación no cambia nada.
  3. No corre ningún temporizador ni despachador de WPF.
  4. La memoria por consulta está medida, y una consulta abandonada se cierra por tiempo.
  5. Un reinicio de la PC la recupera.
- **Riesgo:** alto. Por eso va antes de fijar las rutas de consulta. A Kotlin le da igual si queda (a) o (b).

#### 8.22 · Consulta desde la tablet
- **Cambia:**
  - Rutas de §3.8 y el borrador de la tablet reflejado en la PC.
  - En Pacientes, un aviso fijo "Consulta en curso en la tablet: <paciente>" con "Ver" (solo lectura) y "Continuar aquí".
  - Aviso de tablet sin conexión.
- **Prueba:**
  - Un `clientSeq` viejo se ignora.
  - Terminar con respuesta perdida deja un solo ticket.
  - Después de "Continuar aquí", la tablet recibe `409 consultation_taken_over`.
  - La consulta se recupera tras reiniciar la PC.
  - [W] Captura del aviso.
- **Riesgo:** medio.

#### 8.23 · Consulta entre equipos por la red
- **Cambia:**
  - `LanHandoffs` y las rutas de §3.9.
  - "Enviar a…" por capacidad `handoff.v1`.
  - Aviso en `/events`.
  - Turso queda solo para `SharedPcs`.
- **Prueba:** la batería de `ConsultationHandoffTests` corre contra los dos almacenes (Turso simulado y el local):
  - enviar;
  - aceptar una sola vez;
  - respuesta de aceptación perdida;
  - recuperar contra un envío atrasado;
  - la PC cierra su consulta sin descartarla.
- **Riesgo:** medio.

#### 8.24 · Cliente de referencia y servidor de pruebas para Kotlin
- **Cambia:**
  - `FakeTabletClient` (C#, en pruebas) que vincula por TLS en loopback y recorre:
    - leer;
    - un comando;
    - un aviso;
    - respuesta perdida y reintento;
    - revocación;
    - generación nueva;
    - consulta completa;
    - envío entre equipos en los dos sentidos.

    Graba los ejemplos del contrato.
  - Servidor de desarrollo con una base sembrada temporal (`TPVC_LAN_DEVSERVER=1 dotnet test --filter TestCategory=DevServer`) para la CI de Kotlin y para desarrollar en Linux.
- **Prueba:** los escenarios pasan en Linux.
- **Riesgo:** bajo.

### Pasos opcionales (según las respuestas del médico)

| Paso | Cuándo | Qué |
|---|---|---|
| 8.A Cifras fuera del consultorio | Pregunta 2 = sí | La PC sube `ReportSnapshots` (resumen, corte, alertas, compra sugerida; **sin datos de pacientes**) a una **segunda base** de Turso con su propio token de solo lectura. `GET /api/v1/away-config` lo entrega por TLS solo al celular. Tailscale queda como alternativa sin código. |
| 8.B Cobrar sin la PC | Pregunta 1 = sí | Cola de `consultation.finish` y `sale.register` con `origin: offline`, y folios con el `deviceCode` (`TKT-T-…`, `CNS-T-…`, `EXP-T-…`). Las existencias pueden quedar negativas y el movimiento dice "venta sin conexión". Nunca es todo o nada: nota, venta, movimientos y cita siempre se guardan, y los conflictos del resumen quedan para revisar. Kotlin porta `ConsultationAccount`, la receta y los folios con sus vectores. Cancelar tickets, inventario y configuración nunca se hacen sin conexión. |
| 8.C Kardex por lote | Recomendado | `DeductFromBatches` escribe un movimiento por lote y `SaveBatchAsync` un ajuste. Se agrega un reporte de movimientos contra existencias. |
| 8.D Descubrimiento automático | Si la IP cambia seguido | mDNS `_tpvc._tcp` (`DnsServiceRegister`) y NSD en Android. |
| 8.E Membrete en la nube | Para recuperar ante un desastre | Escudo y encabezado en una tabla `ClinicAssets` de Turso, subidos solo cuando cambia su hash. |

---

## 6. App Android en Kotlin (fase siguiente; aquí solo el esqueleto del plan)

**Ubicación:** carpeta `android/` en el mismo repositorio, para leer `contracts/` directamente. Tiene su propio flujo de CI, `android.yml` (`ubuntu-latest`, JDK 17, solo pruebas de JVM y lint, sin emulador). minSdk 26.

**Módulos**
- `:contract`: DTOs del contrato y lector de vectores.
- `:domain`: reglas portadas (`TextSearch`, `RankPatients`, `RankProducts`, `ParseQuantityPrefix`, signos e IMC, máscara de fecha).
- `:data`: cliente de la PC con el pin, avisos, caché cifrada, cola de comandos, borrador de consulta y vinculación.
- `:feature:patients`, `:feature:consultation`, `:feature:agenda`, `:feature:pos`, `:feature:reports` y `:feature:share`.
- `:app`: navegación por rol, bloqueo y arranque.

**Pila recomendada**

| Pieza | Elección | Por qué |
|---|---|---|
| Interfaz | Jetpack Compose + Material 3 | Un solo código para la tablet (ancho) y el celular (compacto), y se prueba con Robolectric. |
| Red | OkHttp 5 | `TrustManager` propio que solo acepta el SPKI del QR, buen control de tiempos para la espera larga, y `MockWebServer`/`HeldCertificate` para probar. Ktor no aporta nada: en Android usaría OkHttp por debajo. |
| JSON | kotlinx.serialization | No usa reflexión, y `ignoreUnknownKeys` acepta los cambios aditivos del contrato. |
| Caché | Room + SQLCipher | Transacciones y `Flow` para refrescar pantallas, cifrado con llave del Android Keystore. Guarda respuestas por recurso (con ETag), el índice de pacientes, la cola y el borrador; no copia las tablas de la PC. |
| Secretos | Android Keystore + DataStore | El token del equipo va cifrado con AES-GCM. `EncryptedSharedPreferences` está en desuso. |
| QR | CameraX + ML Kit (modelo incluido) | Funciona sin servicios de Google. Como respaldo se puede escribir a mano la dirección y el código. |
| Segundo plano | WorkManager | Reintenta la cola cuando vuelve la red y refresca los reportes en el Wi-Fi del consultorio. La espera larga corre en corrutinas solo con la app al frente. |
| Receta en PDF | WebView + `PrintDocumentAdapter` | Usa el HTML de la PC tal cual (mismo modelo y membrete) en media carta horizontal. Se prueba en la tablet real al inicio. |
| Ticket en PDF o imagen | `android.graphics.pdf.PdfDocument` | El ticket es texto de 42 columnas: dibujarlo es sencillo y no necesita WebView. |
| Bloqueo | androidx.biometric | Huella o PIN al abrir, porque hay datos de expediente. También se exige bloqueo de pantalla del equipo y `allowBackup=false`. |
| Concurrencia | Corrutinas + Flow | Es lo estándar en Android y se prueba con `kotlinx-coroutines-test`. |

**Roles (`DevicePermissions`, aplicados en la PC)**

| Función | PC | Tablet (`AndroidTablet`) | Celular (`AndroidMobileAdmin`) |
|---|:---:|:---:|:---:|
| Ver expedientes (`CanViewClinicalRecords`, nuevo) | sí | sí | sí |
| Editar expedientes y consulta | sí | sí | no |
| Recetar | sí | sí | no |
| Cobrar y vender | sí | sí | no |
| Agendar citas | sí | sí | pregunta 4 |
| Inventario | edita | solo ve | solo ve (o solo las alertas: pregunta 4) |
| Resumen, alertas y compra sugerida | sí | sí | sí |
| Corte de caja | sí | hoy no (pregunta 4) | sí |
| Compartir receta y ticket (`CanShareDocuments`, nuevo) | sí | sí | sí |
| Configuración, Excel, respaldos, Turso, vincular | sí | no | no |

**Orden de trabajo** (entre paréntesis, el paso de la PC que necesita)
1. **K0.** Esqueleto, CI y textos en español.
2. **K1.** Contrato y vectores (8.14).
3. **K2.** Vinculación: QR, pin, token y bloqueo (8.17).
4. **K3.** Conexión: orden de búsqueda de la PC, espera larga, aviso "Conectado a la PC / Buscando la PC… / Sin conexión: datos de las hh:mm", caché y borrado por revocación o generación (8.19).
5. **K4.** Celular: resumen, corte de caja, alertas, compra sugerida, expedientes en solo lectura y compartir la receta (8.19). **Primera versión del celular.** La prueba de HTML a PDF en el equipo real va al empezar este paso.
6. **K5.** Tablet, lectura: inicio (citas de hoy, recientes, búsqueda también sin conexión), ficha y agenda.
7. **K6.** Tablet, escrituras: alta y edición por campos con los errores del servidor bajo cada campo, la pregunta "¿Es el mismo paciente?", la pantalla de conflicto "Su cambio / En la PC", y agendar con "¿Agendar de todos modos?" (8.20).
8. **K7.** Consulta: las cinco secciones en el orden de la PC con la cuenta a la derecha, borrador con `clientSeq`, comandos con `needsConfirmation`, plantillas, calculadora pediátrica (comando a la PC), terminar y cobrar, y compartir receta y ticket. La pantalla se queda encendida durante la consulta (8.22).
9. **K8.** Consulta entre equipos: aviso, "Continuar aquí", "Enviar a la PC" y solo lectura mientras está ofrecida (8.23).
10. **K9.** Punto de venta en la tablet, con `RankProducts` y el prefijo `3*` (8.20).
11. **K10.** Accesibilidad, tema oscuro y revisión de textos. Más adelante, impresión ESC/POS por Bluetooth con `format=text`.

**Cómo se prueba**
- **Vectores:** JUnit lee los mismos archivos de `contracts/golden/` que C# (ruta relativa). Toda regla portada tiene su archivo, con casos de punto medio (`RoundingMode.HALF_EVEN`) y desempates con `Collator` es-MX.
- **Contrato:** `MockWebServer` reproduce `contracts/lan-api/v1/examples/*.json` como si fuera la PC. Escenarios:
  - vinculación;
  - búsqueda en línea y luego sin conexión;
  - consulta completa con confirmaciones;
  - el Wi-Fi se cae a media consulta: se conserva el borrador, se desactivan los botones de la cuenta, y al volver se manda un solo `PUT` y los comandos en orden;
  - respuesta perdida al terminar: el reintento usa la misma clave;
  - `401` borra todo;
  - `426` muestra el aviso de actualizar.
- **Pin:** con `HeldCertificate`, el pin correcto entra; un pin equivocado o un certificado de una autoridad sin el pin se rechazan.
- **Caché y pantallas:** Robolectric para Room y para los estados clave de Compose (conectado, sin conexión con hora, confirmación).
- **De punta a punta:** la CI levanta el servidor de desarrollo de 8.24 y corre las pruebas de integración de Kotlin contra la PC real, en el mismo runner de Linux.
- **En equipos reales:** solo lo que no se puede simular. Va en la sección D de `PENDIENTES_ANDROID.md`.

---

## 7. Cambios en otros documentos (paso 8.1)

**`docs/PENDIENTES_ANDROID.md`**
- Título: "… app Android (tablet y celular)". Introducción: Kotlin nativo, la red del consultorio como canal principal, Turso solo de respaldo y enlace a este plan.
- **A1–A5:** marcarlos como "Resuelto por diseño en la Fase 8 (solo la PC escribe)", con el paso que cierra cada uno (§4). Correcciones:
  - A1: los lotes no se pueden reconstruir con los movimientos de hoy.
  - A2: también se siembran los pacientes demo en arranques posteriores (`seedDemoData: !isFirstRun`).
  - A3: los folios CNS usan la hora y el `-R1` nunca aplica a CNS.
  - A4: agregar el error latente entre dos PC.
- **A6** se queda: la primera subida completa sigue siendo necesaria para el respaldo.
- **B1:** decidido Kotlin. Quitar la recomendación de MAUI.
- **B2:** se retira; ya no hay `TPVC.Core`. Lo reemplazan los generadores puros (8.13) y la API de la red.
- **B3:** los ViewModels de `ViewModels/Patients` son la referencia de comportamiento y corren en la PC (8.21), no en Android.
- **B4:** los permisos se aplican en la PC (8.17).
- **C1:** QR por la red, sin el token de Turso (8.17).
- **C2:** la receta se genera a partir del HTML de la PC y el ticket con `TicketText`. ESC/POS más adelante.
- **C3a:** la consulta entre equipos va por la red (8.23). El contrato de Turso queda solo para `SharedPcs`.
- **C3:** política sin conexión de §3.10.
- **C4:** Compose, con las mismas reglas de la consulta.
- **C6:** los documentos los sirve la PC (8.19).
- **D:** nueva lista "Fase 8":
  - aviso del firewall y su UAC;
  - red marcada como Pública;
  - cambio de IP de la PC;
  - Wi-Fi con aislamiento de clientes;
  - la PC no se duerme con TPVC abierto;
  - "Quitar" borra los datos del celular;
  - receta en PDF compartida por WhatsApp;
  - un día completo con la tablet.

**`docs/TURSO_SYNC_ARCHITECTURE.md`**
- §1: Turso es respaldo y recuperación, y solo la PC habla con él.
- §2: límites vigentes (500M lecturas, 10M escrituras, 5 GB, 100 bases) y el uso estimado de §2.3. Quitar las cifras viejas (1B, 25M, 9 GB, 500 bases, "6k–15k escrituras al mes").
- §3: matriz igual a `DevicePermissions`, con `CanViewClinicalRecords` y `CanShareDocuments`. El celular tiene corte de caja y la tablet no (pendiente de la pregunta 4).
- §4: el protocolo v2 se queda para el respaldo y para `SharedPcs`, que queda congelado.
  - §4.3: cursor por `Seq` (8.6); en modo respaldo no se descarga.
  - §4.4: `ExecuteBatchAsync` no es transaccional; la subida es idempotente.
  - §4.5: la revisión no lee la hora del servidor (solo lo hace `GetLinkedDevicesAsync`) y aplica solo entre dos PC.
- §5: reescribir. El paquete actual (JSON en claro con el token completo, sin QR) se retira. La vinculación va por la red, según el §3.2 de este plan.
- §7 nuevo, "Respaldo y recuperación": modo respaldo, verificar copia, restaurar y reemplazar, recuperar desde la nube.
- §6: aclarar que el token se queda en la PC.

**`docs/PLAN_REDISENO_UI.md`**
- Tabla de fases: nueva fila "8 · Datos para varios equipos en la red" con estado "Plan (`docs/PLAN_FASE8_VARIOS_EQUIPOS.md`)".
- Fase E: entre PC y PC hecho en v1.17. Entre PC y tablet, por la red del consultorio (pasos 8.22–8.23). La tabla en Turso queda solo entre dos PC.
- Pantallas nuevas de la PC, con los mismos principios y agregadas a `UiSmokeTests`:
  - "Uso de la nube";
  - "Vincular tablet o celular";
  - "Equipos vinculados";
  - "Probar conexión";
  - el aviso "Consulta en curso en la tablet";
  - el aviso al cerrar.
- Notas al final de la Fase 7: QR por la red, y compartir desde el PDF que se arma con el HTML de la PC.

**`docs/ARQUITECTURA_TECNICA.md`**
- §1: la PC también es el servidor del consultorio.
- §6: carpetas `Services/Lan`, `Services/Documents`, `contracts/` y `android/`.
- §7: job de Linux (UTC y `America/Mexico_City`), vectores compartidos, ejemplos del contrato y prueba de deriva de OpenAPI.
- §8:
  - Reemplazar "Siguiente paso sugerido para el servidor", que habla de LWW y prefijo por equipo, por una referencia a §10.
  - Agregar `SyncChangeLog`, `DbMeta` y `LanRequestLog` a "Metadatos de sincronización".
  - Agregar que los guardados de pacientes son por campo (8.9).
- §9: `PatientsWorkspace` también puede correr sin pantalla en el servidor (según 8.21).
- §10 nuevo, "Servidor del consultorio": Kestrel y certificado, `lan.db`, rutas, idempotencia, despachador de avisos y política al restaurar.

**También:** `docs/MANUAL_DE_USUARIO.md` tendrá un capítulo "Tablet y celular" (vincular, firewall, reservar IP, dejar TPVC abierto) en el paso 8.17.

---

## 8. Preguntas abiertas para el médico

Solo las que deciden algo del plan.

1. **¿Necesita terminar y cobrar una consulta en la tablet cuando la PC está apagada o fuera del consultorio?**
   - Si no: la tablet solo lee y sigue escribiendo la consulta abierta, y se termina al volver la PC, o en la PC con "Continuar aquí".
   - Si sí: se construye 8.B. Los folios llevarán `-T-` para siempre, las existencias pueden quedar en negativo hasta que la PC las revise, y la app de Kotlin crece.
2. **¿Quiere ver en el celular, desde casa, el corte de caja y las alertas al día?** Si sí: 8.A, una base aparte en Turso solo con cifras, sin expedientes. En v1 se recomienda no ver expedientes desde fuera.
3. **¿La PC del consultorio es de escritorio o laptop? ¿Queda encendida con TPVC abierto todo el horario?** ¿Configuramos TPVC para abrirse al iniciar Windows y que la PC no se duerma mientras está abierto?
4. **Permisos:** ¿la tablet puede ver el corte de caja (hoy no)? ¿El celular puede agendar o mover citas, o solo verlas? ¿El celular ve todo el inventario o solo las alertas?
