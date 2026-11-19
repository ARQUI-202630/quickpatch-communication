using System.Text.Json;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Domain.Chat;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Application.Chat;

/// <summary>
/// <c>POST /v1/service-requests/{id}/messages</c> (contrato 1.3.0, ADR-026): guarda el mensaje y lo entrega por el canal
/// (<c>chat.message</c>) al otro participante y a las demás conexiones del autor. El autor y su papel salen del JWT y de
/// la réplica, nunca del cuerpo.
/// </summary>
public sealed partial class SendChatMessageHandler(
    IChatStore store, IRealtimeConnections connections, TimeProvider clock, ILogger<SendChatMessageHandler> logger)
{
    public async Task<ChatResult> HandleAsync(
        Guid tenantId, Guid userId, Guid serviceRequestId, string? text, CancellationToken cancellationToken)
    {
        var body = ChatMessage.NormalizeText(text)
            ?? throw new ChatValidationException(new Dictionary<string, string[]>
            {
                ["text"] = [$"Escribe entre 1 y {ChatMessage.MaxLength} caracteres."],
            });

        var participants = await store.FindAsync(tenantId, serviceRequestId, cancellationToken).ConfigureAwait(false);
        var role = participants?.RoleOf(userId);
        if (participants is null || role is null)
        {
            return new ChatResult.NotFound();
        }

        if (!participants.IsWritable)
        {
            return participants.IsClosed ? new ChatResult.Closed() : new ChatResult.NoTechnician();
        }

        var message = new ChatMessage(
            Guid.CreateVersion7(clock.GetUtcNow()), tenantId, serviceRequestId, userId, role, body, clock.GetUtcNow());
        await store.AddAsync(message, cancellationToken).ConfigureAwait(false);

        // La entrega en tiempo real es lo mejor posible: el mensaje ya quedó guardado y el otro lo ve al abrir el chat.
        var notice = new ChatMessageNotice(
            serviceRequestId, new ChatMessageFrame(message.Id, message.AuthorId, message.AuthorRole, message.Body, message.SentAt));
        var sent = 0;
        foreach (var recipient in new[] { participants.ClientId, participants.TechnicianId!.Value })
        {
            try
            {
                sent += await connections.DeliverAsync(
                    new Delivery(tenantId, new UserRecipient(recipient), notice), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDeliveryFailed(logger, serviceRequestId, ex);
            }
        }

        LogSent(logger, serviceRequestId, tenantId, sent);
        return new ChatResult.Sent(message);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Mensaje del chat de {ServiceRequestId} (tenant {TenantId}) entregado a {Sent} conexiones.")]
    private static partial void LogSent(ILogger logger, Guid serviceRequestId, Guid tenantId, int sent);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo entregar por el canal un mensaje del chat de {ServiceRequestId}.")]
    private static partial void LogDeliveryFailed(ILogger logger, Guid serviceRequestId, Exception exception);
}