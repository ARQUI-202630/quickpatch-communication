-- Roles y permisos de Communication Service (DD, secciones 5.22 y 10.1).
-- Lo ejecuta un administrador de la base DESPUÉS de las migraciones (que crean la tabla y la política RLS).
-- Las contraseñas no van aquí: se asignan aparte con ALTER ROLE ... LOGIN PASSWORD, desde el vault.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'communication_app') THEN
        CREATE ROLE communication_app NOLOGIN NOBYPASSRLS;
    END IF;
END
$$;

GRANT USAGE ON SCHEMA public TO communication_app;

-- Rol del servicio: todo bajo RLS. Las notificaciones se crean y se leen; de ellas solo cambia read_at.
GRANT SELECT, INSERT ON notifications TO communication_app;
GRANT UPDATE (read_at) ON notifications TO communication_app;
