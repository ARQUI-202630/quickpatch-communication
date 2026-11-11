using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using QuickPatch.Transversal.Messaging;

namespace QuickPatch.Communication.Infrastructure.Messaging;

/// <summary>
/// Consumidor de eventos que se guardan en <c>db_communication</c>: el buzón (DD 5.22) y la réplica del chat (DD 5.23).
/// Cada uso tiene su propio grupo de consumidores: a diferencia del canal, que solo avisa lo que pasa mientras la app está
/// abierta, lo guardado no puede perder eventos. Confirma el offset solo después de guardar; si la base falla, vuelve al
/// mismo mensaje y reintenta. Los manejadores son idempotentes: un evento repetido no duplica nada.
/// </summary>
public sealed class PersistentEventsConsumer(
    string name,
    IConsumer<string, string> consumer,
    IReadOnlyList<string> topics,
    Func<IServiceProvider, string, CancellationToken, Task> handle,
    IServiceScopeFactory scopes,
    ILogger<PersistentEventsConsumer> logger,
    TimeSpan? retryDelay = null)
    : KafkaConsumerService(consumer, topics, logger, retryDelay, retryDelay ?? TimeSpan.FromSeconds(5))
{
    protected override string Name => name;

    // Si guardar falla, la excepción llega al bucle común, que vuelve al mismo mensaje y reintenta.
    protected override async Task<bool> ProcessAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await handle(scope.ServiceProvider, result.Message.Value, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }
}