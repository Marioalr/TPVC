# Registro de Cambios (Changelog)

Todas las modificaciones notables de este proyecto se documentan en este archivo siguiendo los estándares de [Keep a Changelog](https://keepachangelog.com/es-ES/1.0.0/) y [Semantic Versioning](https://semver.org/lang/es/).

---

## [1.3.3] - 2026-10-03

### Corregido
* **Prevención de Insumos y Códigos Duplicados en Inventario**:
  * **Validación Estricta de Código / SKU Único**: Se implementó tanto a nivel servicio (`InventoryService.IsProductCodeTakenAsync`) como en la capa ViewModel (`ProductDialogViewModel`) una verificación insensible a mayúsculas/minúsculas que bloquea el registro o modificación de insumos si su código ya está asignado a otro artículo activo.
  * **Alertas Visuales de Validación**: Si se intenta registrar un código existente o campos obligatorios vacíos, se muestra una alerta visual destacada en color rojo con el detalle exacto de la duplicidad sin cerrar la ventana.
* **Limpieza Automática del Formulario de Alta ("Borrar lo Agregado")**:
  * Al pulsar **"Guardar Insumo"** en un nuevo registro, todos los campos capturados (código, nombre comercial, genérico, presentación, precio, existencias, ubicación y notas) se limpian automáticamente a su estado en blanco/cero.
  * Se despliega un distintivo verde de confirmación con el nombre del insumo recién guardado, permitiendo capturar de forma continua y limpia múltiples insumos sin duplicar involuntariamente los datos del anterior.
  * Botón de cierre explícito (**"Cerrar"**) con preservación del resultado para sincronizar la lista de inventario en tiempo real.

### Añadido
* **Pruebas Automatizadas de Unicidad y Reseteo**:
  * 4 nuevas pruebas unitarias: `InventoryService_IsProductCodeTakenAsync_DetectsExistingCodeCaseInsensitive`, `InventoryService_SaveProduct_PreventsDuplicateCode`, `ProductDialogViewModel_SaveAsync_PreventsDuplicateCodeAndSetsErrorMessage` y `ProductDialogViewModel_SaveAsync_ClearsFieldsOnSuccessfulSave` (suite ampliada a **28 pruebas unitarias al 100%**).
* **Actualización Global de Versión**:
  * Sincronización a **`1.3.3`** en la solución, instalador, vistas y documentación.

---

## [1.3.2] - 2026-10-03

### Corregido
* **Diagnóstico y Corrección de Verificación de Actualizaciones en Repositorios Privados**:
  * **Causa Raíz Identificada**: Por políticas de seguridad de la API de GitHub, las consultas no autenticadas a repositorios privados devuelven un código `HTTP 404 Not Found`. Anteriormente, el servicio de actualización interpretaba erróneamente cualquier `404` como *"El sistema está en su versión actual"*, enmascarando que el repositorio requería credenciales y reportando erróneamente que no había actualizaciones disponibles.
  * **Mensajes Precisos y Transparentes**: Ahora el servicio reporta con claridad si el repositorio es privado o si se alcanzó el límite de peticiones anónimas, guiando al usuario o administrador a ingresar su token.

### Añadido
* **Soporte de Autenticación con Token (GitHub PAT) para Producción Privada**:
  * Nuevo panel en `Ajustes` (**"🔒 Autenticación para Repositorio Privado en Producción"**) con campo de entrada para Personal Access Token (PAT con alcance `repo`) y botón de guardado con persistencia local en `%LocalAppData%\TPVC\update_config.json`.
  * Detección automática en cascada de tokens desde la configuración local persistida y desde las variables de entorno `GITHUB_TOKEN` o `GH_TOKEN`.
  * **Descarga Autenticada de Releases Privadas**: Implementación del flujo de descarga a través del endpoint de assets de la API de GitHub (`/releases/assets/{id}`) con cabecera `Accept: application/octet-stream` y redirección a URL firmada temporal de S3.
* **Pruebas Unitarias Ampliadas**:
  * Nuevas pruebas `AppUpdateService_DetectsNewerVersionCorrectly`, `AppUpdateService_RecognizesCurrentVersionAsUpToDate` y `AppUpdateService_TokenPersistenceAndEffectiveToken` (suite ampliada a **24 pruebas unitarias al 100%**).
* **Actualización Global de Versión**:
  * Sincronización de versión a **`1.3.2`** en proyectos, instaladores, workflows, vistas y documentación.

---

## [1.3.1] - 2026-10-03

### Añadido
* **Comprobante de Venta Simulado (Ticket POS al Despachar Venta)**:
  * Al presionar el botón **"Cobrar y Despachar Venta"**, se despliega automáticamente una vista modal que simula fielmente un **ticket o comprobante térmico de consultorio médico y farmacia**.
  * Incorporación del modelo de datos [`SaleReceipt.cs`](file:///c:/Users/salva/Documents/TPVC/Models/SaleReceipt.cs) y `SaleReceiptItem`, snapshotting de los datos cobrados:
    * **Encabezado Clínico Oficial**: Nombre del consultorio, atención médica integral, RFC, dirección, teléfono y cédula profesional del médico general.
    * **Metadatos de la Transacción**: Folio único (`TKT-YYYYMMDD-XXXX`), fecha y hora exacta, paciente/cliente, método de pago (*Efectivo*, *Tarjeta*, *Transferencia*) y responsable que atendió.
    * **Desglose de Partidas**: Tabla con cantidad vendida, nombre comercial, principio activo/presentación en cursiva, precio unitario y total por renglón.
    * **Resumen Financiero**: Conteo total de piezas, subtotal, IVA (tasa 0% medicamentos) y **TOTAL GENERAL COBRADO ($ MXN)** en gran formato.
    * **Pie de Comprobante**: Mensaje de agradecimiento, nota de seguimiento médico, aviso de dispensación sanitaria FEFO y código de barras simulado.
  * **Acciones Interactivas**:
    * Botón **"🖨️ Imprimir Ticket"**: Notificación de envío a la impresora térmica predeterminada.
    * Botón **"✓ Nueva Venta / Cerrar"**: Cierra el comprobante y prepara el carrito para el siguiente paciente.
* **Pruebas Automatizadas**:
  * Nueva prueba unitaria `PosViewModel_ProcessSale_CreatesSaleReceiptAndOpensTicketModal` que certifica la generación del ticket, concordancia de totales, folio y limpieza del carrito (suite ampliada a **21 pruebas unitarias al 100%**).
* **Actualización Global de Versión**:
  * Actualizada la versión a **`1.3.1`** en `TPVC.csproj`, `AppUpdateService`, `SettingsViewModel`, `MainViewModel`, `ConfigurationShellViewModel`, instalador y workflows.

---

## [1.3.0] - 2026-10-03

### Añadido
* **Plan de Consulta y Servicios Clínicos con Insumos Predeterminados**:
  * Implementación del modelo de datos de servicios clínicos (`ClinicalService`) y vinculación relacional con insumos y fármacos de inventario (`ServiceDefaultProduct`).
  * Navegación por pestañas en `MedicalRecordsView` para alternar fluidamente entre **"📋 Expedientes de Pacientes"** y **"🩺 Catálogo de Servicios e Insumos"**.
  * Creador interactivo de nuevos servicios médicos: permite ingresar folio/código, nombre, categoría (Consulta, Curaciones, Procedimientos, Diagnóstico, Certificados), precio oficial, duración estimada y descripción clínica.
  * Capacidad de adjuntar paquetes de insumos por defecto desde el catálogo de inventario con cantidades específicas e indicaciones (ej. Curación Menor con gasas, vendas y solución fisiológica pre-configuradas).
  * Documentación exhaustiva en `docs/PLAN_DE_CONSULTA.md` alineada a la norma oficial mexicana **NOM-004-SSA3 del Expediente Clínico**, incluyendo diagramas de secuencia y entidad-relación Mermaid.
* **Instalador Oficial de Experiencia de Prueba (Setup Windows en 3 Vistas)**:
  * Nuevo proyecto ejecutable autónomo `TPVC.Installer` empaquetado como `TPVC-Setup-v1.3.0.exe`.
  * Flujo centrado y minimalista con exactamente **3 vistas**:
    1. **Vista 1 (Inicio)**: Logotipo oficial, versión y **solo el botón de instalar** (`Instalar →`) para una experiencia de prueba ágil en 1 clic.
    2. **Vista 2 (Estado)**: Barra de progreso animada, indicador porcentual y detalle de estado en vivo (extracción, verificación y accesos directos).
    3. **Vista 3 (Finalización)**: Distintivo de éxito, **CheckBox `[X] Iniciar TPVC ahora` activado por defecto** y botón de **Finalizar**.
  * Instalación por usuario en `%LocalAppData%\Programs\TPVC` libre de advertencias UAC o necesidad de privilegios de administrador.
  * Creación automática de accesos directos `.lnk` en el Escritorio y en la carpeta de Programas del Menú Inicio de Windows.
  * Script automatizado de generación y publicación `scripts/build-installer.ps1`.
* **Actualización Global de Versión a `1.3.0`**:
  * Actualización en `TPVC.csproj`, `AppUpdateService`, `SettingsViewModel`, `MainViewModel`, `ConfigurationShellViewModel` y barra de estado inferior.
* **Suite de Pruebas Unitarias Automatizadas**:
  * Ampliación a **20 pruebas unitarias con 100% de éxito** (`InventoryTests` e `InstallerTests`), validando el ciclo de vida de servicios clínicos, insumos por defecto, converters polimórficos y la ruta de instalación del sistema.

---

## [1.2.1] - 2026-10-03

### Corregido
* **Visualización Persistente de Productos Agregados a la Venta (Carrito)**:
  * Corrección crítica en `BoolToVisibilityConverter`: anteriormente solo evaluaba instancias estrictas de tipo `bool` (`value is true`), provocando que cualquier colección con conteo entero (`Cart.Count = 1, 2, ...`) se evaluara a `false`. Esto mantenía permanentemente colapsada la tabla central de productos en el ticket y visible el mensaje de "venta vacía" desde que se introdujo el carrito.
  * El convertidor ahora evalúa de forma polimórfica y segura números enteros (`int > 0`), colecciones (`ICollection.Count > 0`), cadenas de texto no vacías y objetos de modelo no nulos (`SelectedPatient`), además de tipos booleanos.
  * Incorporación de la propiedad reactiva `HasCartItems` en `PosViewModel`, respaldada por un escuchador a `Cart.CollectionChanged` y recalculada en tiempo real al agregar, alterar cantidades, remover o limpiar partidas.
  * Vinculación tipada directa `IsEnabled="{Binding HasStock}"` en los botones de adición de productos tanto en el dropdown instantáneo como en el catálogo completo.
* **Pruebas Automatizadas**:
  * Añadidas pruebas unitarias (`BoolToVisibilityConverter_HandlesIntsStringsObjectsAndBoolsCorrectly` y `PosViewModel_HasCartItems_UpdatesReactivelyOnCartModifications`) ampliando la suite a **13 pruebas unitarias al 100%**.

---

## [1.2.0] - 2026-10-03

### Añadido
* **Navegación por Flechas del Teclado en el Buscador del POS**:
  * Navegación interactiva con las teclas de dirección (**Flecha Abajo `↓`** y **Flecha Arriba `↑`**) para alternar entre sugerencias de medicamentos en el menú desplegable en tiempo real.
  * Mantenimiento continuo del foco de escritura en la caja de texto (`SearchTextBox`), permitiendo escribir y navegar sin interrupciones ni uso forzado del ratón.
  * Resaltado visual prominente del producto activo mediante borde izquierdo de acento y fondo sutil con scroll automático (`ScrollIntoView`).
  * Confirmación y despacho instantáneo al ticket de venta presionando la tecla **`Enter`**.
  * Cancelación y cierre instantáneo del menú desplegable mediante la tecla **`Escape` (`Esc`)**.
* **Actualización Global de Versión a `1.2.0`**:
  * Sincronización completa del número de versión a `v1.2.0` en `TPVC.csproj`, `AppUpdateService`, `SettingsViewModel`, `MainViewModel`, `ConfigurationShellViewModel` y en la barra de estado inferior visible en la aplicación.
* **Pruebas Unitarias Automatizadas**:
  * Nueva prueba unitaria `PosViewModel_ArrowKeyNavigation_NavigatesAndSelectsSuggestionsCorrectly` para certificar la navegación hacia abajo, hacia arriba, ciclado y confirmación directa con `Enter` en el carrito (suite ampliada a 11 pruebas pasando exitosamente).

---

## [1.1.0] - 2026-10-03

### Añadido
* **Nueva Vista Principal con Tres Tarjetas de Acción Directa**:
  * Diseño libre de distracciones con **tres tarjetas de gran formato (cards)**: **Nueva Venta** (POS con Carrito), **Nueva Consulta** (Expedientes Clínicos TODO) y **Configuración** (Gestión y Administración).
  * Traslado del **Panel Lateral de Navegación** dentro del módulo de **Configuración**, liberando la ventana principal para una experiencia a pantalla completa limpia y centrada.
  * Botón de navegación **"← Volver a Inicio"** integrado en las cabeceras de POS, Expedientes y en el Panel Lateral de Configuración para un flujo fluido sin pérdida de estado.
* **Presentación Prominente del TODO en Nueva Consulta**:
  * Banner hero en gran formato con distintivo `⚠️ MÓDULO EN DESARROLLO (TODO)`, especificación de integración con la norma oficial NOM-004-SSA3, signos vitales, notas de evolución y recetas con QR.
  * Modales informativos enriquecidos al pulsar sobre *Nueva Consulta* o *Nuevo Expediente*.
* **Visualización de Versión Precisa y Constante**:
  * Despliegue permanente de la versión exacta de la aplicación (`v1.1.0`) en la barra inferior de estado junto al título y el indicador de datos sincronizados, así como en los ajustes del sistema.
* **Rediseño Integral del Punto de Venta (POS / Farmacia)**:
  * Sustitución del panel lateral de carrito por una **tabla central dedicada al ticket de venta**, optimizando el espacio de visualización para nombres comerciales, principios activos, SKU, stock disponible, precio unitario, stepper reactivo `+`/`-`, subtotal y eliminación.
  * **Búsqueda Instantánea con Menú Desplegable**: Barra superior con autocompletado en tiempo real que lista productos sugeridos con semáforo de existencias y precios, permitiendo agregar al ticket en un clic o con `Enter`.
  * **Ventana Modal de Catálogo Completo**: Botón dedicado *"Buscar en Catálogo"* para explorar toda la farmacia médica por categorías con filtros rápidos y botón de adición continua.
  * **Zona de Cobro en la Esquina Inferior Derecha**: Despliegue de paciente/cliente, método de pago, contador de artículos, **TOTAL A PAGAR** destacado y botón prioritario **"Cobrar y Despachar Venta"**.
  * Deducción de existencias en tiempo real mediante el algoritmo sanitario **FEFO** (*First Expired, First Out*) y registro automático en el Kardex como `SalidaVenta`.
* **Módulo de Expedientes Clínicos (TODO)**:
  * Visualizador maestro-detalle de expedientes médicos de pacientes con datos demográficos, signos vitales, antecedentes y alertas de alergias.
  * Botón interactivo *"Cargar a Venta (POS)"* que transfiere los medicamentos de la receta activa del paciente directamente al ticket del punto de venta.
* **Suite de Pruebas Unitarias Ampliada**:
  * Cobertura expandida a **10 pruebas unitarias automatizadas** (validación de ventas POS, cálculos reactivos del ticket, sugerencias de búsqueda en tiempo real, filtrado en modal de catálogo y despacho de recetas).

---

## [1.0.0] - 2026-10-03

### Añadido
* **Módulo de Inventario Clínico para Médico General**:
  * Catálogo de productos e insumos con soporte para nombre comercial, principio activo (genérico), presentación, unidad, stock actual, umbral de stock mínimo de alerta, ubicación física dentro del consultorio y marca de conservación en frío (refrigeración ❄️).
  * Categorías médicas precargadas: *Medicamentos y Fármacos*, *Material de Curación*, *Insumos Clínicos Descartables*, *Antisépticos y Soluciones*, *Diagnóstico y Exploración*.
* **Control Riguroso de Lotes y Caducidades**:
  * Trazabilidad por número de lote y fecha de vencimiento.
  * Semáforo visual automático: Vigente (verde), Próximo a vencer en ≤60 días (ámbar) y Vencido (rojo).
  * Salida inteligente **FEFO** (*First Expired, First Out*): descuenta prioritariamente del lote con caducidad más cercana al registrar consumos.
* **Kardex y Registro de Movimientos**:
  * Historial auditable de entradas (compras, reposición) y salidas (consumo en consulta, mermas, caducidades) con registro de fecha, hora, responsable y procedimiento médico.
* **Acción Rápida de Consumo en Consulta**:
  * Flujo simplificado para registrar en 2 clics los insumos utilizados en pacientes durante la consulta médica diaria.
* **Sincronización Automática en Tiempo Real**:
  * Despacho reactivo de eventos: cualquier modificación en el catálogo, lotes o movimientos actualiza instantáneamente todos los paneles y métricas sin requerir interacción manual del usuario.
  * Indicador de estado sutil en la barra inferior de la aplicación (`● Datos sincronizados`).
* **Motor de Temas y Personalización Visual**:
  * Modos de fondo: *Modo Claro Clínico*, *Modo Oscuro* y *Modo Personalizado* (códigos Hexadecimales libres para fondo y tarjetas).
  * Paleta médica de colores de acento (*Cian Médico*, *Azul Clínico*, *Verde Quirúrgico*, *Esmeralda*, *Violeta*, *Rosa Clínico*, *Ámbar*, *Grafito*) y selector Hex libre.
  * Persistencia automática de temas en archivo local `theme.json`.
* **Almacenamiento Local Seguro en SQLite**:
  * Base de datos local `consultorio.db` gestionada mediante Entity Framework Core 10.
  * Migraciones y sembrado inicial automático en `%LocalAppData%\TPVC\`.
* **Actualizador Integrado de Software**:
  * Módulo exclusivo dentro de *Ajustes* para comprobar y descargar nuevas versiones de la aplicación desde GitHub de forma automática y directa.
* **Suite de Pruebas Unitarias Automatizadas**:
  * 5 pruebas unitarias con MSTest validando inicialización de datos, cálculo de métricas, lógica de vencimientos y consumo FEFO.
