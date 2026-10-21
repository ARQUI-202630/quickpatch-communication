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
- Por cada evento de Kafka que consume (`service-request.created`, `service-request.status-changed` y `matching.offer-created`, DD 8.5), envía un aviso sin detalle al cliente, al técnico o a los `admin_tenant` del tenant del evento. Nunca a otro tenant. El aviso de estado sale de ServiceRequest, que publica `status-changed` solo cuando aplicó el cambio; los eventos de Matching que cambian el estado ya no generan aviso, porque ServiceRequest puede descartarlos por viejos (SCRUM-466).
- Códigos de cierre: `4000` trama inválida, `4001` token inválido, `4002` sin autenticación, `4003` rol que no usa el canal, `4004` token vencido, `1001` reinicio, `1009` trama de más de 8 KB.
- Las conexiones viven en memoria: el servicio corre con una réplica (ADR-023).
- Despliegue: `deploy/k8s/` (Ingress de `/v1/realtime` y `/v1/notifications`).

## Buzón de notificaciones (ADR-024, SCRUM-456)

`GET /v1/notifications?unread=&page=&size=` y `POST /v1/notifications/{id}/read`. Contrato:
`contracts/api-gateway/openapi/communication.v1.yaml` 1.1.0 (gateway `v0.9.0`). Tabla `notifications` en `db_communication` (DD 5.22).

- **Quién crea las notificaciones:** solo Communication, al consumir los eventos de la tabla de tipos del contrato (DD 8.2.6). Las reglas están en `InboxRules`. Los avisos de estado salen de `service-request.status-changed` (SCRUM-466), igual que en el canal: `asignado` crea `tecnico-asignado` (cliente) y `solicitud-asignada` (técnico); `en_espera`, `sin-tecnico-disponible`; y `en_espera` → `buscando_tecnico`, `busqueda-reanudada`. `cancelado` crea `solicitud-cancelada` para el cliente y, si lo había, el técnico asignado, sin el motivo (1.1.0). Los demás estados todavía no tienen tipo en el contrato y solo llegan por el canal.
- **Consumidor propio** (`Kafka:InboxConsumerGroupId`, por defecto `communication-inbox`): a diferencia del canal, lee desde el principio y confirma el offset solo después de guardar. Si la base falla, vuelve al mismo mensaje y reintenta. Un evento repetido no duplica nada: `UNIQUE (source_event_id, user_id)` con `ON CONFLICT DO NOTHING`.
- **Aislamiento:** el usuario y el tenant salen solo de `sub` y `tenant_id` del JWT. Cada operación fija `app.current_tenant` en su transacción y la tabla tiene RLS forzado; el consumidor usa el `tenantId` del evento. Una notificación de otro usuario o tenant responde `404`.
- **Permisos:** `communication_app` tiene `SELECT` e `INSERT`, y `UPDATE` solo sobre `read_at` (`db/roles.sql` y la migración).
- **Roles:** `cliente`, `empresa_contacto`, `tecnico` y `admin_tenant`; los demás, `403`.
- Sin `ConnectionStrings:Communication` (desarrollo local), el canal funciona igual y el buzón responde `503`.

## Chat de la solicitud (ADR-026, SCRUM-480 y SCRUM-481)

`GET` y `POST /v1/service-requests/{id}/messages` (`communication.v1.yaml` 1.3.0, gateway `v0.15.0`) y la trama `chat.message` del canal (`realtime.v1.yaml` 1.1.0). Tablas `chat_participants` y `chat_messages` en `db_communication` (DD 5.23 y 5.24).

- **Participantes sin llamar a ServiceRequest:** un consumidor propio (`Kafka:ChatConsumerGroupId`, por defecto `communication-chat`) lee `service-request.status-changed` desde el principio y guarda, por solicitud, el cliente, el técnico asignado y el estado. Solo aplica un evento con `changedAt` más nuevo (RN-CH3).
- **Quién:** solo el cliente dueño y el técnico asignado actual (RN-CH1). Otro usuario, otro tenant, el técnico anterior tras una reasignación o una solicitud que nunca tuvo técnico responden `404`. `admin_tenant`, `admin_plataforma` y `proveedor` reciben `403`.
- **Cuándo:** se escribe en `asignado`, `cotizado`, `cotizacion_aceptada` y `en_progreso`. En `completado`, `pagado` y `cancelado` responde `409 chat-cerrado`, y sin técnico responde `409 chat-no-disponible` (RN-CH2). El historial se lee siempre, con `writable`.
- **Entrega:** al guardar, el mensaje se envía por el canal al cliente y al técnico. Si la entrega falla, el mensaje igual queda guardado.
- **Privacidad:** el texto (1 a 1.000 caracteres, sin adjuntos) no se escribe en logs ni en eventos (RN-CH4). `communication_app` puede insertar y leer mensajes, pero no editarlos ni borrarlos.
- **Ruta:** la toma un `IngressRoute` de Traefik por el sufijo `/messages` (`deploy/k8s/communication.yaml`), igual que el pago en Payments.

## Configuración

|Clave|Para qué|
|---|---|
|`ConnectionStrings__Communication`|PostgreSQL `db_communication` con el usuario `communication_app`|
|`Jwt__PublicKeyPem`, `Jwt__Issuer`, `Jwt__Audience`|Validación del token de Identity (canal y buzón)|
|`Kafka__BootstrapServers`|Kafka; vacío no arranca los consumidores|
|`Kafka__ConsumerGroupId`, `Kafka__InboxConsumerGroupId`|Grupos del canal (`communication-service`) y del buzón (`communication-inbox`)|
|`Realtime__*`|Plazo de la trama `auth`, tamaño máximo de trama e intervalo de ping|

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

- `tests/unit`: dominio, reglas del buzón y API completa con el almacenamiento en memoria. La cobertura mide Domain, Application y Api (`coverlet.runsettings`).
- `tests/integration`: PostgreSQL 16 y Kafka reales con Testcontainers (requiere Docker): migraciones, `db/roles.sql`, RLS entre tenants, permisos por columna, eventos que llegan al buzón sin duplicarse y el canal en tiempo real.

Migraciones (EF Core, con el rol dueño de las tablas): `dotnet tool restore` y `dotnet ef database update --project src/QuickPatch.Communication.Infrastructure --connection "<cadena>"`.

## Contenedor

- Imagen: `Dockerfile` en la raíz (multi-stage, usuario sin privilegios).
- Puerto: `8080`.
- Probes para k3s: `GET /health/live` (el proceso responde) y `GET /health/ready` (el servicio y sus dependencias están listos).
