using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

using QuickPatch.Communication.Api.Http;
using QuickPatch.Communication.Api.Realtime;
using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Domain.Inbox;
using QuickPatch.Communication.Domain.Realtime;

namespace QuickPatch.Communication.Api.Inbox;

/// <summary>Esquema <c>Notification</c> de <c>communication.v1.yaml</c> 1.0.0.</summary>
public sealed record NotificationResponse(
    Guid Id, string Type, string Title, string Message, Guid? ServiceRequestId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt)
{
    public static NotificationResponse From(Notification n) =>
        new(n.Id, n.Type, n.Title, n.Message, n.ServiceRequestId, n.CreatedAt, n.ReadAt);
}

/// <summary>Esquema <c>NotificationPage</c> (1.0.0).</summary>
public sealed record NotificationPageResponse(IReadOnlyList<NotificationResponse> Items, int Page, int Size, int TotalItems, int UnreadCount);

/// <summary>
/// Buzón de notificaciones (<c>communication.v1.yaml</c> 1.0.0, ADR-024). El usuario y el tenant salen solo de los claims
/// <c>sub</c> y <c>tenant_id</c> del JWT (RN-U3); cada usuario ve solo sus notificaciones.
/// </summary>
public static class InboxEndpoints
{
    public const string BasePath = "/v1/notifications";
    public const string InboxPolicy = "ConBuzon";

    public static IServiceCollection AddInboxApi(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(JwtSettings.Section).Get<JwtSettings>() ?? new JwtSettings();
        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = settings.Issuer,
                    ValidAudience = settings.Audience,
                    IssuerSigningKey = LoadPublicKey(settings.PublicKeyPem),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.Zero,
                };
            });

        // Los mismos roles que usan el canal en tiempo real: cliente, empresa_contacto, técnico y admin_tenant.
        services.AddAuthorizationBuilder().AddPolicy(InboxPolicy, policy => policy.RequireRole(
            Roles.Cliente, Roles.EmpresaContacto, Roles.Tecnico, Roles.AdminTenant));
        return services;
    }

    public static IEndpointRouteBuilder MapInbox(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(BasePath).RequireAuthorization(InboxPolicy);
        group.MapGet("/", ListAsync).WithName("listNotifications");
        group.MapPost("/{id:guid}/read", MarkReadAsync).WithName("markNotificationRead");
        return app;
    }

    private static async Task<IResult> ListAsync(
        HttpContext context, ListNotificationsHandler handler, string? unread, string? page, string? size, CancellationToken cancellationToken)
    {
        if (!TryGetUser(context.User, out var userId, out var tenantId))
        {
            return Unauthenticated();
        }

        // Llegan como texto para responder 400 con el campo, y no el 400 vacío del enlazado de tipos.
        var errors = new Dictionary<string, string[]>();
        var unreadOnly = false;
        if (unread is not null && !bool.TryParse(unread, out unreadOnly))
        {
            errors["unread"] = ["Debe ser true o false."];
        }

        var pageNumber = ParseOptionalInt(page, "page", errors);
        var pageSize = ParseOptionalInt(size, "size", errors);
        if (errors.Count > 0)
        {
            return ValidationProblem(errors);
        }

        try
        {
            var result = await handler.HandleAsync(
                new ListNotificationsQuery(tenantId, userId, unreadOnly, pageNumber, pageSize), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new NotificationPageResponse(
                result.Items.Select(NotificationResponse.From).ToList(), result.Page, result.Size, result.TotalItems, result.UnreadCount));
        }
        catch (InboxValidationException ex)
        {
            return ValidationProblem(ex.Errors);
        }
        catch (InboxUnavailableException)
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> MarkReadAsync(
        Guid id, HttpContext context, MarkNotificationReadHandler handler, CancellationToken cancellationToken)
    {
        if (!TryGetUser(context.User, out var userId, out var tenantId))
        {
            return Unauthenticated();
        }

        try
        {
            var notification = await handler.HandleAsync(tenantId, userId, id, cancellationToken).ConfigureAwait(false);
            return notification is null
                ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "La notificación no existe.")
                : Results.Ok(NotificationResponse.From(notification));
        }
        catch (InboxUnavailableException)
        {
            return Unavailable();
        }
    }

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

    private static IResult Unauthenticated() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "El token no trae usuario ni tenant válidos.");

    private static IResult Unavailable() =>
        Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "El buzón no está disponible.");

    private static IResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(e => e.Key, e => e.Value),
            title: "La solicitud tiene datos inválidos",
            type: Problems.Validation);

    private static RsaSecurityKey? LoadPublicKey(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            // Sin llave no se valida ningún token: el buzón responde 401 (falla cerrado), igual que el canal.
            return null;
        }

#pragma warning disable CA2000 // La llave vive lo que vive la aplicación.
        var rsa = RSA.Create();
#pragma warning restore CA2000
        rsa.ImportFromPem(pem);
        return new RsaSecurityKey(rsa);
    }
}