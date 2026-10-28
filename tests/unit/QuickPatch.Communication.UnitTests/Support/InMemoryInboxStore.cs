using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.UnitTests.Support;

/// <summary>
/// Buzón en memoria que se comporta como la tabla real: <c>UNIQUE (source_event_id, user_id)</c> y cada consulta solo ve
/// las filas del tenant y del usuario pedidos (como RLS más el filtro por usuario).
/// </summary>
internal sealed class InMemoryInboxStore : IInboxStore
{
    private readonly Lock _gate = new();

    public List<Notification> Rows { get; } = [];

    public bool Fail { get; set; }

    public void Seed(Notification notification)
    {
        lock (_gate)
        {
            Rows.Add(notification);
        }
    }

    public Task<int> AddAsync(Guid tenantId, IReadOnlyList<Notification> notifications, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new InvalidOperationException("La base no responde.");
        }

        lock (_gate)
        {
            var created = 0;
            foreach (var n in notifications)
            {
                if (n.TenantId != tenantId)
                {
                    throw new ArgumentException("Tenant distinto al de la transacción.", nameof(notifications));
                }

                if (!Rows.Any(r => r.SourceEventId == n.SourceEventId && r.UserId == n.UserId))
                {
                    Rows.Add(n);
                    created++;
                }
            }

            return Task.FromResult(created);
        }
    }

    public Task<NotificationPage> ListAsync(Guid tenantId, Guid userId, bool unreadOnly, int skip, int take, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var mine = Rows.Where(r => r.TenantId == tenantId && r.UserId == userId).ToList();
            var filtered = unreadOnly ? mine.Where(r => r.ReadAt is null).ToList() : mine;
            var items = filtered.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Skip(skip).Take(take).ToList();
            return Task.FromResult(new NotificationPage(items, filtered.Count, mine.Count(r => r.ReadAt is null)));
        }
    }

    public Task<Notification?> MarkReadAsync(Guid tenantId, Guid userId, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var row = Rows.SingleOrDefault(r => r.Id == id && r.TenantId == tenantId && r.UserId == userId);
            row?.MarkRead(now);
            return Task.FromResult(row);
        }
    }
}

/// <summary>Sobres de eventos como los publican los productores (DD 8.4.1).</summary>
internal static class Events
{
    public static string Json(string type, Guid tenant, object data, Guid? eventId = null, DateTimeOffset? occurredAt = null, int version = 1) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            eventId = eventId ?? Guid.NewGuid(),
            eventType = type,
            eventVersion = version,
            occurredAt = occurredAt ?? new DateTimeOffset(2026, 10, 12, 16, 10, 0, TimeSpan.Zero),
            correlationId = Guid.NewGuid(),
            tenantId = tenant,
            producer = "pruebas",
            data,
        });

    public static QuickPatch.Communication.Application.Events.EventEnvelope Envelope(string type, object data, Guid? tenant = null) =>
        System.Text.Json.JsonSerializer.Deserialize<QuickPatch.Communication.Application.Events.EventEnvelope>(
            Json(type, tenant ?? Guid.NewGuid(), data), QuickPatch.Communication.Application.Events.RealtimeJson.Options)!;
}