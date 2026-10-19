using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Infrastructure.Messaging;
using QuickPatch.Communication.Infrastructure.Persistence;

namespace QuickPatch.Communication.Infrastructure;

/// <summary>
/// Capa de infraestructura: PostgreSQL, Kafka, Redis y adaptadores externos (SDD 6.3).
/// Implementa las interfaces que definen Application y Domain.
/// </summary>
public static class DependencyInjection
{
    public const string ConnectionStringName = "Communication";
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Buzón (DD 5.22) en db_communication con el rol communication_app, sujeto a RLS. Sin cadena de conexión
        // (desarrollo local) el canal en tiempo real funciona igual y el buzón responde 503.
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        var inboxEnabled = !string.IsNullOrWhiteSpace(connectionString);
        if (inboxEnabled)
        {
            services.AddDbContext<CommunicationDbContext>(options => options.UseNpgsql(connectionString));
            services.AddScoped<IInboxStore, InboxStore>();
            services.AddHealthChecks().AddDbContextCheck<CommunicationDbContext>("postgresql", tags: [ReadyTag]);
        }
        else
        {
            services.AddSingleton<IInboxStore, UnavailableInboxStore>();
        }

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

            if (inboxEnabled)
            {
                // Consumidor propio: un IConsumer solo tiene una suscripción, y el buzón no puede perder eventos.
                services.AddHostedService(sp => new InboxEventsConsumer(
                    new ConsumerBuilder<string, string>(new ConsumerConfig
                    {
                        BootstrapServers = kafka.BootstrapServers,
                        GroupId = kafka.InboxConsumerGroupId,
                        EnableAutoCommit = false,
                        AutoOffsetReset = AutoOffsetReset.Earliest,
                    }).Build(),
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<ILogger<InboxEventsConsumer>>()));
            }
        }

        return services;
    }
}