using Confluent.Kafka;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace QuickPatch.Communication.Infrastructure.Messaging;

/// <summary>
/// Consumidor de eventos que se guardan en <c>db_communication</c>: el buzón (DD 5.22) y la réplica del chat (DD 5.23).
/// Cada uso tiene su propio grupo de consumidores: a diferencia del canal, que solo avisa lo que pasa mientras la app está
/// abierta, lo guardado no puede perder eventos. Confirma el offset solo después de guardar; si la base falla, vuelve al
/// mismo mensaje y reintenta. Los manejadores son idempotentes: un evento repetido no duplica nada.
/// </summary>
public sealed partial class PersistentEventsConsumer(
    string name,
    IConsumer<string, string> consumer,
    IReadOnlyList<string> topics,
    Func<IServiceProvider, string, CancellationToken, Task> handle,
    IServiceScopeFactory scopes,
    ILogger<PersistentEventsConsumer> logger,
    TimeSpan? retryDelay = null) : BackgroundService
{
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => ConsumeLoopAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        consumer.Subscribe(topics);
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
                    LogConsumeFailed(logger, name, ex.Error.Reason, ex);
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
                    await handle(scope.ServiceProvider, result.Message.Value, stoppingToken).ConfigureAwait(false);
                    consumer.Commit(result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogStoreFailed(logger, name, result.Topic, result.Offset.Value, ex);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo leer de Kafka para {Name} ({Reason}); se reintentará.")]
    private static partial void LogConsumeFailed(ILogger logger, string name, string reason, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Name}: no se pudo guardar {Topic} offset {Offset}; se reintentará.")]
    private static partial void LogStoreFailed(ILogger logger, string name, string topic, long offset, Exception exception);
}