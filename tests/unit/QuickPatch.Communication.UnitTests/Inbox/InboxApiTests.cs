using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Domain.Inbox;
using QuickPatch.Communication.UnitTests.Realtime;

namespace QuickPatch.Communication.UnitTests.Inbox;

/// <summary>API del buzón (<c>communication.v1.yaml</c> 1.0.0) con el almacenamiento en memoria.</summary>
public sealed class InboxApiTests : IClassFixture<InboxApiTests.Factory>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 16, 0, 0, TimeSpan.Zero);
    private readonly Factory _factory;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();

    public InboxApiTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        internal InMemoryInboxStore Store { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem);
            builder.ConfigureTestServices(services => services.AddSingleton<IInboxStore>(Store));
        }
    }

    private HttpClient Client(string role = "cliente", Guid? user = null, Guid? tenant = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(user ?? _user, tenant ?? _tenant, role));
        return client;
    }

    private Notification Add(int minutes, Guid? user = null, Guid? serviceRequestId = null, string type = NotificationTypes.TecnicoAsignado)
    {
        var n = new Notification(Guid.NewGuid(), _tenant, user ?? _user, type, "Ya tienes técnico",
            "Un técnico aceptó tu solicitud y va a ponerse en contacto contigo.", serviceRequestId, Guid.NewGuid(), T0.AddMinutes(minutes));
        _factory.Store.Seed(n);

        return n;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("cliente")]
    [InlineData("empresa_contacto")]
    [InlineData("tecnico")]
    [InlineData("admin_tenant")]
    public async Task Listar_RolesConBuzon_DevuelvenSusNotificacionesSegunElContrato(string role)
    {
        var request = Guid.NewGuid();
        var user = Guid.NewGuid();
        var n = Add(1, user, request);
        Add(2, user, type: NotificationTypes.RegistroRecibido);
        Add(3);
        using var client = Client(role, user);

        using var response = await client.GetAsync(new Uri("/v1/notifications", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(1, json.GetProperty("page").GetInt32());
        Assert.Equal(20, json.GetProperty("size").GetInt32());
        Assert.Equal(2, json.GetProperty("totalItems").GetInt32());
        Assert.Equal(2, json.GetProperty("unreadCount").GetInt32());
        var items = json.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("serviceRequestId").ValueKind);
        var item = items[1];
        Assert.Equal(n.Id, item.GetProperty("id").GetGuid());
        Assert.Equal("tecnico-asignado", item.GetProperty("type").GetString());
        Assert.Equal("Ya tienes técnico", item.GetProperty("title").GetString());
        Assert.Equal(request, item.GetProperty("serviceRequestId").GetGuid());
        Assert.Equal(T0.AddMinutes(1), item.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("readAt").ValueKind);
        Assert.Equal(7, item.EnumerateObject().Count());
    }

    [Fact]
    public async Task Listar_OtroTenantConElMismoUsuario_NoVeNada()
    {
        Add(1);
        using var client = Client(tenant: Guid.NewGuid());

        var json = await Json(await client.GetAsync(new Uri("/v1/notifications", UriKind.Relative)));

        Assert.Equal(0, json.GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Listar_SoloNoLeidasYPaginado()
    {
        var user = Guid.NewGuid();
        Add(1, user);
        Add(2, user);
        Add(3, user);
        using var client = Client(user: user);

        var json = await Json(await client.GetAsync(new Uri("/v1/notifications?unread=true&page=2&size=2", UriKind.Relative)));

        Assert.Equal(1, json.GetProperty("items").GetArrayLength());
        Assert.Equal(3, json.GetProperty("totalItems").GetInt32());
    }

    [Theory]
    [InlineData("unread=quizas", "unread")]
    [InlineData("page=0", "page")]
    [InlineData("page=uno", "page")]
    [InlineData("size=101", "size")]
    public async Task Listar_ParametrosInvalidos_Responde400ConElCampo(string query, string field)
    {
        using var client = Client();

        using var response = await client.GetAsync(new Uri($"/v1/notifications?{query}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("https://quickpatch.internal/problems/validacion", json.GetProperty("type").GetString());
        Assert.True(json.GetProperty("errors").TryGetProperty(field, out _));
        Assert.True(json.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task Listar_SinToken_Responde401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/v1/notifications", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Listar_TokenSinTenant_Responde401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(_user, _tenant, "cliente", includeTenant: false));

        using var response = await client.GetAsync(new Uri("/v1/notifications", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("proveedor")]
    [InlineData("admin_plataforma")]
    public async Task Listar_RolSinBuzon_Responde403(string role)
    {
        using var client = Client(role);

        using var response = await client.GetAsync(new Uri("/v1/notifications", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MarcarLeida_DevuelveLaNotificacionConReadAtYEsIdempotente()
    {
        var n = Add(1);
        var correlation = Guid.NewGuid();
        using var client = Client();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", correlation.ToString());

        using var first = await client.PostAsync(new Uri($"/v1/notifications/{n.Id}/read", UriKind.Relative), null);
        using var second = await client.PostAsync(new Uri($"/v1/notifications/{n.Id}/read", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(correlation.ToString(), first.Headers.GetValues("X-Correlation-Id").Single());
        var readAt = (await Json(first)).GetProperty("readAt").GetDateTimeOffset();
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(readAt, (await Json(second)).GetProperty("readAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task MarcarLeida_DeOtroUsuarioOInexistente_Responde404()
    {
        var ajena = Add(1, Guid.NewGuid());
        using var client = Client();

        using var otra = await client.PostAsync(new Uri($"/v1/notifications/{ajena.Id}/read", UriKind.Relative), null);
        using var inexistente = await client.PostAsync(new Uri($"/v1/notifications/{Guid.NewGuid()}/read", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.NotFound, otra.StatusCode);
        Assert.Equal("https://quickpatch.internal/problems/no-encontrado", (await Json(otra)).GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Null(ajena.ReadAt);
    }
}

/// <summary>Sin base configurada el buzón responde 503 y el resto del servicio sigue funcionando.</summary>
public sealed class InboxWithoutDatabaseTests(InboxWithoutDatabaseTests.Factory factory) : IClassFixture<InboxWithoutDatabaseTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem);
    }

    [Fact]
    public async Task SinBase_Responde503()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(Guid.NewGuid(), Guid.NewGuid(), "cliente"));

        using var list = await client.GetAsync(new Uri("/v1/notifications", UriKind.Relative));
        using var read = await client.PostAsync(new Uri($"/v1/notifications/{Guid.NewGuid()}/read", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, list.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
    }
}