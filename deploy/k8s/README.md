# Despliegue de Communication en k3s

`communication.yaml` crea el ConfigMap, el Deployment, el Service y el Ingress de `/v1/realtime` y `/v1/notifications` en el namespace
`quickpatch`. Es igual en QA y en producción; solo cambian el secreto y la dirección de Kafka.

Las conexiones del canal en tiempo real viven en memoria (ADR-023). El buzón de notificaciones (ADR-024, SCRUM-456) tiene
base propia, `db_communication`, con los roles `communication_migrator` (dueño, solo para las migraciones) y
`communication_app` (el servicio, bajo RLS). DevOps ya los creó con `deploy-db.yml`; sus permisos están en `db/roles.sql`.

**Falta el init container de migraciones.** Se agrega a este manifiesto cuando el Dockerfile publique el `efbundle` de las
migraciones de `notifications`, igual que en Catalog (`initContainers` con `command: ["/app/efbundle", "--connection",
"$(MIGRATOR_CONNECTION)"]` y `MIGRATOR_CONNECTION` desde la clave `migrator-connection`). Hasta entonces no se puede poner:
la imagen actual no trae `/app/efbundle` y el pod no arrancaría.

## Secreto `communication-secretos`

Lo crea DevOps desde Ansible Vault. Nunca se guarda en el repositorio.

|Clave|Contenido|
|---|---|
|`jwt-public-key`|Llave pública RSA de Identity en PEM, para validar el token de la primera trama del WebSocket (ADR-018)|
|`app-connection`|Cadena de conexión de `communication_app` a `db_communication` (`Maximum Pool Size=8`); el pod la lee como `ConnectionStrings__Communication`|
|`migrator-connection`|Cadena de `communication_migrator`, solo para el init container de migraciones|

Lo crea el playbook `primer-despliegue-servicios.yml` de `quickpatch` con las tres claves. Ejemplo a mano solo para la llave
(el playbook incluye además las conexiones desde el vault):

```bash
kubectl -n quickpatch create secret generic communication-secretos --from-file=jwt-public-key=identity-public.pem
```

## Primer despliegue

1. Topics en el Kafka del ambiente, según `quickpatch-kafka/topics/topics.yaml`: `service-request.created`,
   `matching.offer-created`, `matching.technician-assigned`, `matching.no-technician-available` y
   `matching.search-resumed`. Si un topic todavía no existe, el consumidor espera y reintenta sin detener el servicio.
2. Reemplazar `<vm-mensajeria>` en el ConfigMap por la IP de Kafka del ambiente y
   `kubectl apply -f deploy/k8s/communication.yaml`. El pod queda esperando la imagen (`:pendiente`).
3. Push a `release/*`: el pipeline publica la imagen y la fija en el Deployment.
4. Con las migraciones desplegadas, `aplicar-roles-db.yml -e servicio=communication` aplica `db/roles.sql` (después de las migraciones, porque los permisos necesitan la tabla).

## Gateway de VM1 (coordinar con DevOps)

El Nginx de VM1 ya reenvía `Upgrade` y `Connection`. Para conexiones de larga duración hay que subir
`proxy_read_timeout` y `proxy_send_timeout` de `/api/` por encima del intervalo de ping del servidor
(`Realtime__KeepAliveSeconds`, 30 s): por ejemplo, 75 s. Sin ese ajuste, Nginx corta un WebSocket inactivo a los 60 s
y la app se reconecta (el respaldo de 30 s mantiene RNF-06).
