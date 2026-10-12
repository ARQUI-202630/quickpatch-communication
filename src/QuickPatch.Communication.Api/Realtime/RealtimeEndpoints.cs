using QuickPatch.Communication.Application.Realtime;

namespace QuickPatch.Communication.Api.Realtime;

public static class RealtimeEndpoints
{
    public const string Path = "/v1/realtime";

    public static IServiceCollection AddRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RealtimeOptions>(configuration.GetSection(RealtimeOptions.Section));
        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.Section));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TokenValidator>();
        services.AddSingleton<ConnectionRegistry>();
        services.AddSingleton<IRealtimeConnections>(sp => sp.GetRequiredService<ConnectionRegistry>());
        services.AddTransient<RealtimeSession>();
        return services;
    }

    public static WebApplication MapRealtime(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Configuration.GetSection(RealtimeOptions.Section).Get<RealtimeOptions>() ?? new RealtimeOptions();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(options.KeepAliveSeconds) });

        app.Map(Path, async (HttpContext context, RealtimeSession session) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    type: "https://quickpatch.internal/problems/validacion",
                    title: "Se esperaba una conexión WebSocket.");
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await session.RunAsync(socket, context.RequestAborted).ConfigureAwait(false);
            return Results.Empty;
        });

        var registry = app.Services.GetRequiredService<ConnectionRegistry>();
        app.Lifetime.ApplicationStopping.Register(() => registry.CloseAllAsync(CancellationToken.None).GetAwaiter().GetResult());
        return app;
    }
}