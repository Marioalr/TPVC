# TPVC - Sistema de Inventario para Consultorio Médico

[![CI](https://github.com/Marioalr/TPVC/actions/workflows/ci.yml/badge.svg)](https://github.com/Marioalr/TPVC/actions/workflows/ci.yml)
[![Version](https://img.shields.io/badge/version-v1.0.0-blue.svg)](https://github.com/Marioalr/TPVC/releases/tag/v1.0.0)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D7.svg)](https://www.microsoft.com/windows)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Database](https://img.shields.io/badge/database-SQLite-003B57.svg)](https://www.sqlite.org/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Sistema de control y trazabilidad de inventario exclusivo para **Windows**, concebido para consultorios de **Médico General** (fármacos, material de curación, insumos descartables, diagnóstico y exploración). Desarrollado con tecnología moderna en **C# (.NET 10 / WPF)** con base de datos local **SQLite** (sin necesidad de configurar servidores externos) y motor de personalización visual en tiempo real.

---

## 📚 Documentación del Proyecto

* 📖 **[Manual de Usuario (docs/MANUAL_DE_USUARIO.md)](docs/MANUAL_DE_USUARIO.md)**: Guía práctica completa para el personal del consultorio (manejo del panel, consumo en consulta, lotes, reportes y temas).
* 🏗️ **[Arquitectura Técnica (docs/ARQUITECTURA_TECNICA.md)](docs/ARQUITECTURA_TECNICA.md)**: Diagramas Mermaid, modelo de base de datos relacional, patrón FEFO y especificaciones de ingeniería.
* 📝 **[Historial de Versiones (CHANGELOG.md)](CHANGELOG.md)**: Registro de cambios detallado siguiendo *Semantic Versioning*.
* ⚖️ **[Licencia MIT](LICENSE)**: Términos y condiciones de uso del software.

---

## 🌟 Características Principales

### 1. Sincronización Automática en Tiempo Real
- **Reactividad Inmediata**: Cualquier cambio en el catálogo, alta de lote o registro de consumo actualiza automáticamente todos los paneles e indicadores sin necesidad de presionar botones de refresco.
- **Barra de Estado Inferior Sutil**: Indicador discreto en el pie de la aplicación (`● Datos sincronizados`) que confirma el estado operativo y el motor de base de datos local activo.

### 2. Panel de Control (Dashboard) y Registro Rápido
- **KPIs Clínicos en Vivo**: Total de insumos registrados, alertas de stock bajo o crítico, conteo de lotes próximos a vencer y total de unidades consumidas en la consulta de hoy.
- **Acceso Rápido a Consumo en Consulta**: Modal simplificado para registrar en 2 pasos el material utilizado durante la atención a pacientes (descontando existencias de forma inteligente mediante FEFO).
- **Semáforo de Alertas**: Panel visual que destaca insumos en riesgo de desabasto y medicamentos próximos a caducar.

### 3. Catálogo Especializado para Medicina General
- Ficha técnica completa de insumos:
  - Código / SKU escaneable.
  - Nombre comercial y **Principio Activo (Genérico)** (ej. *Paracetamol*, *Amoxicilina*).
  - Presentación y unidad (tabletas, frascos, ampolletas, piezas, cajas).
  - Categorización preconfigurada: *Medicamentos y Fármacos*, *Material de Curación*, *Insumos Clínicos Descartables*, *Antisépticos y Soluciones*, *Diagnóstico y Exploración*.
  - Stock actual, nivel mínimo de alerta y nivel óptimo de reposición.
  - Ubicación física dentro del consultorio (*Gabinete A*, *Botiquín de Urgencias*, *Vitrina*) y marca de refrigeración (cadena de frío ❄️).
- **Búsqueda Reactiva**: Filtrado instantáneo por nombre comercial, principio activo o código mientras se escribe.
- **Filtros Rápidos de 1 Clic**: Acceso inmediato a *"Solo Stock Bajo ⚠️"* y *"Próximos a Caducar ⏳"*.

### 4. Control de Lotes y Semáforo de Caducidad (FEFO)
- Trazabilidad sanitaria por número de lote y fecha de vencimiento.
- Clasificación visual cromática automática:
  - 🟢 **Vigente**: Con más de 60 días antes de su vencimiento.
  - 🟡 **Próximo a Vencer**: Con 60 días o menos de vida útil (con contador de días restantes).
  - 🔴 **Vencido**: Medicamentos o insumos caducados para baja inmediata.
- Salida inteligente **FEFO** (*First Expired, First Out*): el sistema descuenta prioritariamente del lote con fecha de expiración más próxima al registrar consumos.

### 5. Kardex y Auditoría de Movimientos
- Registro inmutable de cada operación:
  - Entrada por compra o factura.
  - Entrada por reposición.
  - Salida por consumo en consulta médica.
  - Salida por caducidad o merma/daño.
  - Ajuste derivado de conteo físico.
- Trazabilidad con fecha, hora, responsable y procedimiento médico realizado.

### 6. Motor de Temas y Personalización Visual
- **Modos de Fondo**:
  - ☀️ **Modo Claro Clínico**: Interfaz clara de alto contraste.
  - 🌙 **Modo Oscuro**: Fondo oscuro diseñado para disminuir la fatiga visual.
  - 🎨 **Modo Personalizado**: Selector de colores Hexadecimales independientes para el fondo de ventana y tarjetas.
- **Colores de Acento**:
  - Paleta médica: *Cian Médico* (`#0284C7`), *Azul Clínico* (`#2563EB`), *Verde Quirúrgico* (`#0D9488`), *Esmeralda* (`#059669`), *Violeta* (`#7C3AED`), *Rosa Clínico* (`#E11D48`), *Ámbar* (`#D97706`) y *Grafito* (`#475569`).
  - Campo Hexadecimal libre para aplicar cualquier tono corporativo.
- **Persistencia Local**: La preferencia se almacena en `theme.json` y se recuerda en cada inicio.

### 7. Actualizador de Software Integrado
- Ubicado de forma limpia y exclusiva en la pantalla de **Ajustes**.
- Comprueba directamente con GitHub Releases si existen nuevas versiones de la aplicación.
- Permite descargar la nueva versión de manera directa y guiada.

### 8. Base de Datos Local y Segura (SQLite)
- Almacenamiento local en `%LocalAppData%\TPVC\consultorio.db`.
- Totalmente autónomo: no requiere internet ni configuración de servidores externos.
- Copias de seguridad sencillas mediante el copiado directo del archivo `.db`.

---

## 🛠️ Tecnologías y Arquitectura

* **Plataforma**: Windows Desktop (WPF nativo)
* **Framework**: .NET 10 (C# 13 / 14)
* **Patrón de Arquitectura**: MVVM con `CommunityToolkit.Mvvm`
* **Inyección de Dependencias**: `Microsoft.Extensions.DependencyInjection`
* **Persistencia / ORM**: Entity Framework Core 10 (`Microsoft.EntityFrameworkCore.Sqlite`)
* **Pruebas Automatizadas**: MSTest v3.5 con cobertura de servicios y modelos

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

Para reportar problemas o solicitar mejoras clínicas, cree un Issue en el repositorio privado de GitHub o consulte la [Arquitectura Técnica](docs/ARQUITECTURA_TECNICA.md).

---

## 📄 Licencia

Este proyecto está distribuido bajo los términos de la Licencia MIT. Consulte el archivo [LICENSE](LICENSE) para más detalles.
