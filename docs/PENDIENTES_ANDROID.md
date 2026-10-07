# Pendientes antes de iniciar la app Android (tablet)

Estado al 2026-10-07 (TPVC v1.10.0). Objetivo: una app para la tablet **igual a la de Windows**
(expedientes NOM-004, consulta, receta, citas y cobro), sincronizada por Turso.

Orden sugerido: primero lo **bloqueante** (si se omite, la tablet corrompe datos o duplica registros);
luego la preparación del código; al final lo que se construye en la propia app.

---

## A. Bloqueantes de datos (resolver antes de que la tablet escriba)

| # | Pendiente | Por qué bloquea | Propuesta |
|---|---|---|---|
| A1 | **Existencias derivadas de movimientos** | `Product.CurrentStock` y `ProductBatch.Quantity` se sincronizan como valores. Si la PC y la tablet venden el mismo producto sin conexión, gana la última escritura y **se pierde un descuento** (las dos ventas sí llegan como movimientos, pero el stock solo baja una vez). | Tras cada descarga, recalcular stock y lotes a partir de `InventoryMovements` (ya se sincronizan completos). Requiere un movimiento de "saldo inicial" para productos sembrados o creados antes de v1.9. |
| A2 | **Equipo secundario sin sembrado** | Al instalar, cada equipo siembra sus propias categorías y servicios clínicos con `SyncId` distintos → al sincronizar aparecen **duplicados** ("Medicamentos y Fármacos" ×2). | Modo "equipo secundario": base vacía + sincronización completa desde Turso. Alternativa: `SyncId` determinísticos para el catálogo sembrado. |
| A3 | **Folios por dispositivo** | `EXP-`, `TKT-` y `CNS-` son consecutivos por equipo; dos equipos generan el mismo folio. v1.9 evita el bloqueo agregando `-R1`, pero los folios quedan distintos entre equipos. | Prefijo de dispositivo (`TKT-T-20261007-0001`) o rango de folios asignado por equipo. |
| A4 | **Referencias dentro de JSON** | `PatientRecord.ActiveServicesJson` guarda el `Id` local del servicio clínico; en otro equipo apunta a otro servicio. | Guardar el `SyncId` del servicio dentro del JSON (migrar los existentes). |
| A5 | **Sincronización periódica** | La PC solo sincroniza al iniciar o al guardar algo; no verá lo que capture la tablet hasta su siguiente cambio local. | Sincronización en segundo plano cada 1–2 min con la app abierta (las consultas vacías casi no consumen cuota). |
| A6 | **Primera sincronización real con Turso** | Las versiones 1.8.x nunca subieron datos (HTTP 400). | En la PC: *Configuración → Nube y Turso Cloud → Sincronización Completa* (hace sincronización completa y renombra las tablas viejas a `legacy_v1_*`). Revisar en el panel de Turso que existan filas en todas las tablas. |

## B. Preparación del código compartido

| # | Pendiente | Detalle |
|---|---|---|
| B1 | **Decidir tecnología** | Recomendado: **.NET MAUI** para reutilizar modelos, base de datos, sincronización e importador (ya son .NET puro). Kotlin nativo obligaría a reimplementar el protocolo v2 completo. |
| B2 | **Extraer `TPVC.Core` (sin WPF)** | Mover `Models/`, `Data/`, `TextSearch`, `XlsxReader`, `TursoClient`, `TursoSyncService`, `InventoryService`, `DataImportService` a una biblioteca `net10.0`. Abstraer lo que hoy depende de Windows: `Application.Current.Dispatcher` (sync, impresión), `DialogService` (MessageBox), ruta de datos `%LocalAppData%` (en Android `FileSystem.AppDataDirectory`), DPAPI de tokens (en Android `SecureStorage`/Keystore). |
| B3 | **Dividir los ViewModels gigantes** | `MedicalRecordsViewModel` (~5,000 líneas) y su vista (~6,900) mezclan expediente, consulta, receta, insumos, servicios, agenda y POS. Separarlos por función permite reutilizarlos en MAUI y probarlos. |
| B4 | **Reglas de permisos por rol** | `DevicePermissions` ya define la matriz (PC / tablet / celular), pero solo es informativa: aplicarla en los servicios, no solo en la UI. |
| B5 | **Pruebas en Windows de la interfaz** | La UI no se puede ejecutar en Linux; agregar una lista de verificación manual o pruebas de humo en el runner de Windows antes de cada release. |

