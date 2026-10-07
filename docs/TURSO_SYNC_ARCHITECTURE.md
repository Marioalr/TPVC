# Arquitectura de Sincronización Turso Cloud (TPVC)

## 1. Resumen Ejecutivo
TPVC implementa una arquitectura **Local-First (offline-first)** con sincronización bidireccional incremental respaldada por **Turso Cloud (libSQL)**.
Este diseño garantiza que el consultorio médico continúe funcionando al 100% incluso sin conexión a internet, sincronizando automáticamente los cambios en segundo plano cuando la conectividad se restablece.

---

## 2. Garantía de Uso 100% Gratuito (Turso Free Tier)

Turso ofrece un plan gratuito sumamente generoso diseñado para proyectos y pequeñas empresas:
- **Lecturas mensuales permitidas:** 1,000,000,000 (1 Billón de lecturas)
- **Escrituras mensuales permitidas:** 25,000,000 (25 Millones de escrituras)
- **Almacenamiento total incluido:** 9 GB
- **Bases de datos:** Hasta 500 bases de datos

### Análisis de Consumo en TPVC (3 Dispositivos)
En un consultorio con 1 PC Windows 11, 1 Tablet Android y 1 Teléfono Android:
- Consultas diarias promedio: 20-30 pacientes.
- Registros modificados/creados por día: ~200 filas (notas médicas, inventario, ventas, recibos).
- Escrituras mensuales estimadas: **~6,000 a 15,000 escrituras** (< 0.06% del límite gratuito de 25M).
- Lecturas mensuales estimadas: **~30,000 a 100,000 lecturas** (< 0.01% del límite gratuito de 1B).
- Tamaño de base de datos tras 5 años de operación: **< 150 MB** (< 1.6% del límite de 9 GB).

> **Conclusión:** El sistema opera permanentemente en el nivel gratuito sin riesgo de cobros sorpresa ni interrupción de servicio. La pantalla *Nube y Turso Cloud* muestra una estimación del consumo mensual.

---

## 3. Topología de Dispositivos y Matriz de Roles (Máximo 3 Dispositivos)

La arquitectura soporta hasta 3 dispositivos vinculados para el consultorio:

```
                  ┌───────────────────────────────┐
                  │          TURSO CLOUD          │
                  │    (libSQL / Hrana v2 HTTP)    │
                  └───────────────▲───────────────┘
                                  │
          ┌───────────────────────┼───────────────────────┐
          │                       │                       │
          ▼                       ▼                       ▼
┌──────────────────┐    ┌──────────────────┐    ┌──────────────────┐
│  PC Windows 11   │    │  Tablet Android  │    │ Celular Android  │
│ (Estación Ppal)  │    │ (Asistente/Cons) │    │  (Admin / Móvil) │
└──────────────────┘    └──────────────────┘    └──────────────────┘
```

### Matriz de Permisos por Dispositivo

| Capacidad / Función | Windows 11 Desktop | Tablet Android | Celular Android (Admin) |
|---|:---:|:---:|:---:|
| **Consultas Médicas** | ✅ Completo | ✅ Completo | ❌ Solo lectura |
| **Punto de Venta / Caja** | ✅ Completo | ✅ Completo | ❌ No permite cobro |
| **Expedientes Clínicos** | ✅ Completo | ✅ Completo | ❌ Solo lectura |
| **Control de Inventario** | ✅ Completo | 👁️ Solo consulta | 👁️ Solo consulta |
| **Reportes y Dashboard** | ✅ Completo | 👁️ Básico | ✅ Completo (Financiero) |
| **Gestión de Configuración**| ✅ Completo | ❌ Restringido | ❌ Restringido |
| **Generar Emparejamiento**| ✅ Autorizado | ❌ Restringido | ❌ Restringido |

---

## 4. Protocolo de Sincronización (versión 2, desde TPVC v1.9.0)

> Esta sección es el contrato que debe implementar **cualquier** cliente (Windows hoy, Android después).
> Implementación de referencia: `Services/TursoSyncService.cs` + `Services/TursoClient.cs`.
> Pruebas de punta a punta con un servidor Hrana simulado: `tests/TPVC.Tests/TursoSyncEndToEndTests.cs`.

### 4.1 Transporte
- `POST {url}/v2/pipeline` con `Authorization: Bearer <token>` y cuerpo `{"requests":[{"type":"execute","stmt":{...}}, ..., {"type":"close"}]}`.
- Valores (`args`) según la especificación Hrana:
  - `{"type":"integer","value":"42"}` → **el entero va como texto**. Enviarlo como número JSON hace que Turso responda **HTTP 400** a todo el pipeline (era el error de v1.8.x).
  - `{"type":"float","value":1.5}`, `{"type":"text","value":"..."}`, `{"type":"null"}`.
- Cada sentencia de un pipeline se confirma por separado (no hay transacción entre lotes); todas las escrituras son *upserts* idempotentes, así que reintentar un lote es seguro.

### 4.2 Esquema remoto
Se genera automáticamente desde el modelo de EF Core: **cada entidad `ISyncEntity` tiene una tabla con el mismo nombre y TODAS sus columnas** (no un subconjunto), más:

