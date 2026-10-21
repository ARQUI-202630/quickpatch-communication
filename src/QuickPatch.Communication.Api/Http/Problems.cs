namespace QuickPatch.Communication.Api.Http;

/// <summary>Tipos de error del contrato (<c>application/problem+json</c>, RFC 9457).</summary>
public static class Problems
{
    private const string Base = "https://quickpatch.internal/problems/";

    public const string Validation = Base + "validacion";
    public const string NotFound = Base + "no-encontrado";
    public const string Unauthorized = Base + "no-autenticado";
    public const string Forbidden = Base + "no-autorizado";

    /// <summary>La solicitud se completó, se pagó o se canceló: el chat es de solo lectura (RN-CH2, contrato 1.3.0).</summary>
    public const string ChatClosed = Base + "chat-cerrado";

    /// <summary>La solicitud no tiene técnico asignado en este momento (RN-CH2, contrato 1.3.0).</summary>
    public const string ChatNotAvailable = Base + "chat-no-disponible";

    /// <summary>Agrega <c>correlationId</c> y el tipo según el código a todo Problem Details que produzca el servicio.</summary>
    public static void Customize(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problem = context.ProblemDetails;
        problem.Extensions["correlationId"] = CorrelationId.Get(context.HttpContext);
        problem.Type = problem.Status switch
        {
            StatusCodes.Status400BadRequest when problem.Type is null or "https://tools.ietf.org/html/rfc9110#section-15.5.1" => Validation,
            StatusCodes.Status401Unauthorized => Unauthorized,
            StatusCodes.Status403Forbidden => Forbidden,
            StatusCodes.Status404NotFound when problem.Type is null or "https://tools.ietf.org/html/rfc9110#section-15.5.5" => NotFound,
            _ => problem.Type,
        };
    }
}