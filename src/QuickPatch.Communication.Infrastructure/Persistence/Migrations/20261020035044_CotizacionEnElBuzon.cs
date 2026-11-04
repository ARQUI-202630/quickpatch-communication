using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Communication.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CotizacionEnElBuzon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_type",
                table: "notifications");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_type",
                table: "notifications",
                sql: "type IN ('solicitud-creada', 'tecnico-asignado', 'solicitud-asignada', 'sin-tecnico-disponible', 'busqueda-reanudada', 'oferta-recibida', 'registro-recibido', 'verificacion-actualizada', 'solicitud-cancelada', 'cotizacion-recibida', 'cotizacion-aceptada', 'cotizacion-rechazada')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_type",
                table: "notifications");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_type",
                table: "notifications",
                sql: "type IN ('solicitud-creada', 'tecnico-asignado', 'solicitud-asignada', 'sin-tecnico-disponible', 'busqueda-reanudada', 'oferta-recibida', 'registro-recibido', 'verificacion-actualizada', 'solicitud-cancelada')");
        }
    }
}
