using Microsoft.EntityFrameworkCore;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Chat;
using QuickPatch.Communication.Domain.Chat;

namespace QuickPatch.Communication.Infrastructure.Persistence;

/// <summary>
/// Chat en PostgreSQL (DD 5.23 y 5.24, ADR-026). Cada operación abre una transacción y fija <c>app.current_tenant</c> con
/// <c>set_config(..., true)</c>: RLS deja ver y escribir solo las filas de ese tenant.
/// </summary>
public sealed class ChatStore(CommunicationDbContext db) : IChatStore
{
    public Task<bool> ApplyAsync(ChatParticipants participants, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(participants);
        var p = participants;
        return InTenantAsync(p.TenantId, async ct =>
        {
            // RN-CH3: solo un evento más nuevo cambia la fila; uno viejo o repetido no hace nada.
            var changed = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO chat_participants (service_request_id, tenant_id, client_id, technician_id, status, status_changed_at)
                VALUES ({p.ServiceRequestId}, {p.TenantId}, {p.ClientId}, {p.TechnicianId}, {p.Status}, {p.StatusChangedAt})
                ON CONFLICT (service_request_id) DO UPDATE
                SET client_id = EXCLUDED.client_id, technician_id = EXCLUDED.technician_id, status = EXCLUDED.status,
                    status_changed_at = EXCLUDED.status_changed_at
                WHERE chat_participants.status_changed_at < EXCLUDED.status_changed_at
                """,
                ct);
            return changed > 0;
        }, cancellationToken);
    }

    public Task<ChatParticipants?> FindAsync(Guid tenantId, Guid serviceRequestId, CancellationToken cancellationToken) =>
        InTenantAsync(tenantId, ct => db.ChatParticipants.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ServiceRequestId == serviceRequestId && x.TenantId == tenantId, ct), cancellationToken);

    public Task<ChatMessagePage> ListAsync(Guid tenantId, Guid serviceRequestId, int skip, int take, CancellationToken cancellationToken) =>
        InTenantAsync(tenantId, async ct =>
        {
            var messages = db.ChatMessages.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ServiceRequestId == serviceRequestId);
            var total = await messages.CountAsync(ct);
            var items = await messages
                .OrderByDescending(x => x.SentAt)
                .ThenByDescending(x => x.Id)
                .Skip(skip)
                .Take(take)
                .ToListAsync(ct);
            return new ChatMessagePage(items, total);
        }, cancellationToken);

    public Task AddAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return InTenantAsync(message.TenantId, _ =>
        {
            db.ChatMessages.Add(message);
            return Task.FromResult(true);
        }, cancellationToken);
    }

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

/// <summary>Sin <c>ConnectionStrings:Communication</c> (desarrollo local): el chat responde 503.</summary>
public sealed class UnavailableChatStore : IChatStore
{
    public Task<bool> ApplyAsync(ChatParticipants participants, CancellationToken cancellationToken) =>
        throw new ChatUnavailableException();

    public Task<ChatParticipants?> FindAsync(Guid tenantId, Guid serviceRequestId, CancellationToken cancellationToken) =>
        throw new ChatUnavailableException();

    public Task<ChatMessagePage> ListAsync(Guid tenantId, Guid serviceRequestId, int skip, int take, CancellationToken cancellationToken) =>
        throw new ChatUnavailableException();

    public Task AddAsync(ChatMessage message, CancellationToken cancellationToken) => throw new ChatUnavailableException();
}