## C. Lo que la app de la tablet debe resolver

| # | Pendiente | Detalle |
|---|---|---|
| C1 | **Enlace por QR** | La PC ya genera el paquete de emparejamiento (`SyncProtocolVersion = 2`). Falta mostrarlo como QR y leerlo en Android. El paquete contiene el token: generar un token propio para la tablet en Turso (revocable). |
| C2 | **Impresión** | La impresión actual usa `FlowDocument` (solo Windows). En Android: receta por PDF/HTML (el exportador HTML ya existe) y ticket en impresora térmica Bluetooth (ESC/POS). |
| C3 | **Modo sin conexión** | La tablet trabaja sobre su SQLite local y sincroniza al recuperar red (mismo diseño que la PC). Indicador visible de "pendiente por subir". |
| C4 | **Interfaz táctil** | Rehacer las pantallas para tablet (MAUI XAML): consulta, receta con plantillas y calculadora pediátrica, agenda, cobro. Los atajos de teclado no aplican. Tomar como modelo la consulta de v1.10 (secciones en el orden de la entrevista, cuenta de la consulta a la derecha y ventana *Terminar consulta*); la búsqueda al escribir de las listas ya está en `TextSearch.FindBestMatchIndex` (reutilizable). |
| C5 | **Funciones solo-PC** | Importar Excel, restaurar base, configuración de Turso e inventario completo quedan en la PC (rol `WindowsDesktop`). |

## D. Verificación pendiente en Windows

### v1.10.0 (pantalla de consulta)

- [ ] Iniciar consulta: se ve la pantalla de consulta (barra con paciente, alergias y tiempo; contexto a la izquierda; 5 secciones; cuenta a la derecha) y no la ficha.
- [ ] Ctrl + ↓ / Ctrl + ↑ cambian de sección también con el cursor dentro de un cuadro de varias líneas, y el cursor queda en el primer campo de la sección.
- [ ] Diagnóstico: sugerencias CIE-10 con ↑ ↓ y Enter.
- [ ] Receta: *Se surte aquí* marcado al agregar un medicamento con existencias; desmarcarlo lo quita de la cuenta.
- [ ] + Insumo / + Servicio durante la consulta aparecen al instante en la cuenta y el total cambia.
- [ ] Posología frecuente / dosis pediátrica / medidas generales: desplegar, escribir "cada 8" y Enter.
- [ ] Terminar consulta: cuenta con honorario, ± y quitar; efectivo con cambio; *Volver a la consulta* (y Esc) regresa sin perder nada; *Cobrar y terminar* cierra la consulta y abre la selección del siguiente paciente.
- [ ] Descartar: pide confirmación y no deja nota ni cargos.
- [ ] POS → *Abrir consulta* lleva a la consulta en curso.

### v1.9.0

- [ ] Navegar Hub → Expedientes → POS → Expedientes: la pantalla conserva su estado y **Esc** solo actúa en la pantalla visible.
- [ ] Receta: selector *Posología frecuente*, calculadora *Dosis pediátrica por peso* y botón **Usar**.
- [ ] Plan terapéutico: botones rápidos y selector de medidas generales escriben en el cuadro visible.
- [ ] Reimportar `RECETA MÉDICA.xlsx` (corrige el mínimo de stock de los 259 medicamentos importados con v1.8.x y carga plantillas).
- [ ] Turso: *Sincronización Completa* sin error y, después, *Sincronizar ahora* con "0 subidos, 0 recibidos".
- [ ] Corte de caja exportado a CSV se abre en columnas en Excel.
