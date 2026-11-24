using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.Application.Inbox;

/// <summary><c>POST /v1/notifications/{id}/read</c> (contrato 1.0.0): idempotente; de otro usuario o tenant, null (404).</summary>
public sealed class MarkNotificationReadHandler(IInboxStore store, TimeProvider clock)
{
    public Task<Notification?> HandleAsync(Guid tenantId, Guid userId, Guid id, CancellationToken cancellationToken) =>
        store.MarkReadAsync(tenantId, userId, id, clock.GetUtcNow(), cancellationToken);
}