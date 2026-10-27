using System.Globalization;
using System.Security.Claims;
using System.Text.Json;

using QuickPatch.Communication.Api.Http;
using QuickPatch.Communication.Application.Abstractions;
using QuickPatch.Communication.Application.Chat;
using QuickPatch.Communication.Domain.Chat;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Api.Endpoints;

/// <summary>Esquema <c>ChatMessage</c> de <c>communication.v1.yaml</c> 1.3.0.</summary>
public sealed record ChatMessageResponse(Guid Id, Guid AuthorId, string AuthorRole, string Text, DateTimeOffset SentAt)
{
    public static ChatMessageResponse From(ChatMessage m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return new(m.Id, m.AuthorId, m.AuthorRole, m.Body, m.SentAt);
    }
}

/// <summary>Esquema <c>ChatMessagePage</c> (1.3.0).</summary>
public sealed record ChatMessagePageResponse(IReadOnlyList<ChatMessageResponse> Items, int Page, int Size, int TotalItems, bool Writable);

/// <summary>
/// Chat de la solicitud (<c>communication.v1.yaml</c> 1.3.0, ADR-026; excepción de ADR-025). El usuario y el tenant salen solo
/// de los claims <c>sub</c> y <c>tenant_id</c> del JWT (RN-U3); el papel en el chat, de la réplica de la solicitud. El texto de
/// los mensajes no se escribe en los logs.
/// </summary>
public static class ChatEndpoints
{
    public const string Route = "/v1/service-requests/{id:guid}/messages";
    public const string ChatPolicy = "ConChat";

    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Los roles que pueden participar: cliente, empresa_contacto (dueño) y técnico. Los demás, 403.</summary>
    public static IServiceCollection AddChatApi(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder().AddPolicy(ChatPolicy, policy => policy.RequireRole(
            Roles.Cliente, Roles.EmpresaContacto, Roles.Tecnico));
        return services;
    }

    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Route).RequireAuthorization(ChatPolicy);
        group.MapGet("/", ListAsync).WithName("listServiceRequestMessages");
        group.MapPost("/", SendAsync).WithName("sendServiceRequestMessage");
        return app;
    }

    private static async Task<IResult> ListAsync(
        Guid id, HttpContext context, ListChatMessagesHandler handler, string? page, string? size, CancellationToken cancellationToken)
    {
        if (!TryGetUser(context.User, out var userId, out var tenantId))
        {
            return Unauthenticated();
        }

        var errors = new Dictionary<string, string[]>();
        var pageNumber = ParseOptionalInt(page, "page", errors);
        var pageSize = ParseOptionalInt(size, "size", errors);
        if (errors.Count > 0)
        {
            return ValidationProblem(errors);
        }

        try
        {
            return await handler.HandleAsync(tenantId, userId, id, pageNumber, pageSize, cancellationToken).ConfigureAwait(false) switch
            {
                ChatResult.History h => Results.Ok(new ChatMessagePageResponse(
                    h.Items.Select(ChatMessageResponse.From).ToList(), h.Page, h.Size, h.TotalItems, h.Writable)),
                _ => NotFound(),
            };
        }
        catch (ChatValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
        catch (ChatUnavailableException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> SendAsync(
        Guid id, HttpContext context, SendChatMessageHandler handler, CancellationToken cancellationToken)
    {
        if (!TryGetUser(context.User, out var userId, out var tenantId))
        {
            return Unauthenticated();
        }

        string? text;
        try
        {
            var body = await JsonSerializer.DeserializeAsync<SendMessageRequest>(context.Request.Body, BodyJson, cancellationToken)
                .ConfigureAwait(false);
            text = body?.Text;
        }
        catch (JsonException)
        {
            // El detalle de la excepción puede citar el texto del mensaje: no se registra ni se devuelve.
            return ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["El cuerpo debe ser { \"text\": \"...\" }."] });
        }

        try
        {
            return await handler.HandleAsync(tenantId, userId, id, text, cancellationToken).ConfigureAwait(false) switch
            {
                ChatResult.Sent s => Results.Json(ChatMessageResponse.From(s.Message), statusCode: StatusCodes.Status201Created),
                ChatResult.Closed => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    type: Problems.ChatClosed,
                    title: "El chat de esta solicitud es de solo lectura",
                    detail: "La solicitud se completó, se pagó o se canceló."),
                ChatResult.NoTechnician => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    type: Problems.ChatNotAvailable,
                    title: "La solicitud no tiene técnico asignado",
                    detail: "El chat se habilita cuando un técnico acepta la solicitud."),
                _ => NotFound(),
            };
        }
        catch (ChatValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
        catch (ChatUnavailableException)
        {
            return Unavailable();
        }
    }

    private sealed record SendMessageRequest(string? Text);

    private static bool TryGetUser(ClaimsPrincipal principal, out Guid userId, out Guid tenantId)
    {
        tenantId = Guid.Empty;
        return Guid.TryParse(principal.FindFirstValue("sub"), out userId) && userId != Guid.Empty
            && Guid.TryParse(principal.FindFirstValue("tenant_id"), out tenantId) && tenantId != Guid.Empty;
    }

    private static int? ParseOptionalInt(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        errors[field] = ["Debe ser un número entero."];
        return null;
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "La solicitud no tiene chat para este usuario.");

    private static IResult Unauthenticated() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "El chat no está disponible.");

    private static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(e => e.Key, e => e.Value),
            title: "La solicitud tiene datos inválidos",
            type: Problems.Validation);
}