| Columna | Significado |
|---|---|
| `SyncId TEXT PRIMARY KEY` | GUID en minúsculas con guiones (`Guid.ToString("D")`). Los `Id` enteros locales **no** se suben. |
| `UpdatedAtUtc TEXT` | Hora UTC del dispositivo que hizo el último cambio, formato `yyyy-MM-dd HH:mm:ss.fffffff`. |
| `SyncedAtUtc TEXT` | La asigna el **servidor** (`strftime('%Y-%m-%d %H:%M:%f','now')`) en cada escritura. Base de las descargas. |
| `OriginDeviceId TEXT` | Dispositivo que escribió la versión vigente. |
| `<Referencia>SyncId TEXT` | Cada llave foránea viaja como SyncId: `ProductId → ProductSyncId`, `PatientRecordId → PatientRecordSyncId`, `CategoryId → CategorySyncId`, `SaleItem.ProductId → ProductSyncId`, etc. |

- Tipos: enteros/bool/enums → `INTEGER`; double → `REAL`; texto, `decimal` (exacto), fechas y GUID → `TEXT`.
- Fechas de negocio (consulta, caducidad, cita) se guardan **sin conversión de zona horaria** (hora local del consultorio).
- Tablas especiales: `SyncTombstones (EntityType, SyncId, DeletedAtUtc, SyncedAtUtc, OriginDeviceId)`, `ClinicProfiles` (fila Id = 1), `RegisteredDevices`, `_tpvc_meta` (huella del esquema).
- Las tablas creadas por v1.8.x (con `Id` entero) se renombran a `legacy_v1_*`; no se borra nada.

### 4.3 Ciclo de sincronización
1. **Esquema:** se compara la huella en `_tpvc_meta`; solo si cambió se crean tablas / columnas nuevas (`ALTER TABLE ADD COLUMN`).
2. **Subida:** filas locales con `UpdatedAtUtc` entre la última subida y ahora, en orden de dependencias (categorías → productos → lotes → ...), en lotes de 80 sentencias:
   `INSERT ... ON CONFLICT(SyncId) DO UPDATE SET ... WHERE excluded.UpdatedAtUtc > tabla.UpdatedAtUtc` (gana la versión más nueva).
   Borrados: upsert en `SyncTombstones` + `DELETE ... WHERE SyncId = ? AND UpdatedAtUtc <= fechaDelBorrado`.
3. **Descarga:** se toma la marca del servidor (`now - 5 s`) y se leen filas con `SyncedAtUtc` mayor a la última marca guardada, en páginas de 500, **excluyendo las de este mismo dispositivo**. Las filas se aplican sin re-estampar `UpdatedAtUtc` (bandera `AppDbContext.ApplyingRemoteChanges`), traduciendo SyncId → Id local.
4. **Perfil del consultorio:** gana el `UpdatedAtUtc` más reciente.
5. Se guardan `LastPushedLocalUtc` y `LastPulledServerWatermark`.

### 4.4 Garantías
- **Sin ciclos:** lo recibido conserva su marca y no vuelve a subirse ni a descargarse.
- **Relojes desfasados:** las descargas usan el reloj del servidor, no el del dispositivo.
- **Folios duplicados** (dos equipos crean `EXP-2026-005` sin conexión): el registro recibido se guarda como `EXP-2026-005-R1` en lugar de bloquear la sincronización.
- **Sincronización completa** (botón "Sincronización Completa"): ignora las marcas y sube/descarga todo; útil al enlazar un equipo nuevo o tras restaurar una base.

---

## 5. Vinculación Segura de Dispositivos (Pairing Protocol)

Para enlazar la Tablet o el Celular sin exponer tokens manualmente:
1. Desde la PC principal (Windows 11), dirigirse a **Configuración > Nube y Turso Cloud**.
2. Hacer clic en **"Generar Código de Enlace Android"**.
3. Seleccionar el rol de destino (`AndroidTablet` o `AndroidMobileAdmin`).
4. La aplicación genera un payload JSON encriptado o código QR compatible con el esquema URI:
   ```
   tpvc-sync://pairing?data=<PAYLOAD_BASE64>
   ```
5. El dispositivo Android escanea o importa la configuración, validando la clave y estableciendo conexión directa con Turso.

---

## 6. Guía Rápida de Configuración en Turso

### Opción A: Mediante Turso CLI
```bash
# 1. Instalar Turso CLI
curl -sSfL https://get.tur.so/install.sh | bash

# 2. Iniciar sesión gratuita
turso auth signup # o turso auth login

# 3. Crear base de datos para la clínica
turso db create tpvc-clinica --location qro # o la región más cercana

# 4. Obtener URL de conexión
turso db show tpvc-clinica --url
# Ejemplo: libsql://tpvc-clinica-usuario.turso.io

# 5. Generar token de autenticación
turso db tokens create tpvc-clinica
```

### Opción B: Mediante Consola Web de Turso
1. Ingresar a [https://turso.tech](https://turso.tech) y registrarse gratis con GitHub o Google.
2. Hacer clic en **"Create database"**.
3. Nombrar la base de datos (ej. `tpvc-clinica`) y elegir la región más cercana (ej. `qro` Querétaro, México o `dfw` Dallas).
4. Copiar la **Database URL** y generar un **Auth Token** desde la pestaña *Settings > Tokens*.
5. Pegar ambos valores en TPVC Windows en **Configuración > Nube y Turso Cloud**.
6. Hacer clic en **"Probar Conexión"** y posteriormente en **"Sincronización Completa"**.
