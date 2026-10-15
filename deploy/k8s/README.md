# Despliegue de Communication en k3s

`communication.yaml` crea el ConfigMap, el Deployment (con las migraciones del buzón como init container), el Service y
el Ingress de `/v1/realtime` y `/v1/notifications` en el namespace `quickpatch`. Es igual en QA y en producción; solo
cambian el secreto y la dirección de Kafka.

Las conexiones del canal en tiempo real viven en memoria (ADR-023). El buzón de notificaciones (ADR-024, DD 5.22) se
guarda en `db_communication`.

## Secreto `communication-secretos`

Lo crea DevOps desde Ansible Vault. Nunca se guarda en el repositorio.

|Clave|Contenido|
|---|---|
|`migrator-connection`|Cadena de conexión con `communication_migrator` (dueño de las tablas; solo para las migraciones)|
|`app-connection`|Cadena de conexión con `communication_app` (sin `BYPASSRLS`)|
|`jwt-public-key`|Llave pública RSA de Identity en PEM, para validar el token del WebSocket y del buzón (ADR-018)|

```bash
kubectl -n quickpatch create secret generic communication-secretos \
  --from-literal=migrator-connection='Host=<vm-datos>;Port=5432;Database=db_communication;Username=communication_migrator;Password=<...>' \
  --from-literal=app-connection='Host=<vm-datos>;Port=5432;Database=db_communication;Username=communication_app;Password=<...>' \
  --from-file=jwt-public-key=identity-public.pem
```

Sin `app-connection` el canal funciona igual, pero el buzón responde `503`.

## Primer despliegue

1. Base: `db_communication` con dueño `communication_migrator` y el rol `communication_app` (Ansible). No necesita PostGIS.
2. Topics en el Kafka del ambiente, según `quickpatch-kafka/topics/topics.yaml`: `service-request.created`,
   `matching.offer-created`, `matching.technician-assigned`, `matching.no-technician-available`,
   `matching.search-resumed` y, para el buzón, `technician.registered` y `technician.verification-changed`. Si un topic
   todavía no existe, los consumidores esperan y reintentan sin detener el servicio.
3. Reemplazar `<vm-mensajeria>` en el ConfigMap por la IP de Kafka del ambiente y
   `kubectl apply -f deploy/k8s/communication.yaml`. El pod queda esperando la imagen (`:pendiente`).
4. Push a `release/*`: el pipeline publica la imagen y la fija en el Deployment; el init container aplica las migraciones.
5. Solo la primera vez, un administrador de la base ejecuta `db/roles.sql` y reinicia el servicio con
   `kubectl -n quickpatch rollout restart deployment/communication`. Si `communication_app` ya existía, la migración
   concede los permisos.

## Gateway de VM1 (coordinar con DevOps)

El Nginx de VM1 ya reenvía `Upgrade` y `Connection`. Para conexiones de larga duración hay que subir
`proxy_read_timeout` y `proxy_send_timeout` de `/api/` por encima del intervalo de ping del servidor
(`Realtime__KeepAliveSeconds`, 30 s): por ejemplo, 75 s. Sin ese ajuste, Nginx corta un WebSocket inactivo a los 60 s
y la app se reconecta (el respaldo de 30 s mantiene RNF-06).
