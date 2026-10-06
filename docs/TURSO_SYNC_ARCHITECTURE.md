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

> **Conclusión:** El sistema opera permanentemente en el nivel gratuito sin riesgo de cobros sorpresa ni interrupción de servicio. El componente `TursoQuotaGuardian` en la aplicación monitorea en tiempo real el consumo mensual.

---

## 3. Topología de Dispositivos y Matriz de Roles (Máximo 3 Dispositivos)

La arquitectura soporta hasta 3 dispositivos vinculados para el consultorio:

```
                  ┌───────────────────────────────┐
                  │          TURSO CLOUD          │
                  │    (libSQL / Hrana v3 API)    │
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

## 4. Protocolo de Sincronización

1. **Protocolo de Comunicación:** HTTP REST Pipeline (`/v2/pipeline`) bajo la especificación Hrana 3 JSON de libSQL / Turso.
2. **Identificador Global Único:** Cada entidad del dominio implementa `ISyncEntity` con:
   - `SyncId` (GUID único generado localmente).
   - `UpdatedAtUtc` (Marca temporal en UTC de última modificación).
3. **Manejo de Eliminaciones:** Registradas en la tabla `SyncTombstones` para propagar bajas lógicas sin perder consistencia relacional.
4. **Resolución de Conflictos:** *Last-Write-Wins (LWW)* basado en `UpdatedAtUtc`. En caso de colisión de microsegundos, prevalece la versión remota consolidada.
5. **Transaccionalidad por Lotes:** Las escrituras a Turso se empaquetan en transacciones ACID atómicas (`BEGIN` / `COMMIT`) mediante la pipeline HTTP de Hrana.

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
6. Hacer clic en **"Probar Conexión"** y posteriormente en **"Carga Inicial a la Nube"**.
