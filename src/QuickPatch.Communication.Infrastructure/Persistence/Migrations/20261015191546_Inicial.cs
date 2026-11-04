using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Communication.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Buzón de notificaciones (DD 5.22, ADR-024): tabla <c>notifications</c> con RLS forzado por tenant (DD 10.1).
    /// </summary>
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    service_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.id);
                    table.CheckConstraint("ck_notifications_type", "type IN ('solicitud-creada', 'tecnico-asignado', 'solicitud-asignada', 'sin-tecnico-disponible', 'busqueda-reanudada', 'oferta-recibida', 'registro-recibido', 'verificacion-actualizada')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_user_created",
                table: "notifications",
                columns: new[] { "tenant_id", "user_id", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_unread",
                table: "notifications",
                columns: new[] { "tenant_id", "user_id" },
                filter: "read_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_notifications_source_event_user",
                table: "notifications",
                columns: new[] { "source_event_id", "user_id" },
                unique: true);

            migrationBuilder.Sql(TenantIsolationSql);
        }

        private const string TenantFilter = "tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        // RLS forzado: sin app.current_tenant fijado no se ve ni se escribe ninguna fila. El dueño de la tabla (el migrador)
        // concede los permisos si el rol ya existe; db/roles.sql hace lo mismo en el primer despliegue.
        private static readonly string TenantIsolationSql = $"""
            ALTER TABLE notifications ENABLE ROW LEVEL SECURITY;
            ALTER TABLE notifications FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON notifications USING ({TenantFilter}) WITH CHECK ({TenantFilter});

            DO $$
            BEGIN
                IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'communication_app') THEN
                    GRANT SELECT, INSERT ON notifications TO communication_app;
                    GRANT UPDATE (read_at) ON notifications TO communication_app;
                END IF;
            END
            $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}
