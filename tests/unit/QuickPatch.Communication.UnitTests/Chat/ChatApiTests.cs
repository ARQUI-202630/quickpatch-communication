using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using QuickPatch.Communication.Application.Chat;
using QuickPatch.Communication.Domain.Chat;
using QuickPatch.Communication.UnitTests.Realtime;

namespace QuickPatch.Communication.UnitTests.Chat;

/// <summary>API del chat (<c>communication.v1.yaml</c> 1.3.0) con el almacenamiento en memoria.</summary>
public sealed class ChatApiTests : IClassFixture<ChatApiTests.Factory>
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);
    private readonly Factory _factory;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly Guid _technician = Guid.NewGuid();
    private readonly Guid _request = Guid.NewGuid();

    public ChatApiTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        internal InMemoryChatStore Store { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem);
            builder.ConfigureTestServices(services => services.AddSingleton<IChatStore>(Store));
        }
    }

    private Uri Route(Guid? request = null, string query = "") => new($"/v1/service-requests/{request ?? _request}/messages{query}", UriKind.Relative);

    private HttpClient Client(Guid user, string role = "cliente", Guid? tenant = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user, tenant ?? _tenant, role));
        return client;
    }

    private void Seed(string status = "asignado", Guid? technician = null) =>
        _factory.Store.Participants[_request] = new ChatParticipants(_request, _tenant, _client, technician ?? _technician, status, T0);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Enviar201_YElOtroLoVeEnElHistorial()
    {
        Seed();
        using var client = Client(_client);
        using var technician = Client(_technician, "tecnico");

        using var sent = await client.PostAsJsonAsync(Route(), new { text = "¿A qué hora llega?" });
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        var message = await Json(sent);
        Assert.Equal(["id", "authorId", "authorRole", "text", "sentAt"], message.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("cliente", "¿A qué hora llega?"), (message.GetProperty("authorRole").GetString(), message.GetProperty("text").GetString()));

        using var history = await technician.GetAsync(Route(query: "?page=1&size=10"));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var page = await Json(history);
        Assert.Equal(["items", "page", "size", "totalItems", "writable"], page.EnumerateObject().Select(p => p.Name));
        Assert.True(page.GetProperty("writable").GetBoolean());
        Assert.Equal(message.GetProperty("id").GetGuid(), page.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task OtroUsuario_OtroTenant_YSolicitudDesconocida_404()
    {
        Seed();
        using var stranger = Client(Guid.NewGuid());
        using var otherTenant = Client(_client, tenant: Guid.NewGuid());
        using var owner = Client(_client);

        using var a = await stranger.GetAsync(Route());
        using var b = await otherTenant.PostAsJsonAsync(Route(), new { text = "hola" });
        using var c = await owner.GetAsync(Route(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, a.StatusCode);
        Assert.EndsWith("/no-encontrado", (await Json(a)).GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, b.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, c.StatusCode);
    }

    [Theory]
    [InlineData("completado", "chat-cerrado")]
    [InlineData("cancelado", "chat-cerrado")]
    [InlineData("en_espera", "chat-no-disponible")]
    public async Task Escribir_EnSoloLectura_409(string status, string type)
    {
        _factory.Store.Participants[_request] = new ChatParticipants(
            _request, _tenant, _client, status == "en_espera" ? null : _technician, status, T0);
        using var owner = Client(_client);

        using var response = await owner.PostAsJsonAsync(Route(), new { text = "hola" });
        using var history = await owner.GetAsync(Route());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.EndsWith($"/{type}", (await Json(response)).GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.False((await Json(history)).GetProperty("writable").GetBoolean());
    }

    [Theory]
    [InlineData("""{"text":""}""", "text")]
    [InlineData("""{"text":"hola","author":"x"}""", "body")]
    [InlineData("no es json", "body")]
    [InlineData("""{}""", "text")]
    public async Task CuerpoInvalido_400PorCampo(string body, string field)
    {
        Seed();
        using var owner = Client(_client);

        using var response = await owner.PostAsync(Route(), new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task PaginaInvalida_400()
    {
        Seed();
        using var owner = Client(_client);

        using var text = await owner.GetAsync(Route(query: "?page=uno"));
        using var big = await owner.GetAsync(Route(query: "?size=500"));

        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, big.StatusCode);
    }

    [Theory]
    [InlineData("admin_tenant")]
    [InlineData("admin_plataforma")]
    [InlineData("proveedor")]
    public async Task RolesSinChat_403(string role)
    {
        Seed();
        using var client = Client(_client, role);

        using var response = await client.GetAsync(Route());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SinToken_401()
    {
        using var anonymous = _factory.CreateClient();

        using var response = await anonymous.GetAsync(Route());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}