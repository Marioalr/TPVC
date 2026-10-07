# TPVC - Sistema de Inventario para Consultorio Médico

[![CI](https://github.com/Marioalr/TPVC/actions/workflows/ci.yml/badge.svg)](https://github.com/Marioalr/TPVC/actions/workflows/ci.yml)
[![Version](https://img.shields.io/badge/version-v1.12.0-blue.svg)](https://github.com/Marioalr/TPVC/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D7.svg)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Database](https://img.shields.io/badge/database-SQLite-003B57.svg)](https://www.sqlite.org/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Sistema integral de gestión de consulta, expedientes, punto de venta e inventario clínico exclusivo para **Windows**, concebido para consultorios de **Médico General** (fármacos, material de curación, insumos descartables, diagnóstico y exploración). Desarrollado en **C# (.NET 10 / WPF)** con base de datos local **SQLite** (sin necesidad de configurar servidores externos) y motor de personalización visual en tiempo real.

---

## 📚 Documentación del Proyecto

* 🩺 **[Plan de Consulta y Servicios Clínicos (docs/PLAN_DE_CONSULTA.md)](docs/PLAN_DE_CONSULTA.md)**: Hoja de ruta conforme a la norma técnica mexicana NOM-004-SSA3, catálogo de servicios médicos, paquetes con insumos por defecto y diagramas Mermaid.
* 📖 **[Manual de Usuario (docs/MANUAL_DE_USUARIO.md)](docs/MANUAL_DE_USUARIO.md)**: Guía práctica completa para el personal del consultorio (manejo de la vista principal dividida, punto de venta con carrito, expedientes, lotes y temas).
* 🏗️ **[Arquitectura Técnica (docs/ARQUITECTURA_TECNICA.md)](docs/ARQUITECTURA_TECNICA.md)**: Diagramas Mermaid, modelo de base de datos relacional, algoritmo de venta POS FEFO y especificaciones de ingeniería.
* ☁️ **[Sincronización con Turso (docs/TURSO_SYNC_ARCHITECTURE.md)](docs/TURSO_SYNC_ARCHITECTURE.md)**: Protocolo v2 multi-dispositivo (Windows / Android), esquema remoto y garantías.
* 📱 **[Pendientes antes de la app Android (docs/PENDIENTES_ANDROID.md)](docs/PENDIENTES_ANDROID.md)**: Bloqueantes de datos, preparación del código compartido y alcance de la tablet.
* 🎨 **[Plan de rediseño de la interfaz (docs/PLAN_REDISENO_UI.md)](docs/PLAN_REDISENO_UI.md)**: Principios, fases (expediente, POS, configuración, revisión de temas) y propuesta visual.
* 📝 **[Historial de Versiones (CHANGELOG.md)](CHANGELOG.md)**: Registro de cambios detallado siguiendo *Semantic Versioning*.
* ⚖️ **[Licencia MIT](LICENSE)**: Términos y condiciones de uso del software.

---

## 🌟 Características Principales

### 1. Nueva Vista Principal: Hub Central de Tres Tarjetas (Cards)
- **Flujo de Trabajo Médico Limpio y Enfocado**: La vista principal presenta una interfaz despejada a pantalla completa con tres accesos directos de gran tamaño tipo tarjeta (*cards*):
  - **Card 🛒 "Nueva Venta"**: Acceso inmediato al **Punto de Venta con Carrito (POS)** para búsqueda, dispensación y cobro de medicamentos a pantalla completa.
  - **Card 📋 "Nueva Consulta"**: Acceso directo al módulo de **Expedientes Clínicos y Consultas Médicas Operativo (NOM-004-SSA3)** para gestión de fichas, registro de signos vitales, somatometría y notas de evolución.
  - **Card ⚙️ "Configuración"**: Acceso al centro de gestión y administración que **alberga el panel lateral** con Métricas, Inventario, Lotes (FEFO), Kardex y Temas/Ajustes.
- **Navegación Intuitiva**: Todas las vistas incluyen botón de retorno rápido (*"← Volver a Inicio"*).
- **Interconexión Clínica**: Desde el Punto de Venta, *"Abrir consulta"* lleva a la consulta en curso. La consulta nunca manda al POS: todo lo que se usa con el paciente se cobra al terminarla.

### 2. Punto de Venta (POS / Farmacia)
- **Área Central de Ticket**: Visualización principal en formato de tabla para los productos agregados a la venta actual (nombre comercial, genérico, SKU, stock disponible, precio unitario, stepper interactivo de cantidades `+`/`-`, subtotal y eliminación).
- **Búsqueda Instantánea con Menú Desplegable y Navegación por Flechas**: Al teclear en la barra superior se despliega en tiempo real un menú flotante con los productos coincidentes. Permite desplazarse entre las sugerencias usando las teclas de dirección (**Flecha Abajo `↓`** y **Flecha Arriba `↑`**), resaltar el producto deseado y agregarlo de inmediato al ticket con **`Enter`** (o con un clic directo), o cerrar el menú con **`Escape` (`Esc`)** manteniendo siempre el foco de escritura.
- **Ventana Modal de Catálogo Completo**: Botón dedicado *"Buscar en Catálogo"* que abre una ventana emergente donde se exploran todos los productos por categoría, con badges de stock, temperatura (❄️) y botones para seguir agregando.
- **Cobro en la Esquina Inferior Derecha**:
  - Resumen de paciente/cliente, método de pago (*Efectivo*, *Tarjeta*, *Transferencia*) y contador de artículos.
  - **TOTAL A PAGAR** destacado en tipografía de gran formato junto al botón prioritario **"Cobrar y Despachar Venta"**.
  - Deducción automática de inventario mediante la regla sanitaria **FEFO** (*First Expired, First Out*) y registro inmutable en el Kardex (`SalidaVenta`).
- **Simulador de Ticket de Compra al Despachar**: Al presionar *"Cobrar y Despachar Venta"*, se despliega automáticamente una ventana que simula un **ticket térmico de consultorio y farmacia** con encabezado oficial, RFC, cédula profesional, folio secuencial, fecha/hora, desglose de medicamentos con presentación y cantidades, subtotal, IVA tasa 0%, total cobrado, código de barras simulado y opciones interactivas para **Imprimir Ticket** o **Finalizar / Nueva Venta**.

### 3. Expedientes Clínicos y Consultas Médicas (NOM-004-SSA3-2012)
- **Gestión Completa de Expedientes en SQLite**: Alta y edición de fichas de pacientes con generación automática de folio (`EXP-YYYY-NNN`), fecha de nacimiento con edad calculada (meses en bebés), tipo de sangre, contactos de emergencia y antecedentes heredofamiliares/patológicos. Solo se piden los antecedentes que corresponden al sexo y la edad (gineco-obstétricos en mujeres de 10 años o más, desarrollo en menores) y nada se llena con valores supuestos.
- **Alerta Roja de Alergias Medicamentosas**: Resalta prominentemente en el expediente cualquier hipersensibilidad o alergia a fármacos para prevenir prescripciones de riesgo.
- **Pantalla de Consulta en Curso (v1.10)**: cinco secciones en el orden de la entrevista (motivo, signos vitales, exploración, diagnóstico CIE-10 y tratamiento) que se abren una a la vez, con frases frecuentes de un clic, aviso de signos vitales fuera de rango, IMC automático y atajos **Ctrl + ↓/↑** (sección) y **Ctrl + Enter** (terminar).
- **Guardado automático (v1.11)**: la consulta en curso se guarda sola mientras se escribe; si se cierra el programa o se va la luz, se retoma al abrir con su nota, signos, receta, cuenta y hora de inicio.
- **Cuenta de la Consulta**: honorario, medicamentos que se surten en el consultorio (casilla *"Se surte aquí"*, marcada por defecto si hay existencias), insumos y servicios (inyección, curación, revisión especial) se agregan en cuanto se usan; al **Terminar consulta** se revisa la cuenta y se cobra en una sola ventana.
- **Listas que buscan al escribir**: en posología, dosis pediátrica y medidas generales se despliega la lista, se escribe y el cursor salta a la opción (sin acentos ni mayúsculas), Enter la elige.
- **Historial Cronológico de Consultas**: Pestaña dedicada para revisar todas las evoluciones y notas clínicas previas del paciente.
- **Prescripción**: Recetas con plantillas de posología, calculadora pediátrica por peso, alerta de alergias y entrega en el consultorio cargada a la cuenta de la consulta.
- **Catálogo de Servicios Clínicos y Paquetes de Insumos**: Pestaña dedicada para explorar y crear servicios médicos (Consultas, Curaciones, Suturas, Glucemia, Certificados) con insumos predeterminados vinculados.

### 4. Panel de Métricas y KPIs (Dashboard)
- **KPIs Clínicos en Vivo**: Total de insumos registrados, alertas de stock bajo o crítico, conteo de lotes próximos a vencer y consumo en consulta.
- **Acceso Rápido a Consumo en Consulta**: Modal para registrar en 2 pasos el material utilizado durante procedimientos médicos.
- **Semáforo de Alertas**: Panel visual que destaca insumos en riesgo de desabasto y medicamentos próximos a caducar.

### 5. Catálogo de Inventario e Insumos
- Ficha técnica completa de insumos:
  - Código / SKU escaneable.
  - Nombre comercial y **Principio Activo (Genérico)** (ej. *Paracetamol*, *Amoxicilina*).
  - Presentación, unidad y **Precio de venta unitario ($ MXN)**.
  - Categorización preconfigurada: *Medicamentos*, *Material de Curación*, *Insumos Descartables*, *Antisépticos*, *Diagnóstico*.
  - Stock actual, nivel mínimo de alerta y nivel óptimo de reposición.
  - Ubicación física dentro del consultorio y marca de refrigeración (cadena de frío ❄️).
- **Filtros Rápidos**: *"Todos"*, *"Solo Stock Bajo ⚠️"*, *"Próximos a Caducar ⏳"*.

### 6. Control de Lotes y Semáforo de Caducidad (FEFO)
- Trazabilidad sanitaria por número de lote y fecha de vencimiento.
- Clasificación visual cromática automática:
  - 🟢 **Vigente**: Con más de 60 días antes de su vencimiento.
  - 🟡 **Próximo a Vencer**: Con 60 días o menos de vida útil (con contador de días restantes).
  - 🔴 **Vencido**: Medicamentos o insumos caducados para baja inmediata.

### 7. Kardex y Auditoría de Movimientos
- Registro inmutable de cada operación:
  - Entrada por compra o reposición.
  - Salida por consumo en consulta médica.
  - Salida por venta en mostrador / POS.
  - Salida por caducidad o merma/daño.
  - Ajuste derivado de conteo físico.
- Trazabilidad con fecha, hora, responsable y procedimiento médico realizado.

### 8. Motor de Temas y Personalización Visual
- **Modos de Fondo**: *Modo Claro Clínico*, *Modo Oscuro* y *Modo Personalizado* (códigos Hexadecimales independientes para fondo y tarjetas).
- **Colores de Acento**: Paleta médica (*Cian Médico*, *Azul Clínico*, *Verde Quirúrgico*, *Esmeralda*, *Violeta*, *Rosa Clínico*, *Ámbar* y *Grafito*) y selector Hex libre.
- **Persistencia Local**: La preferencia se almacena en `theme.json` y se recuerda en cada inicio.

### 9. Base de Datos Local y Segura (SQLite)
- Almacenamiento local en `%LocalAppData%\TPVC\consultorio.db` en modo **WAL** (escrituras rápidas y lecturas concurrentes).
- Totalmente autónomo: no requiere internet ni configuración de servidores externos.
- **Respaldo automático diario** en `%LocalAppData%\TPVC\backups` (se conservan 14) y botón **Respaldar Ahora** en Ajustes.
- **Migración automática del esquema**: al actualizar, las tablas y columnas nuevas se agregan sin tocar los datos existentes.
- **Lista para sincronizar**: cada registro tiene un identificador global (`SyncId`), marca de modificación UTC y registro de borrados, base para un futuro servidor con varios dispositivos (Windows / Android).
- Bitácora de errores en `%LocalAppData%\TPVC\logs` para diagnóstico.

### 10. Instalador Oficial en 3 Vistas (Experiencia de Prueba)
- **Instalador Oficial para Windows (`TPVC-Setup-v1.3.0.exe`)**: Diseñado para una experiencia de evaluación ultra rápida y sin fricción.
- **Flujo en 3 Pantallas**:
  1. **Vista 1 (Inicio)**: Logotipo y **solo el botón de instalar** (`Instalar →`).
  2. **Vista 2 (Estado)**: Barra de progreso dinámica e informe en tiempo real de operaciones de desempaquetado.
  3. **Vista 3 (Finalización)**: Confirmación de éxito, **CheckBox `[X] Iniciar TPVC ahora` marcado por defecto** y botón de **Finalizar**.
- **Instalación Segura por Usuario**: Se instala en `%LocalAppData%\Programs\TPVC`, sin requerir permisos elevados de administrador ni cuadros de elevación UAC.
- **Accesos Directos Automáticos**: Genera accesos directos en el Escritorio y Menú Inicio.

---

## 🛠️ Tecnologías y Arquitectura

* **Plataforma**: Windows Desktop (WPF nativo)
* **Framework**: .NET 10 (C# 13 / 14)
* **Patrón de Arquitectura**: MVVM con `CommunityToolkit.Mvvm`
* **Inyección de Dependencias**: `Microsoft.Extensions.DependencyInjection`
* **Persistencia / ORM**: Entity Framework Core 10 (`Microsoft.EntityFrameworkCore.Sqlite`)
* **Acceso a Datos**: `IDbContextFactory` con un contexto de vida corta por operación (lecturas sin tracking, escrituras atómicas)
* **Pruebas Automatizadas**: MSTest con **181 pruebas** unitarias y de integración

---

## 🚀 Instalación y Puesta en Marcha

### Requisitos Previos
* Sistema Operativo: **Windows 10** o **Windows 11** (x64)
* Runtime / SDK: [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

### Opción A: Instalador de Experiencia de Prueba (Recomendado)
Descargue `TPVC-Setup-vX.Y.Z.exe` desde [Releases](https://github.com/Marioalr/TPVC/releases/latest), ejecútelo, pulse **Instalar** y finalice para abrir la aplicación. Cada release incluye `SHA256SUMS-vX.Y.Z.txt` para verificar la descarga.

### Opción B: Compilación y Empaquetado del Instalador desde el Código Fuente
```powershell
# 1. Clonar el repositorio
git clone https://github.com/Marioalr/TPVC.git
cd TPVC

# 2. Restaurar dependencias y ejecutar pruebas unitarias
dotnet restore TPVC.sln
dotnet test TPVC.sln

# 3. Compilar la aplicación y generar el Instalador Oficial (Setup .exe), paquete .zip y SHA256SUMS
#    (la versión se toma de TPVC.csproj; puede forzarse con -Version "X.Y.Z")
powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1

# Los archivos quedarán en: publish/
```

### Publicar una nueva versión
1. Actualice `<Version>` en `TPVC.csproj` y agregue la sección `## [X.Y.Z]` en `CHANGELOG.md`.
2. Cree y suba el tag: `git tag vX.Y.Z && git push origin vX.Y.Z`.
3. El workflow **Release** compila en Windows, ejecuta las pruebas, empaqueta y publica el release con las notas del CHANGELOG y las sumas SHA-256.

---

## 🔒 Actualizaciones en Producción (Repositorio Privado con Token)

Por políticas de seguridad de GitHub, cuando el repositorio de código fuente y releases está configurado como **Privado**, las consultas públicas de actualización devuelven un código `HTTP 404 No Encontrado` para no revelar la existencia del proyecto a terceros.

Para habilitar la comprobación y descarga automática de versiones en los equipos del consultorio:

1. **Generar un Personal Access Token (PAT) en GitHub**:
   - Vaya a GitHub: `Settings` → `Developer Settings` → `Personal access tokens` → `Tokens (classic)` (o Fine-grained).
   - Genere un nuevo token con permiso/alcance **`repo`** (o `read:packages`).
2. **Configuración en la Aplicación**:
   - **Desde la Interfaz Gráfica**: Abra TPVC → **Configuración** → **Ajustes** → Sección *"🔒 Autenticación para Repositorio Privado en Producción"*, pegue el token y haga clic en **"Guardar Token"**. Se guarda cifrado con DPAPI de Windows (solo legible por su usuario) en `%LocalAppData%\TPVC\update_config.json`.
   - **Mediante Variable de Entorno**: En despliegues desatendidos o automatizados, declare la variable de entorno del sistema `GITHUB_TOKEN` o `GH_TOKEN` con el valor del token.
3. **Descarga Segura de Paquetes**:
   - La aplicación detecta el token y descarga automáticamente los instaladores `.exe` autenticados mediante el endpoint de assets de la API de GitHub (`Accept: application/octet-stream`).
   - Antes de ejecutar el instalador se verifica su **SHA-256** contra el digest que publica GitHub; si no coincide, la descarga se descarta.
   - *(En caso de que el repositorio se configure como Público en el futuro, no se requiere ningún token y las actualizaciones funcionarán abiertamente para todos los usuarios).*

---

## 👨‍⚕️ Contribución y Soporte

Para reportar problemas o solicitar mejoras clínicas, cree un Issue en el repositorio de GitHub o consulte la [Arquitectura Técnica](docs/ARQUITECTURA_TECNICA.md).

---

## 📄 Licencia

Este proyecto está distribuido bajo los términos de la Licencia MIT. Consulte el archivo [LICENSE](LICENSE) para más detalles.
