using Confluent.Kafka;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Chat;
using QuickPatch.Communication.Application.Events;
using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Application.Realtime;
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
            services.AddScoped<IChatStore, ChatStore>();
            services.AddHealthChecks().AddDbContextCheck<CommunicationDbContext>("postgresql", tags: [ReadyTag]);
        }
        else
        {
            services.AddSingleton<IInboxStore, UnavailableInboxStore>();
            services.AddSingleton<IChatStore, UnavailableChatStore>();
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
                // Consumidores propios: un IConsumer solo tiene una suscripción, y lo guardado no puede perder eventos. Se
                // registran como IHostedService y no con AddHostedService: ese usa TryAddEnumerable, que con dos fábricas del
                // mismo tipo descarta la segunda sin avisar.
                services.AddSingleton<IHostedService>(sp => Persistent(
                    sp, "buzón", kafka.InboxConsumerGroupId, EventTypes.Inbox,
                    (scope, message, ct) => scope.GetRequiredService<InboxRecorder>().HandleAsync(message, ct)));

                // Réplica de los participantes del chat (DD 5.23, ADR-026). Grupo aparte que lee desde el principio, para que
                // al desplegarse conozca las solicitudes que ya tienen técnico.
                services.AddSingleton<IHostedService>(sp => Persistent(
                    sp, "chat", kafka.ChatConsumerGroupId, [EventTypes.ServiceRequestStatusChanged],
                    (scope, message, ct) => scope.GetRequiredService<ChatParticipantsRecorder>().HandleAsync(message, ct)));
            }
        }

        return services;

        PersistentEventsConsumer Persistent(
            IServiceProvider sp, string name, string groupId, IReadOnlyList<string> topics,
            Func<IServiceProvider, string, CancellationToken, Task> handle) =>
            new(
                name,
                new ConsumerBuilder<string, string>(new ConsumerConfig
                {
                    BootstrapServers = kafka.BootstrapServers,
                    GroupId = groupId,
                    EnableAutoCommit = false,
                    AutoOffsetReset = AutoOffsetReset.Earliest,
                }).Build(),
                topics,
                handle,
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<PersistentEventsConsumer>>());
    }
}