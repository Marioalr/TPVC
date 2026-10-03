# TPVC - Sistema de Inventario para Consultorio Médico

Sistema moderno de control y trazabilidad de inventario exclusivo para Windows, diseñado específicamente para consultorios de **Médico General** (fármacos, material de curación, insumos descartables, diagnóstico y exploración). Desarrollado en **C# (.NET 10 / WPF)** con base de datos local **SQLite** (sin necesidad de configurar servidores externos) y motor de personalización visual en tiempo real (temas, acento y fondo).

---

## 🌟 Características Principales

### 1. Panel de Control (Dashboard) y Actualización Rápida
- **Botón "Actualizar Datos"**: Refresca métricas en vivo (stock, alertas, consumos) con un solo clic.
- **KPIs en tiempo real**: Total de insumos, alertas de stock bajo/crítico, lotes próximos a vencer y consumo total en consulta hoy.
- **Acceso Rápido a Consumo en Consulta**: Permite registrar el uso de insumos durante la atención de pacientes de forma inmediata.
- **Verificador de Actualizaciones de la App**: Botón integrado que comprueba automáticamente en GitHub si hay nuevas versiones publicadas.

### 2. Catálogo de Insumos y Medicamentos
- Registro completo adaptado a medicina general:
  - Código / SKU escaneable.
  - Nombre comercial y **Principio Activo (Genérico)** (ej. Paracetamol, Amoxicilina).
  - Presentación (tabletas, frascos, ampolletas, piezas, etc.).
  - Categorización (Medicamentos, Material de curación, Descartables, Antisépticos, Diagnóstico).
  - Stock actual, stock mínimo de alerta y stock óptimo.
  - Ubicación física dentro del consultorio (Botiquín, Estante, Vitrina, etc.) y marca de refrigeración ❄️.
- Búsqueda reactiva instantánea por nombre comercial, principio activo o código.
- Filtros rápidos de un clic: *"Solo Stock Bajo ⚠️"*, *"Próximos a Caducar ⏳"*.

### 3. Control de Lotes y Semáforo de Caducidad (Crítico en Salud)
- Seguimiento por número de lote y fecha de expiración.
- Clasificación visual automática:
  - 🟢 **Vigente** (> 60 días).
  - 🟡 **Próximo a Vencer** (≤ 60 días, con contador de días restantes).
  - 🔴 **Vencido** (alerta roja inmediata).
- Salida inteligente **FEFO** (*First Expired, First Out*): descuenta prioritariamente del lote con fecha de caducidad más cercana al consumir en consulta.

### 4. Kardex y Trazabilidad de Movimientos
- Registro detallado de cada operación:
  - Consumo en consulta médica.
  - Entradas por compra o reposición.
  - Ajustes de inventario / mermas / bajas por caducidad.
- Auditoría con fecha, hora, responsable y motivo del procedimiento.

### 5. Motor de Temas y Personalización Visual
- **Modos de Fondo**:
  - ☀️ **Modo Claro Clínico**: Interfaz limpia en tonos claros.
  - 🌙 **Modo Oscuro**: Para descanso visual en entornos de baja iluminación.
  - 🎨 **Personalizado**: Posibilidad de ingresar valores Hexadecimales para el fondo y las tarjetas.
- **Colores de Acento**:
  - Paleta médica: Cian Médico (`#0284C7`), Azul Clínico (`#2563EB`), Verde Quirúrgico (`#0D9488`), Esmeralda (`#059669`), Violeta (`#7C3AED`), Rosa Clínico (`#E11D48`), Ámbar (`#D97706`), etc.
  - Campo Hexadecimal libre para aplicar cualquier color personalizado en tiempo real.
- **Persistencia**: La configuración de tema se guarda localmente en `theme.json` y se recuerda en cada inicio.

### 6. Almacenamiento Local Seguro (SQLite)
- La base de datos `consultorio.db` se almacena localmente en `%LocalAppData%\TPVC\consultorio.db`.
- No requiere MySQL, SQL Server ni conexión a internet.
- Fácil de respaldar mediante una copia directa del archivo `.db`.

---

## 🛠️ Tecnologías y Arquitectura

- **Lenguaje**: C# (.NET 10 / WPF)
- **Patrón de Diseño**: MVVM (*Model - View - ViewModel*) con `CommunityToolkit.Mvvm`
- **Inyección de Dependencias**: `Microsoft.Extensions.DependencyInjection`
- **Persistencia / ORM**: Entity Framework Core 10 (`Microsoft.EntityFrameworkCore.Sqlite`)
- **Plataforma**: Windows (Nativo)

---

## 🚀 Cómo Ejecutar la Aplicación

### Requisitos
- Windows 10 / 11
- .NET SDK (versión 8, 9 o 10)

### Compilar y Ejecutar
```powershell
# Clonar el repositorio
git clone https://github.com/Marioalr/TPVC.git
cd TPVC

# Compilar la solución
dotnet build TPVC.slnx

# Ejecutar las pruebas unitarias
dotnet test tests\TPVC.Tests\TPVC.Tests.csproj

# Iniciar la aplicación
dotnet run --project TPVC.csproj
```
