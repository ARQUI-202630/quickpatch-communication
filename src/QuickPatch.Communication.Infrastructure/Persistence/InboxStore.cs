using Microsoft.EntityFrameworkCore;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.Infrastructure.Persistence;

/// <summary>
/// Buzón en PostgreSQL (DD 5.22). Cada operación abre una transacción y fija <c>app.current_tenant</c> con
/// <c>set_config(..., true)</c> (equivalente a <c>SET LOCAL</c>): RLS deja ver y escribir solo las filas de ese tenant.
/// </summary>
public sealed class InboxStore(CommunicationDbContext db) : IInboxStore
{
    public Task<int> AddAsync(Guid tenantId, IReadOnlyList<Notification> notifications, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        return InTenantAsync(tenantId, async ct =>
        {
            var created = 0;
            foreach (var n in notifications)
            {
                if (n.TenantId != tenantId)
                {
                    throw new ArgumentException("Todas las notificaciones deben ser del tenant de la transacción.", nameof(notifications));
                }

                created += await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO notifications (id, tenant_id, user_id, type, title, message, service_request_id, source_event_id, created_at)
                    VALUES ({n.Id}, {n.TenantId}, {n.UserId}, {n.Type}, {n.Title}, {n.Message}, {n.ServiceRequestId}, {n.SourceEventId}, {n.CreatedAt})
                    ON CONFLICT (source_event_id, user_id) DO NOTHING
                    """,
                    ct);
            }

            return created;
        }, cancellationToken);
    }

    public Task<NotificationPage> ListAsync(
        Guid tenantId, Guid userId, bool unreadOnly, int skip, int take, CancellationToken cancellationToken) =>
        InTenantAsync(tenantId, async ct =>
        {
            var mine = db.Notifications.AsNoTracking().Where(x => x.TenantId == tenantId && x.UserId == userId);
            var unread = await mine.CountAsync(x => x.ReadAt == null, ct);
            var filtered = unreadOnly ? mine.Where(x => x.ReadAt == null) : mine;
            var total = unreadOnly ? unread : await filtered.CountAsync(ct);
            var items = await filtered
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.Id)
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);
            return new NotificationPage(items, total, unread);
        }, cancellationToken);

    public Task<Notification?> MarkReadAsync(Guid tenantId, Guid userId, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        InTenantAsync(tenantId, async ct =>
        {
            var notification = await db.Notifications.SingleOrDefaultAsync(
                x => x.Id == id && x.TenantId == tenantId && x.UserId == userId, ct);
            notification?.MarkRead(now);
            return notification;
        }, cancellationToken);

    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("El tenant es obligatorio.", nameof(tenantId));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tenant = tenantId.ToString();
        await db.Database.ExecuteSqlAsync($"SELECT set_config('app.current_tenant', {tenant}, true)", cancellationToken);

        var result = await work(cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

/// <summary>Sin <c>ConnectionStrings:Communication</c> (desarrollo local): el buzón responde 503 y el canal sigue funcionando.</summary>
public sealed class UnavailableInboxStore : IInboxStore
{
    public Task<int> AddAsync(Guid tenantId, IReadOnlyList<Notification> notifications, CancellationToken cancellationToken) =>
        throw new InboxUnavailableException();

    public Task<NotificationPage> ListAsync(Guid tenantId, Guid userId, bool unreadOnly, int skip, int take, CancellationToken cancellationToken) =>
        throw new InboxUnavailableException();

    public Task<Notification?> MarkReadAsync(Guid tenantId, Guid userId, Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        throw new InboxUnavailableException();
}