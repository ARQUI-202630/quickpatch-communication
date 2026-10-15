using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;

namespace QuickPatch.Communication.Infrastructure.Messaging;

/// <summary>
/// Consume los eventos de <see cref="EventTypes.Inbox"/> y guarda las notificaciones del buzón (DD 5.22). Tiene su propio
/// grupo de consumidores: a diferencia del canal, que solo avisa lo que pasa mientras la app está abierta, el buzón no
/// puede perder eventos. Confirma el offset solo después de guardar; si la base falla, vuelve al mismo mensaje y
/// reintenta. Un evento repetido no duplica nada (<c>UNIQUE (source_event_id, user_id)</c>).
/// </summary>
public sealed partial class InboxEventsConsumer(
    IConsumer<string, string> consumer,
    IServiceScopeFactory scopes,
    ILogger<InboxEventsConsumer> logger,
    TimeSpan? retryDelay = null) : BackgroundService
{
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => ConsumeLoopAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        consumer.Subscribe(EventTypes.Inbox);
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
                    await Task.Delay(_retryDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (result?.Message?.Value is null)
                {
                    continue;
                }

                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<InboxRecorder>()
                        .HandleAsync(result.Message.Value, stoppingToken).ConfigureAwait(false);
                    consumer.Commit(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogStoreFailed(logger, result.Topic, result.Offset.Value, ex);
                    consumer.Seek(result.TopicPartitionOffset);
                    await Task.Delay(_retryDelay, stoppingToken).ConfigureAwait(false);
                }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo leer de Kafka para el buzón ({Reason}); se reintentará.")]
    private static partial void LogConsumeFailed(ILogger logger, string reason, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "No se pudo guardar la notificación de {Topic} offset {Offset}; se reintentará.")]
    private static partial void LogStoreFailed(ILogger logger, string topic, long offset, Exception exception);
}