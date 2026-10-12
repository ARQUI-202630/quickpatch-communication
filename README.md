# communication

**Tecnología:** ASP.NET Core

## Responsabilidad

Notificaciones y comunicaciones desacopladas orientadas a eventos.

## Reglas

- Mantener el ownership definido en DD/SDD.
- No escribir directamente en tablas de otros servicios.
- Publicar/consumir eventos únicamente mediante contratos versionados.
- Mantener aislamiento multi-tenant cuando corresponda.

## Canal de estado en tiempo real (ADR-023)

WebSocket `/v1/realtime`. Contrato: `contracts/api-gateway/asyncapi/realtime.v1.yaml` (AsyncAPI 3.0).

- La app envía `{"type":"auth","token":"<jwt>"}` en los primeros 10 segundos. El servicio valida el JWT con la llave pública de Identity (RS256, ADR-018) y toma el usuario, el tenant y el rol solo del token. El token no se guarda ni se registra en logs.
- Por cada evento de Kafka que consume (`service-request.created` y los cuatro `matching.*` del DD 8.5), envía un aviso sin detalle al cliente, al técnico o a los `admin_tenant` del tenant del evento. Nunca a otro tenant.
- Códigos de cierre: `4000` trama inválida, `4001` token inválido, `4002` sin autenticación, `4003` rol que no usa el canal, `4004` token vencido, `1001` reinicio, `1009` trama de más de 8 KB.
- Las conexiones viven en memoria: el servicio corre con una réplica y no tiene base de datos todavía (ADR-023, ADR-024).
- Despliegue: `deploy/k8s/` (Ingress de `/v1/realtime`).

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
