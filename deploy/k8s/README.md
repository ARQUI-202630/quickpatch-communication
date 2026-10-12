# Despliegue de Communication en k3s

`communication.yaml` crea el ConfigMap, el Deployment, el Service y el Ingress de `/v1/realtime` en el namespace
`quickpatch`. Es igual en QA y en producción; solo cambian el secreto y la dirección de Kafka.

Communication no tiene base de datos todavía: las conexiones del canal en tiempo real viven en memoria (ADR-023) y no
hay migraciones. Cuando exista el buzón de notificaciones (ADR-024) se agregan la base y el init container.

## Secreto `communication-secretos`

Lo crea DevOps desde Ansible Vault. Nunca se guarda en el repositorio.

|Clave|Contenido|
|---|---|
|`jwt-public-key`|Llave pública RSA de Identity en PEM, para validar el token de la primera trama del WebSocket (ADR-018)|

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

## Gateway de VM1 (coordinar con DevOps)

El Nginx de VM1 ya reenvía `Upgrade` y `Connection`. Para conexiones de larga duración hay que subir
`proxy_read_timeout` y `proxy_send_timeout` de `/api/` por encima del intervalo de ping del servidor
(`Realtime__KeepAliveSeconds`, 30 s): por ejemplo, 75 s. Sin ese ajuste, Nginx corta un WebSocket inactivo a los 60 s
y la app se reconecta (el respaldo de 30 s mantiene RNF-06).
