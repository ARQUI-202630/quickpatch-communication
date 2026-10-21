using Microsoft.AspNetCore.Diagnostics.HealthChecks;

using QuickPatch.Communication.Api.Chat;
using QuickPatch.Communication.Api.Http;
using QuickPatch.Communication.Api.Inbox;
using QuickPatch.Communication.Api.Realtime;
using QuickPatch.Communication.Application;
using QuickPatch.Communication.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = Problems.Customize);
builder.Services.AddHealthChecks();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddRealtime(builder.Configuration);
builder.Services.AddInboxApi(builder.Configuration);
builder.Services.AddChatApi();

var app = builder.Build();

app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// Probes de k3s (Documento de Infraestructura, sección 5.8).
// live: el proceso responde; ready: además, PostgreSQL responde (si el buzón tiene base configurada).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

// Canal de estado en tiempo real (ADR-023; contrato asyncapi/realtime.v1.yaml).
app.MapRealtime();

// Buzón de notificaciones (ADR-024; contrato openapi/communication.v1.yaml 1.0.0).
app.MapInbox();

// Chat de la solicitud (ADR-026; contrato openapi/communication.v1.yaml 1.3.0).
app.MapChat();

app.Run();

/// <summary>Punto de entrada; público para las pruebas con WebApplicationFactory.</summary>
public partial class Program;