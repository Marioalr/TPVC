# TPCV - Sistema de Inventario para Consultorio

Sistema de gestión e inventario para consultorio médico/clínico, desarrollado en C# / WPF para Windows con base de datos local SQLite y soporte de personalización visual (temas, color de acento y fondo).

## Características principales
- **Gestión de Inventario**: Productos, insumos médicos, medicamentos, material descartable, lotes y fechas de caducidad.
- **Entradas y Salidas**: Registro de movimientos de inventario (compras, uso clínico, ajustes, mermas).
- **Alertas**: Notificaciones visuales de stock bajo y productos próximos a caducar.
- **Temas y Personalización**: Configuración dinámica de color de acento y modo/color de fondo (claro, oscuro, personalizado).
- **Almacenamiento Local**: SQLite rápido, seguro y sin dependencias externas complejas.

## Arquitectura y Tecnologías
- **Lenguaje**: C# (.NET)
- **Framework UI**: WPF (Windows Presentation Foundation) con MVVM
- **Base de Datos**: SQLite (Entity Framework Core o Dapper / Microsoft.Data.Sqlite)
- **Plataforma**: Windows
