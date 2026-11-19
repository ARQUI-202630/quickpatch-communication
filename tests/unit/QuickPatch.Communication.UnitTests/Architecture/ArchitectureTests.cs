using System.Reflection;

using NetArchTest.Rules;

using QuickPatch.Communication.Application.Inbox;
using QuickPatch.Communication.Domain.Inbox;
using QuickPatch.Communication.Infrastructure.Persistence;

namespace QuickPatch.Communication.UnitTests.Architecture;

/// <summary>
/// Reglas de dependencia entre capas del SDD 10.3.2 (SCRUM-648), las mismas que en los otros servicios .NET (SCRUM-656).
/// Revisan lo que el compilador no ve: el API referencia a Infrastructure para la composición, pero solo <c>Program</c>
/// puede usarla.
/// </summary>
public sealed class ArchitectureTests
{
    private const string ApiNs = "QuickPatch.Communication.Api";
    private const string ApplicationNs = "QuickPatch.Communication.Application";
    private const string InfrastructureNs = "QuickPatch.Communication.Infrastructure";

    private static readonly string[] Tecnologia =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Confluent.Kafka",
        "Microsoft.AspNetCore",
        "Microsoft.CommunicationModel",
        "BCrypt",
    ];

    private static readonly Assembly Domain = typeof(Notification).Assembly;
    private static readonly Assembly Application = typeof(InboxRecorder).Assembly;
    private static readonly Assembly Infrastructure = typeof(CommunicationDbContext).Assembly;
    private static readonly Assembly Api = Assembly.Load("QuickPatch.Communication.Api");

    [Fact]
    public void Domain_NoDependeDeOtrasCapasNiDeTecnologia() =>
        AssertOk(Types.InAssembly(Domain).ShouldNot()
            .HaveDependencyOnAny([ApplicationNs, InfrastructureNs, ApiNs, .. Tecnologia]).GetResult());

    [Fact]
    public void Application_NoDependeDeInfrastructureApiNiTecnologia() =>
        AssertOk(Types.InAssembly(Application).ShouldNot()
            .HaveDependencyOnAny([InfrastructureNs, ApiNs, .. Tecnologia]).GetResult());

    [Fact]
    public void Infrastructure_NoDependeDelApi() =>
        AssertOk(Types.InAssembly(Infrastructure).ShouldNot()
            .HaveDependencyOnAny(ApiNs, "Microsoft.AspNetCore").GetResult());

    [Fact]
    public void Api_SoloLaRaizDeComposicionConoceInfrastructure() =>
        AssertOk(Types.InAssembly(Api).That().DoNotHaveName("Program")
            .ShouldNot().HaveDependencyOnAny(InfrastructureNs).GetResult());

    [Fact]
    public void Endpoints_NoUsanLosStores() =>
        AssertOk(Types.InAssembly(Api).That().ResideInNamespace(ApiNs + ".Endpoints")
            .ShouldNot().HaveDependencyOnAny(ApplicationNs + ".Abstractions.IChatStore", ApplicationNs + ".Abstractions.IInboxStore")
            .GetResult());

    [Fact]
    public void Realtime_LaConexionNoDependeDeLaSesion() =>
        AssertOk(Types.InAssembly(Api).That().HaveName("WebSocketClient")
            .ShouldNot().HaveDependencyOnAny(ApiNs + ".Realtime.RealtimeSession").GetResult());

    private static void AssertOk(TestResult result) =>
        Assert.True(
            result.IsSuccessful,
            "Viola SDD 10.3.2: " + string.Join(", ", result.FailingTypes?.Select(t => t.FullName) ?? []));
}