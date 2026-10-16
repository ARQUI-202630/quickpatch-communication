-- Roles y permisos de Communication Service (DD, secciones 5.22 y 10.2).
-- Lo ejecuta DevOps con aplicar-roles-db.yml DESPUÉS de las migraciones (que crean la tabla notifications y su política RLS).
-- Las contraseñas no van aquí: las pone deploy-db.yml desde el vault. Communication solo consume eventos y atiende el buzón:
-- no publica por Outbox, así que no hay rol communication_outbox.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'communication_app') THEN
        CREATE ROLE communication_app NOLOGIN NOBYPASSRLS;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO communication_app;

-- Rol del servicio, todo bajo RLS. El buzón se escribe al consumir un evento (INSERT) y la app solo marca la notificación como
-- leída: UPDATE únicamente sobre read_at, para que nadie pueda reescribir el título, el mensaje ni el destinatario.
GRANT SELECT, INSERT ON notifications TO communication_app;
GRANT UPDATE (read_at) ON notifications TO communication_app;
