# Servidor Turso simulado (`FakeHranaServer`)

Qué copia de Turso el servidor simulado de las pruebas (`tests/TPVC.Tests/FakeHranaServer.cs`), qué no copia y cómo
levantarlo por HTTP para la CI de Kotlin. Paso 8.13 del plan (`docs/PLAN_FASE8_VARIOS_EQUIPOS.md`). Lo que se comprobó
en Turso real está en `docs/TURSO_SYNC_ARCHITECTURE.md` §6 (V12, V14, V15, V17–V23).

El servidor es un `HttpMessageHandler` sobre un archivo de SQLite: la PC lo usa en el mismo proceso (`HttpClient(server)`)
y, desde v1.31.0, también por HTTP en `127.0.0.1` (`FakeTursoHost`). Vive en el proyecto de pruebas y nunca entra a
`TPVC.exe`.

## Lo que copia de Turso

- **Hrana v2 por HTTP:** `POST /v2/pipeline` con `{"requests":[…]}`. Atiende `execute`, `batch` y `close`, y responde
  `{"baton":null,"results":[…]}`: un resultado por petición, `{"type":"ok","response":{"type":"execute","result":{"cols":[{"name"}],"rows":[[…]],"affected_row_count":n}}}`
  o `{"type":"error","error":{"message","code"}}`.
- **Valores** (`contracts/golden/values.json`): `{"type":"integer","value":"42"}` (el entero va como **texto**: como número,
  toda la petición recibe HTTP 400), `float` como número, `text`, `null` y `blob` con `base64`. Un BLOB se responde **sin
  el relleno** `=` y se aceptan los argumentos con y sin él (V18); un base64 inválido también es HTTP 400.
- **Sin transacción entre instrucciones:** cada `execute` se confirma solo, aunque vayan en la misma petición. Un `batch`
  con `BEGIN`, pasos con condición (`ok`, `error`, `not`, `and`, `or`, `is_autocommit`), `COMMIT` y `ROLLBACK` sí es
  atómico, como en Turso (V14 y V23). Al cerrar la petición, una transacción que quedó abierta se deshace.
- **Un escritor a la vez:** las peticiones de dos equipos se atienden en orden.
- **Reloj del servidor** inyectable (`Clock`): reemplaza `'now'` en el SQL (`strftime('%Y-%m-%d %H:%M:%f','now')`).
- **Lecturas y escrituras cobradas** (`BilledRowsRead`, `BilledRowsWritten`, y por instrucción en `Requests`): cada fila
  recorrida cuenta como leída (también al ordenar antes del `LIMIT`, y la tabla entera en `ALTER` y `CREATE INDEX`); una
  instrucción que no toca filas cuenta 1. Es una estimación: SQLite no dice las filas leídas por un índice.
- **Revisión de planes** (`ScanPolicy`, desde el paso 8.6): con `EXPLAIN QUERY PLAN`, un recorrido completo (`SCAN`) de una
  tabla sincronizada (las que tienen `SyncedAtUtc`) hace fallar la instrucción con `SCAN_FORBIDDEN`. En Turso no falla,
  pero cobra cada fila: así se detecta una consulta mal escrita. Un recorrido a propósito empieza con
  `/* recorrido completo a propósito */ ` (`TursoSyncService.FullScanMarker`; lo usa el reporte de la «Sincronización
  completa»).
- **Tope de respuesta** (`MaxResponseBytes`): la instrucción que lo pasa responde `RESPONSE_TOO_LARGE` (Turso devolvió
  64 MB sin error, V12).
- **Cuota agotada** (`Blocked`): HTTP 200 con cada instrucción en error `BLOCKED`, o la petición entera con un estado de
  error y `BLOCKED` en el cuerpo (V7: falta ver la forma real).
