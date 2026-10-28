using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Chat;
using QuickPatch.Communication.Domain.Chat;

namespace QuickPatch.Communication.UnitTests.Support;

/// <summary>
/// Chat en memoria que se comporta como las tablas reales: cada consulta solo ve las filas del tenant pedido (como RLS) y
/// la réplica solo cambia con un estado más nuevo (RN-CH3).
/// </summary>
internal sealed class InMemoryChatStore : IChatStore
{
    private readonly Lock _gate = new();

    public Dictionary<Guid, ChatParticipants> Participants { get; } = [];

    public List<ChatMessage> Messages { get; } = [];

    public bool Fail { get; set; }

    public Task<bool> ApplyAsync(ChatParticipants participants, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new InvalidOperationException("La base no responde.");
        }

        lock (_gate)
        {
            if (Participants.TryGetValue(participants.ServiceRequestId, out var current)
                && current.StatusChangedAt >= participants.StatusChangedAt)
            {
                return Task.FromResult(false);
            }

            Participants[participants.ServiceRequestId] = participants;
            return Task.FromResult(true);
        }
    }

    public Task<ChatParticipants?> FindAsync(Guid tenantId, Guid serviceRequestId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(Participants.TryGetValue(serviceRequestId, out var p) && p.TenantId == tenantId ? p : null);
        }
    }

    public Task<ChatMessagePage> ListAsync(Guid tenantId, Guid serviceRequestId, int skip, int take, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var mine = Messages.Where(m => m.TenantId == tenantId && m.ServiceRequestId == serviceRequestId)
                .OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id).ToList();
            return Task.FromResult(new ChatMessagePage([.. mine.Skip(skip).Take(take)], mine.Count));
        }
    }

    public Task AddAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Messages.Add(message);
        }

        return Task.CompletedTask;
    }
}