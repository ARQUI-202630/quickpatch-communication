using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using QuickPatch.Communication.Application.Realtime;
using QuickPatch.Communication.Infrastructure.Persistence;

using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace QuickPatch.Communication.IntegrationTests;

/// <summary>
/// Buzón de punta a punta (SCRUM-456, DD 5.22) con PostgreSQL 16 y Kafka reales: los eventos crean notificaciones en el
/// tenant del evento, un evento repetido no las duplica, cada usuario ve solo las suyas y RLS aísla los tenants aunque
/// el servicio se equivoque de filtro. El servicio se conecta con <c>communication_app</c> (sin BYPASSRLS).
/// </summary>
public sealed class InboxFlowTests : IAsyncLifetime
{
    private const string AppPassword = "app-pruebas";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16").WithDatabase("db_communication").Build();
    private readonly KafkaContainer _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.7.1").Build();
    private WebApplicationFactory<Program>? _factory;

    private string AppConnectionString => new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
    {
        Username = "communication_app",
        Password = AppPassword,
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync());

        var options = new DbContextOptionsBuilder<CommunicationDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options;
        await using (var db = new CommunicationDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await ExecuteAdminAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "roles.sql")));
        await ExecuteAdminAsync($"ALTER ROLE communication_app LOGIN PASSWORD '{AppPassword}';");

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _kafka.GetBootstrapAddress() }).Build();
        await admin.CreateTopicsAsync(EventTypes.Inbox.Select(t => new TopicSpecification { Name = t, NumPartitions = 1, ReplicationFactor = 1 }));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem);
            builder.UseSetting("ConnectionStrings:Communication", AppConnectionString);
            builder.UseSetting("Kafka:BootstrapServers", _kafka.GetBootstrapAddress());
            builder.UseSetting("Kafka:ConsumerGroupId", "communication-service-pruebas");
            builder.UseSetting("Kafka:InboxConsumerGroupId", "communication-inbox-pruebas");
        });
        _ = _factory.Server;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _kafka.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Eventos_CreanElBuzonDeCadaUsuarioSinDuplicarYSinMezclarTenants()
    {
        var tenant = Guid.NewGuid();
        var client = Guid.NewGuid();
        var technician = Guid.NewGuid();
        var request = Guid.NewGuid();
        var assigned = Guid.NewGuid();
        // service-request.status-changed a asignado (SCRUM-466): avisa al cliente y al técnico.
        var data = new
        {
            serviceRequestId = request,
            clientId = client,
            technicianId = technician,
            previousStatus = "buscando_tecnico",
            status = "asignado",
            changedAt = "2026-10-12T16:10:00Z",
        };
        const string topic = EventTypes.ServiceRequestStatusChanged;

        await PublishAsync(topic, Event(topic, tenant, data, assigned));
        await PublishAsync(topic, Event(topic, tenant, data, assigned));
        // Un evento posterior en el mismo topic: cuando aparece, el repetido ya se procesó.
        var marker = Guid.NewGuid();
        await PublishAsync(topic, Event(topic, tenant, data with { }, marker));
        await WaitUntilAsync(async () => await CountAsync("source_event_id = @e", marker) == 2, "el evento posterior crea sus notificaciones");

        Assert.Equal(2, await CountAsync("source_event_id = @e", assigned));

        using (var http = As(client, tenant, "cliente"))
        {
            var json = await JsonAsync(await http.GetAsync(new Uri("/v1/notifications", UriKind.Relative)));
            Assert.Equal(2, json.GetProperty("totalItems").GetInt32());
            Assert.Equal(2, json.GetProperty("unreadCount").GetInt32());
            Assert.All(json.GetProperty("items").EnumerateArray(), i => Assert.Equal("tecnico-asignado", i.GetProperty("type").GetString()));
            Assert.Equal(request, json.GetProperty("items")[0].GetProperty("serviceRequestId").GetGuid());

            var id = json.GetProperty("items")[0].GetProperty("id").GetGuid();
            using var read = await http.PostAsync(new Uri($"/v1/notifications/{id}/read", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var unread = await JsonAsync(await http.GetAsync(new Uri("/v1/notifications?unread=true", UriKind.Relative)));
            Assert.Equal(1, unread.GetProperty("totalItems").GetInt32());
            Assert.Equal(1, unread.GetProperty("unreadCount").GetInt32());
        }

        using (var http = As(technician, tenant, "tecnico"))
        {
            var json = await JsonAsync(await http.GetAsync(new Uri("/v1/notifications", UriKind.Relative)));
            Assert.All(json.GetProperty("items").EnumerateArray(), i => Assert.Equal("solicitud-asignada", i.GetProperty("type").GetString()));
            Assert.Equal(2, json.GetProperty("totalItems").GetInt32());

            // La notificación del cliente no existe para el técnico.
            var ofClient = await ScalarAdminAsync<Guid>("SELECT id FROM notifications WHERE user_id = @u LIMIT 1", ("u", client));
            using var read = await http.PostAsync(new Uri($"/v1/notifications/{ofClient}/read", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        }

        // El mismo usuario con un token de otro tenant no ve nada.
        using (var http = As(client, Guid.NewGuid(), "cliente"))
        {
            var json = await JsonAsync(await http.GetAsync(new Uri("/v1/notifications", UriKind.Relative)));
            Assert.Equal(0, json.GetProperty("totalItems").GetInt32());
        }
    }

    [Fact]
    public async Task RegistroYVerificacion_LleganAlBuzonDelTecnico()
    {
        var tenant = Guid.NewGuid();
        var technician = Guid.NewGuid();

        await PublishAsync(EventTypes.TechnicianRegistered, Event(EventTypes.TechnicianRegistered, tenant, new
        {
            technicianId = technician,
            specialtyId = Guid.NewGuid(),
            providerId = (Guid?)null,
            verificationStatus = "pendiente",
            updatedAt = "2026-10-12T16:00:00Z",
        }));
        await PublishAsync(EventTypes.VerificationChanged, Event(EventTypes.VerificationChanged, tenant, new
        {
            technicianId = technician,
            specialtyId = Guid.NewGuid(),
            previousStatus = "pendiente",
            verificationStatus = "aprobado",
            reason = (string?)null,
            updatedAt = "2026-10-12T17:00:00Z",
        }, occurredAt: "2026-10-12T17:00:00Z"));
        await WaitUntilAsync(async () => await CountAsync("user_id = @e", technician) == 2, "llegan las dos notificaciones");

        using var http = As(technician, tenant, "tecnico");
        var items = (await JsonAsync(await http.GetAsync(new Uri("/v1/notifications", UriKind.Relative)))).GetProperty("items");
        Assert.Equal("verificacion-actualizada", items[0].GetProperty("type").GetString());
        Assert.Equal("registro-recibido", items[1].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("serviceRequestId").ValueKind);
    }

    [Fact]
    public async Task Cotizacion_LlegaAlBuzon_YElRechazoNoRepiteLaAsignacion()
    {
        var (tenant, client, technician, request) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await PublishAsync(EventTypes.Quoted, Event(EventTypes.Quoted, tenant, new
        {
            serviceRequestId = request,
            quoteId = Guid.NewGuid(),
            clientId = client,
            technicianId = technician,
            quoteNumber = 1,
            totalAmount = "185000.00",
            expiresAt = "2026-10-20T20:30:00Z",
        }));
        await PublishAsync(EventTypes.QuoteRejected, Event(EventTypes.QuoteRejected, tenant, new
        {
            serviceRequestId = request,
            quoteId = Guid.NewGuid(),
            technicianId = technician,
            remainingQuotes = 2,
        }));

        // El regreso de cotizado a asignado (SCRUM-488) va por el canal, pero no crea otra notificación de asignación.
        await PublishAsync(EventTypes.ServiceRequestStatusChanged, Event(EventTypes.ServiceRequestStatusChanged, tenant, new
        {
            serviceRequestId = request,
            clientId = client,
            technicianId = technician,
            previousStatus = "cotizado",
            status = "asignado",
            changedAt = "2026-10-19T16:10:00Z",
        }));

        await WaitUntilAsync(async () => await CountAsync("user_id = @e", technician) == 1, "llega el rechazo al técnico");
        await WaitUntilAsync(async () => await CountAsync("user_id = @e", client) == 1, "llega la cotización al cliente");

        Assert.Equal("cotizacion-recibida", await ScalarAdminAsync<string>("SELECT type FROM notifications WHERE user_id = @e", ("e", client)));
        Assert.Equal("cotizacion-rechazada", await ScalarAdminAsync<string>("SELECT type FROM notifications WHERE user_id = @e", ("e", technician)));
    }

    [Fact]
    public async Task Rls_ElRolDelServicioSoloVeElTenantFijadoYSoloCambiaReadAt()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var user = Guid.NewGuid();
        await ExecuteAdminAsync($"""
            INSERT INTO notifications (id, tenant_id, user_id, type, title, message, source_event_id, created_at)
            VALUES ('{Guid.NewGuid()}', '{tenantA}', '{user}', 'registro-recibido', 't', 'm', '{Guid.NewGuid()}', now()),
                   ('{Guid.NewGuid()}', '{tenantB}', '{user}', 'registro-recibido', 't', 'm', '{Guid.NewGuid()}', now());
            """);

        await using var connection = new NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync();
        Assert.Equal(0L, await ScalarAsync(connection, null, $"SELECT count(*) FROM notifications WHERE user_id = '{user}'"));
        Assert.Equal(1L, await ScalarAsync(connection, tenantA, $"SELECT count(*) FROM notifications WHERE user_id = '{user}'"));

        // Insertar en otro tenant que el fijado viola la política (WITH CHECK).
        var insert = Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(connection, tenantA, $"""
            INSERT INTO notifications (id, tenant_id, user_id, type, title, message, source_event_id, created_at)
            VALUES ('{Guid.NewGuid()}', '{tenantB}', '{user}', 'registro-recibido', 't', 'm', '{Guid.NewGuid()}', now()) RETURNING 1
            """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await insert).SqlState);

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            ScalarAsync(connection, tenantA, $"UPDATE notifications SET title = 'x' WHERE user_id = '{user}' RETURNING 1"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);
        Assert.Equal(1, await ScalarAsync(connection, tenantA, $"UPDATE notifications SET read_at = now() WHERE user_id = '{user}' RETURNING 1"));
    }

    private static string Event(string type, Guid tenant, object data, Guid? eventId = null, string occurredAt = "2026-10-12T16:10:00Z") =>
        JsonSerializer.Serialize(new
        {
            eventId = eventId ?? Guid.NewGuid(),
            eventType = type,
            eventVersion = 1,
            occurredAt,
            correlationId = Guid.NewGuid(),
            tenantId = tenant,
            producer = "pruebas",
            data,
        });

    private HttpClient As(Guid user, Guid tenant, string role)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant, role));
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.Clone();
        }
    }

    private async Task PublishAsync(string topic, string value)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _kafka.GetBootstrapAddress() }).Build();
        await producer.ProduceAsync(topic, new Message<string, string> { Key = Guid.NewGuid().ToString(), Value = value });
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T?> ScalarAdminAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private async Task<long> CountAsync(string where, Guid value) =>
        await ScalarAdminAsync<long>($"SELECT count(*) FROM notifications WHERE {where}", ("e", value));

    /// <summary>Ejecuta en una transacción con el tenant fijado (o sin fijar, si es null), como el servicio.</summary>
    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, Guid? tenant, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        if (tenant is { } t)
        {
            await using var set = new NpgsqlCommand($"SELECT set_config('app.current_tenant', '{t}', true)", connection, transaction);
            await set.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var result = await command.ExecuteScalarAsync();
        await transaction.RollbackAsync();
        return result;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string description, int seconds = 60)
    {
        var limit = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < limit)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"No se cumplió en {seconds} s: {description}");
    }
}