- **Token** (`AcceptedTokens`, desde v1.31.0): con la lista puesta, un token que no está en ella recibe HTTP 401 sin
  ejecutar nada, con el cuerpo de Turso (V3, 2026-10-11): ``{"error":"Unauthorized: `unauthorized access attempt on
  database: invalid JWT token: can't be decoded with any of the existing keys`"}``. Un token que ni siquiera es un JWT
  recibe en Turso HTTP 400 `{"error":"JWT error: InvalidToken"}`; el simulador no lo copia (los tokens de las pruebas no
  son JWT), y el equipo lo trata igual que el 401. En Turso, `turso db tokens invalidate` anula todos los
  tokens de la base y del grupo, al instante (V4, 2026-10-11); un token anulado recibe 401 con otro texto, ``invalid JWT
  token: role was invalidated after token was issued``. El simulador manda siempre el primero: el equipo no distingue
  el texto, solo el estado.

## Lo que no copia

- **Las estadísticas de cada resultado.** Turso agrega `rows_read`, `rows_written`, `last_insert_rowid`, `replication_index`
  y `query_duration_ms`, y `decltype` en cada columna; el simulador no los manda. Un cliente debe ignorar los campos que no
  conoce y no depender de ellos (la PC solo lee `cols`, `rows` y `affected_row_count`).
- **Los streams.** El `baton` siempre es `null` y cada petición es independiente; tampoco hay `sequence`, `describe`,
  `store_sql` ni el endpoint `/v3`.
- **Los mensajes de error de SQLite** van con el código genérico `SQLITE_ERROR` (Turso puede mandar uno más específico).
- **La red:** sin latencia ni límites de tamaño de la petición (V17: Turso aceptó 43 MB de JSON).
- **Comprimir la respuesta:** Turso tampoco lo hace (V21).

## Para las pruebas

| Propiedad | Para qué |
|---|---|
| `IsOffline` | La petición falla antes de llegar (sin red). |
| `DropResponseAfterExecute` | Se aplica y se pierde la respuesta (la red cae justo después). |
| `BeforeExecuteAsync` | Esperar antes de aplicar, para forzar el orden entre dos equipos. |
| `FailWhen` | La instrucción cuyo SQL cumple la condición falla con `SQLITE_CONSTRAINT` sin ejecutarse. |
| `Requests`, `ScanWarnings` | Lo que llegó, con lo que habría cobrado Turso. |
| `RecordTranscript`, `Transcript` | Cada intercambio tal como viajó (petición, estado y respuesta), para `contracts/hrana/`. |
| `Lock()` y `Connection` | Leer o cambiar la nube desde la prueba sin cruzarse con una petición. |

## `contracts/hrana/`

Grabaciones de un equipo que **lee** la nube (el motor de la PC hace lo que hará Kotlin), una por archivo, en JSON
Lines: un renglón de encabezado (`scenario`, `contract`, `protocol`, `notes`) y uno por intercambio
(`{"n","request","status","response"}`). Lo que cambia en cada corrida va como marcador: `<time>` (una hora), `<id:N>`
(un GUID al azar: `clinicId`, generación), `<nonce:N>` (el `InstanceNonce`) y `<appVersion>`. Los `SyncId`
`10000000-0000-4000-8000-…` son de los datos de ejemplo. Se regeneran con `TPVC_WRITE_GOLDEN=1`
(`TursoSyncEndToEndTests.Hrana.cs`).

| Archivo | Qué muestra |
|---|---|
| `first-download.jsonl` | Un equipo nuevo que se une a una nube con identidad y baja todo, tabla por tabla. |
| `incremental.jsonl` | La bajada incremental: una sola petición (un `batch` de lectura) con la hora, la primera página de cada tabla y de las lápidas, y el perfil. |
| `key-ties.jsonl` | Cinco filas con el mismo `SyncedAtUtc` y páginas de 2: el cursor (`SyncedAtUtc`, `SyncId`) no pierde ni repite filas. |
| `generation-change.jsonl` | Otra PC reemplaza la nube: el equipo espera mientras `generationReady` no es la nueva y luego vuelve a bajar todo. |
| `blocked.jsonl` | Cuota agotada, en las dos formas que simula el servidor. |
| `unauthorized.jsonl` | Un token anulado: HTTP 401 y no se borra nada. |
| `statements.json` | Cada instrucción de lectura, literal y con sus argumentos (la identidad, la generación, el perfil, la página de cada tabla, el latido, el registro y el membrete). La prueba las corre en el simulador y ninguna recorre una tabla completa. |

## Por HTTP para la CI de Kotlin (`FakeTursoHost`)

`tests/TPVC.Tests/FakeTursoHost.cs` sirve el simulador con `HttpListener`, solo en `127.0.0.1` (TPVC acepta `http://` solo
ahí y en `localhost`). Atiende `POST /v2/pipeline`, `GET /health` y `GET /__stop`. La prueba
`FakeTursoHostTests.ServeForTheKotlinCi` (categoría `FakeTursoHost`) lo levanta solo con `TPVC_FAKE_TURSO_HOST=1`; sin la
variable termina en el acto, así que no estorba en la CI normal.

| Variable | Valor por omisión | Para qué |
|---|---|---|
| `TPVC_FAKE_TURSO_HOST` | — | `1` para levantarlo. |
| `TPVC_FAKE_TURSO_PORT` | `8787` | El puerto en `127.0.0.1`. |
| `TPVC_FAKE_TURSO_MINUTES` | `30` | Cuánto escucha como máximo. |
| `TPVC_FAKE_TURSO_TOKEN` | (cualquiera) | Si se pone, solo acepta ese token; los demás reciben 401. |
| `TPVC_FAKE_TURSO_READY_FILE` | — | Archivo donde escribe la URL cuando ya está listo. |
| `TPVC_FAKE_TURSO_SEED` | `demo` | `none` deja la nube vacía: así se prueban los scripts de `scripts/verify-turso-*.ps1`, que se niegan a correr en una base con datos (`-Url http://127.0.0.1:8787`). |
| `TPVC_FAKE_TURSO_API_TOKEN` | — | Paso 8.15: con un valor, también hace de API de la plataforma de Turso (solo lo de la rotación) y pide ese token. `GET /v1/organizations/{org}/databases` lista la base simulada (`prueba-simulada`, grupo `pruebas`, `Hostname` 127.0.0.1); `POST …/databases/prueba-simulada/auth/rotate` anula todos los tokens; `POST …/auth/tokens` crea uno y lo regresa en `jwt`. Para `verify-turso-hrana.ps1 -Rotate -ApiBase http://127.0.0.1:8787`. |
| `TPVC_FAKE_TURSO_GROUP_PEERS` | — | Otras bases (separadas por comas) en el mismo grupo: el script se niega a rotar. |

Antes de escuchar, una PC con los datos de demostración (pacientes, consultas, productos, lotes y ventas) sube todo y hace
la «Sincronización completa», así que la nube tiene esquema, identidad (`clinicId`, generación) y datos, como la del
consultorio. Ejemplo para el job de Kotlin (en Linux, con el runtime de escritorio que ya instala la CI de C#):

```bash
TPVC_FAKE_TURSO_HOST=1 TPVC_FAKE_TURSO_TOKEN=kotlin-ci TPVC_FAKE_TURSO_READY_FILE="$RUNNER_TEMP/turso.url" \
  dotnet test tests/TPVC.Tests/TPVC.Tests.csproj --filter TestCategory=FakeTursoHost &
for i in $(seq 1 120); do [ -f "$RUNNER_TEMP/turso.url" ] && break; sleep 1; done
./gradlew :sync:test -Pturso.url="$(cat "$RUNNER_TEMP/turso.url")" -Pturso.token=kotlin-ci
curl -s http://127.0.0.1:8787/__stop
```

El simulador revisa los planes también aquí: una consulta de Kotlin que recorra una tabla sincronizada recibe
`SCAN_FORBIDDEN`, igual que en las pruebas de la PC.
