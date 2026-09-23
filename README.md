# communication

**Tecnología:** ASP.NET Core

## Responsabilidad

Notificaciones y comunicaciones desacopladas orientadas a eventos.

## Reglas

- Mantener el ownership definido en DD/SDD.
- No escribir directamente en tablas de otros servicios.
- Publicar/consumir eventos únicamente mediante contratos versionados.
- Mantener aislamiento multi-tenant cuando corresponda.
