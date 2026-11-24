using Confluent.Kafka;

using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Transversal.Messaging;

namespace QuickPatch.Communication.Infrastructure.Messaging;

/// <summary>
/// Consume los eventos de <see cref="EventTypes.Consumed"/> y los convierte en avisos del canal en tiempo real
/// (ADR-023, DD 8.5). Confirma el offset después de procesar cada mensaje; un mensaje que no cumple el contrato se
/// registra y se salta. Si el topic todavía no existe, espera y reintenta sin detener el servicio (POC-BE-001).
/// </summary>
public sealed partial class RealtimeEventsConsumer(
    IConsumer<string, string> consumer,
    RealtimeNotifier notifier,
    ILogger<RealtimeEventsConsumer> logger)
    : KafkaConsumerService(consumer, EventTypes.Consumed, logger)
{
    // Un aviso que no llega no se reintenta: la app tiene el respaldo de consulta cada 30 s (ADR-023).
    protected override async Task<bool> ProcessAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            await notifier.HandleAsync(result.Message.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogNotifyFailed(logger, result.Topic, ex);
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo avisar {Topic} por el canal en tiempo real; no se reintenta.")]
    private static partial void LogNotifyFailed(ILogger logger, string topic, Exception exception);
}