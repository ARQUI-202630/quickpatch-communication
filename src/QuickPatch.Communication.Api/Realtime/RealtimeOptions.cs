namespace QuickPatch.Communication.Api.Realtime;

/// <summary>Parámetros del canal (sección <c>Realtime</c>), según <c>asyncapi/realtime.v1.yaml</c>.</summary>
public sealed class RealtimeOptions
{
    public const string Section = "Realtime";

    /// <summary>Plazo para recibir la trama <c>auth</c> (contrato: 10 s; cierre 4002).</summary>
    public int AuthTimeoutSeconds { get; set; } = 10;

    /// <summary>Tamaño máximo de una trama de la app (contrato: 8 KB; cierre 1009).</summary>
    public int MaxFrameBytes { get; set; } = 8192;

    /// <summary>Intervalo de ping del servidor para que el gateway no corte la conexión inactiva.</summary>
    public int KeepAliveSeconds { get; set; } = 30;
}

/// <summary>Configuración del JWT (sección <c>Jwt</c>). Identity firma con RS256; aquí solo está la llave pública (ADR-018).</summary>
public sealed class JwtSettings
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "quickpatch-identity";

    public string Audience { get; set; } = "quickpatch";

    /// <summary>Llave pública RSA en PEM. Sin llave, ningún token es válido (falla cerrado).</summary>
    public string PublicKeyPem { get; set; } = string.Empty;
}

/// <summary>Códigos de cierre del contrato (<c>x-close-codes</c>).</summary>
public static class CloseCodes
{
    public const int Restart = 1001;
    public const int FrameTooBig = 1009;
    public const int InternalError = 1011;
    public const int InvalidFrame = 4000;
    public const int InvalidToken = 4001;
    public const int NoAuthentication = 4002;
    public const int RoleNotAllowed = 4003;
    public const int TokenExpired = 4004;
}