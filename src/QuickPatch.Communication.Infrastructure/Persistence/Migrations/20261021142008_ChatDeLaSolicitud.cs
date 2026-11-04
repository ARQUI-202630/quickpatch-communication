using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuickPatch.Communication.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Chat de la solicitud (DD 5.23 y 5.24, ADR-026): réplica de participantes y mensajes, con RLS forzado por tenant.
    /// </summary>
    public partial class ChatDeLaSolicitud : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_participants",
                columns: table => new
                {
                    service_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    technician_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_participants", x => x.service_request_id);
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_messages", x => x.id);
                    table.CheckConstraint("ck_chat_messages_author_role", "author_role IN ('cliente', 'tecnico')");
                    table.ForeignKey(
                        name: "fk_chat_messages_participants",
                        column: x => x.service_request_id,
                        principalTable: "chat_participants",
                        principalColumn: "service_request_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_service_request_id",
                table: "chat_messages",
                column: "service_request_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_history",
                table: "chat_messages",
                columns: new[] { "tenant_id", "service_request_id", "sent_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.Sql(TenantIsolationSql);
        }

        private const string TenantFilter = "tenant_id = NULLIF(current_setting('app.current_tenant', true), '')::uuid";

        // Igual que notifications: RLS forzado y permisos mínimos. La réplica se inserta y se actualiza al consumir
        // service-request.status-changed; los mensajes solo se insertan y se leen (no se editan ni se borran en el MVP).
        private static readonly string TenantIsolationSql = $"""
            ALTER TABLE chat_participants ENABLE ROW LEVEL SECURITY;
            ALTER TABLE chat_participants FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON chat_participants USING ({TenantFilter}) WITH CHECK ({TenantFilter});
            ALTER TABLE chat_messages ENABLE ROW LEVEL SECURITY;
            ALTER TABLE chat_messages FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON chat_messages USING ({TenantFilter}) WITH CHECK ({TenantFilter});

            DO $$
            BEGIN
                IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'communication_app') THEN
                    GRANT SELECT, INSERT, UPDATE ON chat_participants TO communication_app;
                    GRANT SELECT, INSERT ON chat_messages TO communication_app;
                END IF;
            END
            $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_messages");

            migrationBuilder.DropTable(
                name: "chat_participants");
        }
    }
}
