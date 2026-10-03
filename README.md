# TPVC - Sistema de Inventario para Consultorio Médico

[![CI](https://github.com/Marioalr/TPVC/actions/workflows/ci.yml/badge.svg)](https://github.com/Marioalr/TPVC/actions/workflows/ci.yml)
[![Version](https://img.shields.io/badge/version-v1.1.0-blue.svg)](https://github.com/Marioalr/TPVC/releases/tag/v1.0.0)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D7.svg)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Database](https://img.shields.io/badge/database-SQLite-003B57.svg)](https://www.sqlite.org/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Sistema integral de gestión de consulta, expedientes, punto de venta e inventario clínico exclusivo para **Windows**, concebido para consultorios de **Médico General** (fármacos, material de curación, insumos descartables, diagnóstico y exploración). Desarrollado en **C# (.NET 10 / WPF)** con base de datos local **SQLite** (sin necesidad de configurar servidores externos) y motor de personalización visual en tiempo real.

---

## 📚 Documentación del Proyecto

* 📖 **[Manual de Usuario (docs/MANUAL_DE_USUARIO.md)](docs/MANUAL_DE_USUARIO.md)**: Guía práctica completa para el personal del consultorio (manejo de la vista principal dividida, punto de venta con carrito, expedientes, lotes y temas).
* 🏗️ **[Arquitectura Técnica (docs/ARQUITECTURA_TECNICA.md)](docs/ARQUITECTURA_TECNICA.md)**: Diagramas Mermaid, modelo de base de datos relacional, algoritmo de venta POS FEFO y especificaciones de ingeniería.
* 📝 **[Historial de Versiones (CHANGELOG.md)](CHANGELOG.md)**: Registro de cambios detallado siguiendo *Semantic Versioning*.
* ⚖️ **[Licencia MIT](LICENSE)**: Términos y condiciones de uso del software.

---

## 🌟 Características Principales

### 1. Nueva Vista Principal: Hub Central de Tres Tarjetas (Cards)
- **Flujo de Trabajo Médico Limpio y Enfocado**: La vista principal presenta una interfaz despejada a pantalla completa con tres accesos directos de gran tamaño tipo tarjeta (*cards*):
  - **Card 🛒 "Nueva Venta"**: Acceso inmediato al **Punto de Venta con Carrito (POS)** para búsqueda, dispensación y cobro de medicamentos a pantalla completa.
  - **Card 📋 "Nueva Consulta"**: Acceso directo al módulo de **Expedientes Clínicos con distintivo TODO en gran formato**, visualizador interactivo y hoja de ruta de la norma NOM-004-SSA3.
  - **Card ⚙️ "Configuración"**: Acceso al centro de gestión y administración que **alberga el panel lateral** con Métricas, Inventario, Lotes (FEFO), Kardex y Temas/Ajustes.
- **Navegación Intuitiva**: Todas las vistas incluyen botón de retorno rápido (*"← Volver a Inicio"*).
- **Interconexión Clínica**: Despache las recetas de los pacientes directamente al carrito de compras con el botón *"Cargar a Venta (POS)"*.

### 2. Punto de Venta (POS / Farmacia)
- **Área Central de Ticket**: Visualización principal en formato de tabla para los productos agregados a la venta actual (nombre comercial, genérico, SKU, stock disponible, precio unitario, stepper interactivo de cantidades `+`/`-`, subtotal y eliminación).
- **Búsqueda Instantánea con Menú Desplegable**: Al teclear en la barra superior se despliega en tiempo real un menú flotante con los productos coincidentes, stock y precios para agregarlos con un solo clic o presionando `Enter`.
- **Ventana Modal de Catálogo Completo**: Botón dedicado *"Buscar en Catálogo"* que abre una ventana emergente donde se exploran todos los productos por categoría, con badges de stock, temperatura (❄️) y botones para seguir agregando.
- **Cobro en la Esquina Inferior Derecha**:
  - Resumen de paciente/cliente, método de pago (*Efectivo*, *Tarjeta*, *Transferencia*) y contador de artículos.
  - **TOTAL A PAGAR** destacado en tipografía de gran formato junto al botón prioritario **"Cobrar y Despachar Venta"**.
  - Deducción automática de inventario mediante la regla sanitaria **FEFO** (*First Expired, First Out*) y registro inmutable en el Kardex (`SalidaVenta`).

### 3. Módulo de Expedientes Clínicos (TODO)
- **Ficha Integral del Paciente**: Registro de folio, datos demográficos, signos vitales de la última consulta, diagnóstico médico, antecedentes y notas de evolución.
- **Alerta de Alergias**: Resalta en rojo alergias medicamentosas de alto riesgo (ej. *Penicilina*, *Sulfas*, *AINEs*).
- **Recetas Prescritas**: Lista de fármacos y dosificación con enlace directo de despacho al carrito del POS.
- **Avisos Informativos**: Notificaciones sobre la próxima integración con la norma oficial NOM-004-SSA3.

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
- Almacenamiento local en `%LocalAppData%\TPVC\consultorio.db`.
- Totalmente autónomo: no requiere internet ni configuración de servidores externos.
- Sincronización automática reactiva con indicador discreto en la barra inferior (`● Datos sincronizados`).

---

## 🛠️ Tecnologías y Arquitectura

* **Plataforma**: Windows Desktop (WPF nativo)
* **Framework**: .NET 10 (C# 13 / 14)
* **Patrón de Arquitectura**: MVVM con `CommunityToolkit.Mvvm`
* **Inyección de Dependencias**: `Microsoft.Extensions.DependencyInjection`
* **Persistencia / ORM**: Entity Framework Core 10 (`Microsoft.EntityFrameworkCore.Sqlite`)
* **Pruebas Automatizadas**: MSTest v3.5 con 8 pruebas unitarias de integración

---

## 🚀 Instalación y Puesta en Marcha

### Requisitos Previos
* Sistema Operativo: **Windows 10** o **Windows 11** (x64)
* Runtime / SDK: [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

### Compilación desde el Código Fuente
```powershell
# 1. Clonar el repositorio
git clone https://github.com/Marioalr/TPVC.git
cd TPVC

# 2. Restaurar dependencias
dotnet restore TPVC.sln

# 3. Compilar la solución
dotnet build TPVC.sln -c Release

# 4. Ejecutar las pruebas unitarias
dotnet test TPVC.sln

# 5. Iniciar la aplicación
dotnet run --project TPVC.csproj
```

### Publicar Ejecutable Independiente
```powershell
dotnet publish TPVC.csproj -c Release -r win-x64 --self-contained false -o ./publish/TPVC-win-x64
```

---

## 👨‍⚕️ Contribución y Soporte

Para reportar problemas o solicitar mejoras clínicas, cree un Issue en el repositorio de GitHub o consulte la [Arquitectura Técnica](docs/ARQUITECTURA_TECNICA.md).

---

## 📄 Licencia

Este proyecto está distribuido bajo los términos de la Licencia MIT. Consulte el archivo [LICENSE](LICENSE) para más detalles.
