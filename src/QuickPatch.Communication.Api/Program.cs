using Microsoft.AspNetCore.Diagnostics.HealthChecks;

using QuickPatch.Communication.Api.Realtime;
using QuickPatch.Communication.Application;
using QuickPatch.Communication.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddRealtime(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Probes de k3s (Documento de Infraestructura, sección 5.8).
// live: el proceso responde; ready: además, sus dependencias (se agregan con cada integración).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

// Canal de estado en tiempo real (ADR-023; contrato asyncapi/realtime.v1.yaml).
app.MapRealtime();

app.Run();

/// <summary>Punto de entrada; público para las pruebas con WebApplicationFactory.</summary>
public partial class Program;