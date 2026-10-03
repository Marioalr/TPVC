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

### 1. Nueva Vista Principal: Acceso Directo por Tarjetas (Cards)
- **Flujo de Trabajo Médico Limpio y Enfocado**: La vista principal presenta una interfaz despejada con dos accesos directos de gran tamaño tipo tarjeta (*cards*):
  - **Card 🛒 "Nueva Venta"**: Acceso inmediato al **Punto de Venta con Carrito (POS)** para búsqueda, dispensación y cobro de medicamentos.
  - **Card 📋 "Nueva Consulta"**: Acceso directo al módulo de **Expedientes Clínicos (TODO)** para historial médico, signos vitales y prescripción.
- **Navegación Intuitiva**: Ambas vistas incluyen botón de retorno rápido (*"← Volver a Inicio"*) además de la barra lateral permanente.
- **Interconexión Clínica**: Despache las recetas de los pacientes directamente al carrito de compras con el botón *"Cargar a Venta (POS)"*.

### 2. Punto de Venta (POS) con Carrito de Compra Interactivo
- **Catálogo de Productos**: Búsqueda reactiva instantánea por nombre comercial, principio activo o código SKU, filtrado por categorías y badges visuales de existencias.
- **Carrito de Venta**:
  - Listado dinámico de artículos con control de cantidades (`+`/`-`), eliminación de partidas y subtotales por renglón.
  - Campo para asociar el nombre del paciente o cliente.
  - Selector de método de pago: *Efectivo*, *Tarjeta Débito/Crédito*, *Transferencia*.
  - Total a pagar en tiempo real con tipografía destacada.
- **Cobro y Despacho Automatizado**:
  - Descuenta existencias en SQLite aplicando la regla sanitaria **FEFO** (*First Expired, First Out*).
  - Genera automáticamente el registro de auditoría en el Kardex (`SalidaVenta`).
  - Actualiza todos los catálogos y métricas sin requerir interacción manual.

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
