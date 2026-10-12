using Confluent.Kafka;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Communication.Infrastructure.Messaging;

namespace QuickPatch.Communication.Infrastructure;

/// <summary>
/// Capa de infraestructura: PostgreSQL, Kafka, Redis y adaptadores externos (SDD 6.3).
/// Implementa las interfaces que definen Application y Domain.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var kafka = configuration.GetSection(KafkaOptions.Section).Get<KafkaOptions>() ?? new KafkaOptions();
        if (kafka.Enabled)
        {
            services.AddSingleton(_ => new ConsumerBuilder<string, string>(new ConsumerConfig
            {
                BootstrapServers = kafka.BootstrapServers,
                GroupId = kafka.ConsumerGroupId,
                EnableAutoCommit = false,
                // Las conexiones viven en memoria: al arrancar no tiene sentido avisar de eventos viejos.
                AutoOffsetReset = AutoOffsetReset.Latest,
            }).Build());
            services.AddHostedService<RealtimeEventsConsumer>();
        }

        return services;
    }
}