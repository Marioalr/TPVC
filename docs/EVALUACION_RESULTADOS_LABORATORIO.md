# Laboratorios: dónde guardar los resultados (PDF y fotos)

Evaluación del 2026-10-10 sobre TPVC v1.20.6 (rama `fase-8/paso-8.8`), hecha por un agente a pedido del médico. Fuentes de Turso consultadas el 2026-10-10: <https://turso.tech/pricing>, <https://docs.turso.tech/help/usage-and-billing>, <https://docs.turso.tech/features/point-in-time-recovery> y el código de libSQL (`libsql-hrana/src/proto.rs`, `libsql-server/src/main.rs`).

**Estado al 2026-10-11:** el médico eligió la recomendación («Turso + copia en la laptop»). La orden de estudios ya está en v1.25.0 (`PatientConsultations.LabOrderJson`); los resultados quedan para el paso 8.12b de `PLAN_FASE8_VARIOS_EQUIPOS.md`. Los dos errores de §2 se corrigieron en v1.20.7 (#12) y V18 ya está en `scripts/verify-turso-hrana.ps1 -Only V18`. V17 y V19–V23 se corrieron en Turso real el mismo día (resultados en §7): borrar sí libera cuota, así que el diseño de §6 no cambia.

## Recomendación

**Guardar los resultados en Turso, en tablas propias y fuera de las 13 que bajan todos. La laptop guarda una copia completa y la nube tiene un tope de 2 GB (el híbrido H2 de §5).**
- **Las filas no son el problema.** Con 10 resultados al día se gastan como máximo ~6k filas escritas y ~13k leídas al mes: 0.06% y 0.003% del plan. El límite que importa es el **almacenamiento (5 GB)**, y en el plan gratuito, al pasar *cualquier* límite, Turso bloquea toda la base: también la sincronización y la consulta entre equipos.
- **En el caso realista sí cabe.** Con 3 resultados al día y las fotos re-codificadas a ~0.5 MB se usan 0.55 GB al año, así que el tope de 2 GB llega en ~3.7 años. El peor caso sin comprimir (10 × 3 MB al día) llenaría los 5 GB en **5.6 meses**. Por eso la compresión al capturar y el tope son obligatorios.
- **La laptop como único almacén es peor en todo.** Vuelve a poner el servidor en la red local que se descartó (plan §2.6). El celular no vería resultados fuera del consultorio. Con la laptop cerrada, la única copia queda en la tablet. Y la laptop no tiene respaldo fuera de su disco: `backups/` está junto a `consultorio.db` (`BackupService.cs:29`).
- **Salida si algún día no alcanza:** el plan Developer (US$5.99 al mes, o 4.99 con pago anual) da 9 GB a US$0.75 por GB extra y 10 días de restauración a un punto en el tiempo, sin cambiar el diseño.

## 1. Turso: límites y cómo cuenta un BLOB

- **Plan gratuito:** 5 GB, 500M filas leídas y 10M escritas al mes, restauración a un punto en el tiempo de 1 día, sin bitácora de auditoría. La página de precios no lista ningún tráfico de red para las consultas; la métrica "syncs" solo aplica a las réplicas embebidas, que TPVC no usa.
- **Filas:** se cuentan las filas que se tocan, no los bytes. Un BLOB de 3 MB en una fila es 1 fila escrita, más 1 por cada índice. La página de precios ya dice que cada índice suma escrituras, así que **V10 queda contestada: sí cuentan**.
- **Almacenamiento:** se mide en páginas de 4 KB con `dbstat` (tablas e índices). El BLOB se guarda en binario, con ~1% de sobrecarga por páginas de desbordamiento; el base64 solo existe al viajar. `VACUUM` está deshabilitado en Turso, pero borrar sí libera cuota (V20): `dbstat` no cuenta las páginas libres, y SQLite las reúsa antes de que el archivo crezca.
- **Restaurar a un punto en el tiempo** crea una base **nueva**, con otra URL y otro token (hay que volver a vincular todos los equipos), y esa base también cuenta para el almacenamiento. Mientras convivan las dos, el contenido L debe cumplir 2 × (L + 0.1 GB) ≤ 5 GB, o sea **L ≤ 2.4 GB**. De ahí sale el tope de 2 GB.
- **Tamaño de fila:** no hay un tope documentado. SQLite admite valores de hasta 1,000,000,000 bytes. El servidor libre de libSQL trae por defecto un tope de respuesta de 10 MB, pero en Turso real V12 devolvió 64 MB sin error. Tampoco hay tope de **petición** hasta 32 MB de BLOB, ~43 MB de JSON (V17).

## 2. Transporte por Hrana

- Un BLOB viaja como `{"type":"blob","base64":…}`, con **+33%**. La petición sale sin comprimir (`StringContent`, `TursoClient.cs:580-581`). En la respuesta, el +33% se recupera solo si Turso la comprime con gzip; el cliente lo pide (`AutomaticDecompression`, :89). Turso no la comprime (V21), así que la bajada también cuesta el +33%. Comprimir el contenido no sirve: JPEG y PDF ya vienen comprimidos (gzip gana menos de 10%).
- **Trozos de 512 KiB, unos 700 KB de JSON cada uno.** Un archivo de 3 MB en una sola petición pesa ~4 MB. Con 1 Mbps de subida tarda ~32 s, más que los 30 s de espera de `SharedHttpClient` (:92). Un trozo tarda ~5.6 s a 1 Mbps y ~1.1 s a 5 Mbps, y una subida cortada sigue desde el último trozo. Para bajar se piden 4 trozos por petición (~2.8 MB), muy debajo de los 64 MB de V12.
- **Compresión al capturar** (supuestos que hay que medir con 10 resultados reales del laboratorio que usa el médico):
  - Una foto de 12 MP pesa 3–5 MB. Re-codificada a 2000 px por el lado largo en JPEG con calidad ~80 queda en 0.2–0.6 MB por página, unos 180 dpi en hoja carta.
  - Se usa JPEG y no WebP: en WPF, WebP depende de que esté instalado el códec de WIC.
  - Los PDF digitales (0.1–0.5 MB) no se tocan. Un PDF escaneado de más de 3 MB recibe un aviso, y el tope duro es de 10 MB por archivo.
- **Dos errores que había, y que también afectaban al membrete (8.12)** (corregidos en v1.20.7, #12: `ExtractValue` regresa `byte[]` y `DecodeBase64` acepta las dos formas):
  - `ExtractValue` regresa `null` para todo BLOB, porque revisa `value` antes de llegar al caso `blob` (`TursoClient.cs:500-501` contra :508).
  - libSQL manda el base64 **sin relleno** (`STANDARD_NO_PAD`) y `Convert.FromBase64String` lo rechaza. `FakeHranaServer` lo manda con relleno (`FakeHranaServer.cs:503`), así que las pruebas no lo ven.

## 3. Escenarios

Supuestos:
- 30 días al mes y 365 al año; 1 MB = 10⁶ bytes.
- Trozos de 512 KiB.
- Cada archivo se baja 3 veces: el archivo de la laptop y 2 vistas.
- Escrituras por archivo: 2 por trozo, más ~8 de los metadatos y del cierre.
- No incluye el resto de la base, que crece decenas de MB al año.

| Escenario (resultados/día × MB) | MB/día | MB/mes | GB/año | Llenar 5 GB sin tope | Meses de resultados en la nube con tope de 2 GB | Escrituras/mes | Lecturas/mes |
|---|---:|---:|---:|---:|---:|---:|---:|
| 1 × 0.3 (PDF digital) | 0.3 | 9 | 0.11 | 46 años | 222 meses | 300 | 360 |
| **3 × 0.5 (realista, fotos comprimidas)** | 1.5 | 45 | 0.55 | 9.3 años | 44 meses | 900 | 1.1k |
| 5 × 1.0 | 5 | 150 | 1.8 | 33 meses | 13 meses | 1.8k | 2.7k |
| 10 × 0.6 (máximo, comprimidas) | 6 | 180 | 2.2 | 28 meses | 11 meses | 3.6k | 5.4k |
| 3 × 3.0 (sin comprimir) | 9 | 270 | 3.3 | 18.5 meses | 7.4 meses | 1.8k | 3.8k |
| 10 × 3.0 (peor caso, sin comprimir) | 30 | 900 | 11 | **5.6 meses** | 2.2 meses | 6k | 12.6k |

- Todo queda por debajo del 0.06% de las escrituras y del 0.003% de las lecturas del plan.
- Agregar una suma diaria de `LabBlobs` para el tope cuesta ≤ 0.2% de las lecturas incluso a los 10 años.
- En tráfico, el peor caso comprimido sube ~240 MB y baja ~0.5–0.7 GB al mes.

## 4. Impacto en lo que ya existe

- **Sincronización completa y bajadas:** sin cambio. El contenido no está en las tablas de `GetTableMaps`; solo viajan los metadatos (~400 B por fila, ~3.6k filas al año con 10 al día).
- **Reporte de conteos (`TursoSyncService.cs:743-767`):**
  - suma `LabResults` como una tabla más;
  - agrega una línea desde `LabBlobs`: archivos y MB en la nube, sin subir y faltantes en la laptop (1 fila leída por archivo).
- **Unirse o Recuperar (8.8):** baja los metadatos. Después, la laptop baja en segundo plano todo el contenido de la nube: 2 GB tardan ~5–7 min a 50 Mbps. Lo que ya se desalojó de la nube solo regresa desde el respaldo externo.
- **Reemplazar la nube (8.9):** borra y vuelve a subir solo los metadatos. `LabBlobs` y `LabBlobChunks` no se tocan, igual que `FolioCounters`, así que no se re-suben gigas. El contenido que el respaldo ya no menciona queda huérfano; la PC lo borra de la nube a los 30 días y lo conserva en su archivo.
- **Respaldos locales:** los archivos **nunca** van dentro de `consultorio.db`. Si fueran ahí, los 14 respaldos diarios (`BackupService.cs:18`) multiplicarían 2 GB a 28 GB. Van en una carpeta aparte, inmutable, con el hash como nombre, que restaurar `consultorio.db` no toca.
- **Restauración a un punto de 1 día:** borrar algo en la nube es definitivo a las 24 h. Por eso la nube nunca es la única copia de lo que se desaloja.

## 5. La laptop como almacén y los híbridos

**Opción 2: la laptop como almacén. No se recomienda.**
- **Hace falta un servidor permanente.** `LanPairingHost` (8.14) escucha 10 min, atiende un solo equipo y solo sirve para vincular. Aquí haría falta un `LanFileHost` escuchando siempre que TPVC esté abierto, con:
  - descubrimiento: la IP cambia por DHCP, así que la PC tendría que publicar su IP y su puerto en el latido;
  - una regla permanente en el Firewall;
  - un Wi-Fi sin aislamiento de clientes. La vinculación tiene el respaldo de teclear a mano; esto no tendría ninguno.
- **Seguridad:** se puede resolver, pero es otro protocolo criptográfico que mantener igual en C# y en Kotlin, con sus vectores:
  - una llave de larga duración por equipo, sacada al vincular (HKDF con otro `info`), guardada con DPAPI y con el Keystore;
  - HMAC mutuo y AES-GCM por trozo;
  - revocación por `DeviceRegistry`.
- **Disponibilidad y pérdida:**
  - Con la laptop cerrada (supuesto del plan §1), la tablet guarda la única copia: horas entre semana, y días en fines de semana o vacaciones.
  - El celular no ve nada fuera del consultorio.
  - Si la laptop se pierde o se descompone, se pierde todo, salvo que haya un respaldo externo que hoy no existe.
- **Costo (estimación gruesa):** PC 3–4 semanas y Kotlin 3–5 semanas (descubrimiento, cola, protocolo y pruebas en la red real), contra PC 2–3 y Kotlin 2–3 para la opción Turso.

**Híbridos:**
- **H1 · Metadatos y miniatura en Turso, archivo en la laptop.** Las miniaturas son baratas (15–30 KB, ~0.1 GB al año), pero conserva todos los problemas de la red local. No vale la pena.
- **H2 · Archivos en Turso con tope, y la laptop como archivo completo.** Es la recomendada:
  - La nube guarda los resultados recientes: según el ritmo de §3, de 11 meses a varios años dentro de 2 GB.
  - La laptop guarda todo.
  - Solo se desaloja de la nube lo que la laptop verificó **y** que ya está en un respaldo externo.
- **H3 · Turso solo como buzón:** se borra de la nube en cuanto la laptop lo archiva, o a los 90 días. Es H2 con un tope chico, y queda como opción de configuración.
- **Almacenamiento de objetos (R2 o B2):** sería otra credencial en cada equipo y otro proveedor. Solo se justifica más allá de los 9 GB del plan Developer.

## 6. Diseño recomendado (bosquejo)

**Tablas sincronizadas** (`ISyncEntity`: LWW, lápidas, `SyncChangeLog`, filas apartadas y la cerca de generación de 8.9, como cualquier otra):
- `LabOrders`: la orden de estudios, en texto.
- `LabResults`: solo metadatos. Columnas:
  - `SyncId`;
  - `PatientRecordSyncId`, obligatoria y con borrado en cascada;
  - `PatientConsultationSyncId` y `LabOrderSyncId`, opcionales;
  - `Title`, `Laboratory`, `ResultDate` (`dateOnly`), `MimeType`, `PageCount`, `SizeBytes`;
  - `ContentSha256` y `ThumbSha256` (opcional).

  El contenido es inmutable: cambiar el archivo crea una fila nueva.

**Tablas solo de la nube.** Las crea la PC con DDL explícito (como `FolioCounters`), no están en `GetTableMaps` y Kotlin nunca corre DDL:

```sql
CREATE TABLE LabBlobs (Sha256 TEXT PRIMARY KEY, SizeBytes INTEGER NOT NULL, ChunkSize INTEGER NOT NULL,
  ChunkCount INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT, ArchivedAtUtc TEXT, EvictedAtUtc TEXT);
CREATE TABLE LabBlobChunks (Sha256 TEXT NOT NULL, ChunkIndex INTEGER NOT NULL, Data BLOB NOT NULL,
  PRIMARY KEY (Sha256, ChunkIndex));   -- tabla con rowid, no WITHOUT ROWID: las filas son grandes
```

**Cómo funciona:**
- **Subir** (PC y tablet; el celular no, porque no escribe nada clínico):
  1. Guarda el archivo en el equipo primero.
  2. Registra el archivo en `LabBlobs` con `INSERT … ON CONFLICT DO NOTHING`; si ya existía el hash, no hay duplicado.
  3. Pregunta qué trozos ya existen y sube los que faltan, uno por petición, con `INSERT … ON CONFLICT DO NOTHING`.
  4. Cierra con `UPDATE LabBlobs SET CompletedAtUtc = <hora del servidor> WHERE Sha256 = ? AND (SELECT COUNT(*) FROM LabBlobChunks WHERE Sha256 = ?) = ChunkCount`.

  Todo es idempotente y se puede continuar. Mientras no esté completo, los demás equipos ven "Subiendo desde la tablet".
- **Bajar al abrir:** `SELECT ChunkIndex, Data … WHERE Sha256 = ? AND ChunkIndex >= ? ORDER BY ChunkIndex LIMIT 4`, y se verifica el SHA-256.
  - **Laptop:** guarda todo en `%LocalAppData%\TPVC\laboratorio\<ab>\<sha>`; después de cada sincronización baja lo que le falta y escribe `ArchivedAtUtc`.
  - **Android:** caché LRU en `noBackupFilesDir`, cifrada con AES-GCM y una llave del Keystore (500 MB en la tablet, 200 MB en el celular). El celular baja solo lo que abre. La caché se borra junto con la base al revocar el equipo.
- **Tope:** solo lo maneja la PC principal, una vez al día, con la suma de `SizeBytes` sin desalojar.
  - Arriba de 2 GB desaloja los más viejos hasta 1.8 GB: `DELETE` de sus trozos y `EvictedAtUtc`, en una transacción.
  - Solo desaloja lo que tiene `ArchivedAtUtc` y está en el respaldo externo ("Respaldar laboratorios": copia incremental a una USB o a una carpeta de OneDrive).
  - Sin respaldo externo no desaloja nada: avisa en 1.6 GB y en 2 GB deja de subir ("el archivo queda en este equipo").
- **Freno de consumo:** se le agrega un tope de bytes por equipo y por día, por ejemplo 300 MB de bajada y 150 MB de subida. Hoy solo cuenta filas, y un ciclo de bajadas gastaría datos móviles sin pasar sus topes.
- **Contrato:** el SQL de subir, cerrar y bajar va literal en `contracts/hrana/`, con vectores del hash y de los trozos.

**Dónde cabe en el plan:**
- **La orden** puede entrar en cualquier momento como tabla sincronizada aditiva, de preferencia antes de que 8.23 congele `schema.json`.
- **Los resultados** cabrían en un paso nuevo, **8.12b**:
  - después de 8.12, porque es el mismo patrón que el membrete (binario por hash, bajado bajo pedido) y comparte la corrección de los BLOB;
  - antes de 8.13, para que el kit de conformidad grabe estos intercambios;
  - necesita 8.9 (Reemplazar la nube no toca el contenido) y 8.10 (solo la PC crea tablas).
- **En Kotlin:** ver y bajar en K4 (celular y tablet); capturar y subir en K7 (tablet).

## 7. Riesgos y comprobaciones en Turso real

**Riesgos:**
- Pasar de 5 GB bloquea todo: el tope de 2 GB lo revisa la app antes de cada subida.
- Que la compresión deje ilegible la letra chica de un resultado: probar con resultados reales.
- Los resultados son datos sensibles: quedan igual de expuestos que las notas, porque un token `full-access` ya lee todo.
- Hasta que se suba, la única copia está en el equipo, como cualquier cambio hecho sin conexión.

**Comprobaciones.** Están en `scripts/verify-turso-blobs.ps1` (desde el 2026-10-11), con el mismo estilo de `verify-turso-hrana.ps1`: solo tablas `vtest_*` en una base de prueba, ~100 MB temporales. Los BLOB se generan en el servidor con `randomblob`, salvo en V17, V22 y V23. V20 mide con `PRAGMA page_count`/`freelist_count`, `dbstat` (si existe) y `turso db inspect` (con `-DbName`); V22 se corre en cada red con `-Only V22 -Network …`. V18 se hizo aparte (`verify-turso-hrana.ps1 -Only V18`).

| # | Qué se comprueba | Qué decide |
|---|---|---|
| V17 | `INSERT` con BLOB de 0.5, 1, 2, 4, 8, 16 y 32 MB: tope de la **petición**, código de error (¿413?) y tiempo | El tamaño de trozo y el manejo del error |
| V18 | BLOB con y sin relleno de base64, y la forma en que regresa | Los decodificadores de C# y Kotlin, y `FakeHranaServer` |
| V19 | `rows_written` al insertar un trozo (se esperan 2) y `rows_read` al leer 4 trozos por llave y en `COUNT`/`SUM` de `LabBlobs` | El presupuesto de §3 |
| V20 | 100 MB con `randomblob`: `SUM(pgsize)` de `dbstat` y el panel; luego `DELETE` de 50 MB: ¿baja sin `VACUUM`? | **Crítico.** Si no baja, desalojar no libera cuota: el tope pasa a ser sobre lo acumulado y la salida es el plan Developer |
| V21 | `Content-Encoding` de la respuesta al pedir gzip y bytes reales de una lectura de 2 MB | Si la bajada cuesta +0% o +33% |
| V22 | Tiempos de 512 KB y 2 MB por el Wi-Fi del consultorio y por LTE | El tamaño de trozo y los tiempos de espera |
| V23 | Un `batch` con `BEGIN`/`COMMIT` que lleva un BLOB de 1 MB (V14 con BLOB) | Si el cierre puede ir en la misma transacción que el último trozo |

**Resultados en Turso real** (2026-10-11, base de prueba, Wi-Fi de casa; el detalle está en `TURSO_SYNC_ARCHITECTURE.md` §6):
- **V17:** de 0.5 a 32 MB pasaron todos, sin error; 32 MB tardaron 22 s. El trozo se queda en 512 KiB por la espera de 30 s, no por Turso.
- **V19:** registrar un archivo escribe 2 filas y cada trozo otras 2 (la fila y su llave, lo que también contesta V10); un trozo repetido escribe 0. Bajar 4 trozos lee 4 filas, cerrar lee 5 y escribe 1, y la suma de 50 archivos lee 50. Los números de §3 se quedan.
- **V20:** `dbstat` bajó de 102.1 a 52.1 MB al borrar la mitad, y a 0 al borrar las tablas. `page_count` no baja, pero las páginas libres se reúsan antes de que el archivo crezca, y Turso cuenta con `dbstat`. **Desalojar libera cuota:** el tope de 2 GB se queda sobre lo que hay en la nube. La PC lo mide con `(page_count − freelist_count) × page_size`, que dio lo mismo que `dbstat`.
- **V21:** sin gzip; la bajada cuesta el +33% del archivo, y el freno de bytes cuenta lo que viaja.
- **V22:** un trozo de 512 KiB sube en 0.6 s y baja en 0.4 s; uno de 2 MiB, 1.7 s y 0.9 s. Faltan el Wi-Fi del consultorio y LTE.
- **V23:** atómico. El último trozo y el cierre van en la misma transacción, y el desalojo también.
