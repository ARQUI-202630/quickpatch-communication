using Confluent.Kafka;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Realtime;

namespace QuickPatch.Communication.Infrastructure.Messaging;

/// <summary>
/// Consume los eventos de <see cref="EventTypes.Consumed"/> y los convierte en avisos del canal en tiempo real
/// (ADR-023, DD 8.5). Confirma el offset después de procesar cada mensaje; un mensaje que no cumple el contrato se
/// registra y se salta. Si el topic todavía no existe, espera y reintenta sin detener el servicio (POC-BE-001).
/// </summary>
public sealed partial class RealtimeEventsConsumer(
    IConsumer<string, string> consumer,
    RealtimeNotifier notifier,
    ILogger<RealtimeEventsConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => ConsumeLoopAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        consumer.Subscribe(EventTypes.Consumed);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    LogConsumeFailed(logger, ex.Error.Reason, ex);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (result?.Message?.Value is null)
                {
                    continue;
                }

                // Un aviso que no llega no se reintenta: la app tiene el respaldo de consulta cada 30 s (ADR-023).
                await notifier.HandleAsync(result.Message.Value, stoppingToken).ConfigureAwait(false);
                consumer.Commit(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo leer de Kafka ({Reason}); se reintentará.")]
    private static partial void LogConsumeFailed(ILogger logger, string reason, Exception exception);
}