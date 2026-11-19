using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Inbox;

namespace QuickPatch.Communication.Application.Inbox;

/// <summary>Consulta del buzón. <see cref="TenantId"/> y <see cref="UserId"/> vienen del JWT.</summary>
public sealed record ListNotificationsQuery(Guid TenantId, Guid UserId, bool UnreadOnly, int? Page, int? Size);

public sealed record NotificationList(IReadOnlyList<Notification> Items, int Page, int Size, int TotalItems, int UnreadCount);

/// <summary><c>GET /v1/notifications</c> (contrato 1.0.0): página desde 1 y hasta 100 por página (DD 8.1).</summary>
public sealed class ListNotificationsHandler(IInboxStore store)
{
    public const int DefaultSize = 20;
    public const int MaxSize = 100;

    public async Task<NotificationList> HandleAsync(ListNotificationsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var errors = new Dictionary<string, string[]>();
        var page = query.Page ?? 1;
        var size = query.Size ?? DefaultSize;
        if (page < 1)
        {
            errors["page"] = ["La página empieza en 1."];
        }

        if (size is < 1 or > MaxSize)
        {
            errors["size"] = [$"El tamaño de página va de 1 a {MaxSize}."];
        }

        if (errors.Count > 0)
        {
            throw new InboxValidationException(errors);
        }

        var result = await store.ListAsync(query.TenantId, query.UserId, query.UnreadOnly, (page - 1) * size, size, cancellationToken)
            .ConfigureAwait(false);
        return new NotificationList(result.Items, page, size, result.TotalItems, result.UnreadCount);
    }
}