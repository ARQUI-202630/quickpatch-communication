using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Chat;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Application.Chat;

/// <summary><c>GET /v1/service-requests/{id}/messages</c> (contrato 1.3.0): página desde 1 y hasta 100 por página.</summary>
public sealed class ListChatMessagesHandler(IChatStore store)
{
    public const int DefaultSize = 30;
    public const int MaxSize = 100;

    public async Task<ChatResult> HandleAsync(
        Guid tenantId, Guid userId, Guid serviceRequestId, int? page, int? size, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var pageNumber = page ?? 1;
        var pageSize = size ?? DefaultSize;
        if (pageNumber < 1)
        {
            errors["page"] = ["La página empieza en 1."];
        }

        if (pageSize is < 1 or > MaxSize)
        {
            errors["size"] = [$"El tamaño de página va de 1 a {MaxSize}."];
        }

        if (errors.Count > 0)
        {
            throw new ChatValidationException(errors);
        }

        var participants = await store.FindAsync(tenantId, serviceRequestId, cancellationToken).ConfigureAwait(false);
        if (participants?.RoleOf(userId) is null)
        {
            return new ChatResult.NotFound();
        }

        var result = await store.ListAsync(tenantId, serviceRequestId, (pageNumber - 1) * pageSize, pageSize, cancellationToken)
            .ConfigureAwait(false);
        return new ChatResult.History(result.Items, pageNumber, pageSize, result.TotalItems, participants.IsWritable);
    }
}