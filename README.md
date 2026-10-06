# communication

**Tecnología:** ASP.NET Core

## Responsabilidad

Notificaciones y comunicaciones desacopladas orientadas a eventos.

## Reglas

- Mantener el ownership definido en DD/SDD.
- No escribir directamente en tablas de otros servicios.
- Publicar/consumir eventos únicamente mediante contratos versionados.
- Mantener aislamiento multi-tenant cuando corresponda.

## Estructura

Cuatro capas, según el SDD (secciones 6.3 y 6.5):

```text
QuickPatch.Communication.slnx
src/
  QuickPatch.Communication.Api/              Endpoints, validación de entrada y raíz de composición
  QuickPatch.Communication.Application/      Casos de uso, comandos y consultas
  QuickPatch.Communication.Domain/           Entidades, value objects y reglas de negocio (sin dependencias externas)
  QuickPatch.Communication.Infrastructure/   PostgreSQL, Kafka, Redis y adaptadores externos
tests/
  unit/QuickPatch.Communication.UnitTests/
  integration/QuickPatch.Communication.IntegrationTests/
```

Dependencias permitidas: `Api → Application → Domain`; `Infrastructure → Application, Domain`. `Api` referencia `Infrastructure` solo para registrar sus servicios.

## Desarrollo local

Requiere el SDK de .NET indicado en `global.json`.

```bash
dotnet restore
dotnet format --verify-no-changes   # lint, igual que el CI
dotnet build -c Release
dotnet test tests/unit/QuickPatch.Communication.UnitTests
dotnet test tests/integration/QuickPatch.Communication.IntegrationTests
dotnet run --project src/QuickPatch.Communication.Api
```

## Contenedor

- Imagen: `Dockerfile` en la raíz (multi-stage, usuario sin privilegios).
- Puerto: `8080`.
- Probes para k3s: `GET /health/live` (el proceso responde) y `GET /health/ready` (el servicio y sus dependencias están listos).
