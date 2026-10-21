using Microsoft.EntityFrameworkCore;

using QuickPatch.Communication.Domain.Chat;
using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.Infrastructure.Persistence;

/// <summary>Base <c>db_communication</c>: el buzón de notificaciones (DD 5.22, ADR-024) y el chat (DD 5.23 y 5.24, ADR-026).</summary>
public sealed class CommunicationDbContext(DbContextOptions<CommunicationDbContext> options) : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<ChatParticipants> ChatParticipants => Set<ChatParticipants>();

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(e =>
        {
            e.ToTable("notifications", t => t.HasCheckConstraint(
                "ck_notifications_type", $"type IN ({string.Join(", ", NotificationTypes.All.Select(x => $"'{x}'"))})"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(40);
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(Notification.TitleMaxLength);
            e.Property(x => x.Message).HasColumnName("message").HasMaxLength(Notification.MessageMaxLength);
            e.Property(x => x.ServiceRequestId).HasColumnName("service_request_id");
            e.Property(x => x.SourceEventId).HasColumnName("source_event_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ReadAt).HasColumnName("read_at");

            // Un evento crea a lo sumo una notificación por destinatario (RN-EV1, sin processed_events).
            e.HasIndex(x => new { x.SourceEventId, x.UserId }).IsUnique().HasDatabaseName("ux_notifications_source_event_user");
            e.HasIndex(x => new { x.TenantId, x.UserId, x.CreatedAt }).IsDescending(false, false, true)
                .HasDatabaseName("ix_notifications_tenant_user_created");
            e.HasIndex(x => new { x.TenantId, x.UserId }).HasFilter("read_at IS NULL").HasDatabaseName("ix_notifications_unread");
        });

        modelBuilder.Entity<ChatParticipants>(e =>
        {
            e.ToTable("chat_participants");
            e.HasKey(x => x.ServiceRequestId);
            e.Property(x => x.ServiceRequestId).HasColumnName("service_request_id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ClientId).HasColumnName("client_id");
            e.Property(x => x.TechnicianId).HasColumnName("technician_id");
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(30);
            e.Property(x => x.StatusChangedAt).HasColumnName("status_changed_at");
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.ToTable("chat_messages", t => t.HasCheckConstraint(
                "ck_chat_messages_author_role", $"author_role IN ('{ChatRoles.Cliente}', '{ChatRoles.Tecnico}')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ServiceRequestId).HasColumnName("service_request_id");
            e.Property(x => x.AuthorId).HasColumnName("author_id");
            e.Property(x => x.AuthorRole).HasColumnName("author_role").HasMaxLength(30);
            e.Property(x => x.Body).HasColumnName("body").HasMaxLength(ChatMessage.MaxLength);
            e.Property(x => x.SentAt).HasColumnName("sent_at");
            e.HasOne<ChatParticipants>().WithMany().HasForeignKey(x => x.ServiceRequestId).OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_chat_messages_participants");
            e.HasIndex(x => new { x.TenantId, x.ServiceRequestId, x.SentAt, x.Id }).IsDescending(false, false, true, true)
                .HasDatabaseName("ix_chat_messages_history");
        });
    }
}