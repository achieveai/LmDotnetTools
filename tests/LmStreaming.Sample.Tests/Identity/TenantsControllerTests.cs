using System.Net;
using System.Net.Http.Json;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LmStreaming.Sample.Tests.Identity;

/// <summary>
/// An in-memory tenant registry that records what it was asked to create, so a test can assert
/// that a refused request reached no store at all.
/// </summary>
internal sealed class RecordingTenantStore : ITenantStore
{
    public List<TenantRecord> Provisioned { get; } = [];

    public TenantProvisionOutcome NextOutcome { get; set; } = TenantProvisionOutcome.Created;

    public Task<TenantRecord?> FindByEntraTenantIdAsync(string entraTenantId, CancellationToken ct = default) =>
        Task.FromResult(Provisioned.Find(t => t.EntraTenantId == entraTenantId));

    public Task<TenantRecord?> FindByTenantIdAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(Provisioned.Find(t => t.TenantId == tenantId));

    public Task<TenantProvisionOutcome> ProvisionAsync(
        TenantRecord tenant,
        string firstAdminUpn,
        CancellationToken ct = default)
    {
        if (NextOutcome == TenantProvisionOutcome.Created)
        {
            Provisioned.Add(tenant);
        }

        return Task.FromResult(NextOutcome);
    }

    public Task<bool> TryBindFirstAdminAsync(
        string tenantId,
        string upn,
        string userId,
        DateTimeOffset boundAt,
        CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> IsTenantAdminAsync(string tenantId, string userId, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<EntraTenantNormalizationResult> NormalizeEntraTenantIdsAsync(CancellationToken ct = default) =>
        Task.FromResult(default(EntraTenantNormalizationResult));

    /// <summary>
    /// Whether the configured quarantine tenant id is free. False models the collision the startup
    /// repair refuses to write through - an id that already names a real customer.
    /// </summary>
    public bool QuarantineAvailable { get; set; } = true;

    public Task<bool> TryEnsureQuarantineTenantAsync(
        string tenantId,
        DateTimeOffset createdAt,
        CancellationToken ct = default) =>
        Task.FromResult(QuarantineAvailable);
}

/// <summary>
/// Pins the operator-secret guard on <c>POST /api/admin/tenants</c> against the acceptance clauses
/// of issue #301.
/// </summary>
/// <remarks>
/// <para>
/// The guard is an <c>IAsyncActionFilter</c>, so it only exists as behaviour once MVC has selected
/// an action. These tests therefore run the real controller behind real routing rather than
/// invoking the filter directly - invoking it directly would prove the filter refuses, not that
/// the route is actually guarded by it.
/// </para>
/// <para>
/// The two clauses that matter are both about the ABSENCE of a success. A guard that can be
/// bypassed by omitting a header, and a guard that switches itself off when its secret is unset,
/// are the same bug wearing different clothes: an unauthenticated caller who can create tenants
/// has defeated the "tenants are explicitly provisioned" rule entirely.
/// </para>
/// </remarks>
public sealed class TenantsControllerTests
{
    private const string Secret = "operator-secret-value";

    private readonly RecordingTenantStore _store = new();
    private readonly RecordingAuditSink _audit = new();

    /// <summary>Boots a host exposing the real controller, with or without the operator secret configured.</summary>
    /// <param name="secret">The configured operator secret, or null to leave it unconfigured.</param>
    private async Task<TestServer> StartAsync(string? secret)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureAppConfiguration(config =>
                {
                    if (secret is not null)
                    {
                        _ = config.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                [OperatorSecretAuthAttribute.SecretConfigKey] = secret,
                            });
                    }
                })
                .ConfigureServices(services =>
                {
                    _ = services.AddSingleton<ITenantStore>(_store);
                    _ = services.AddSingleton<IAuditSink>(_audit);
                    _ = services.AddSingleton(TimeProvider.System);

                    // Added when TenantsController gained the adopt-legacy route (#302). These
                    // tests are about the operator-secret guard and are unchanged by it; the
                    // registrations exist only so the controller can be constructed.
                    _ = services.AddSingleton<IConversationStore>(new InMemoryConversationStore());
                    _ = services.Configure<IdentityOptions>(_ => { });
                    _ = services.AddControllers()
                        .AddApplicationPart(typeof(TenantsController).Assembly);
                })
                .Configure(app =>
                {
                    _ = app.UseRouting();
                    _ = app.UseEndpoints(endpoints => endpoints.MapControllers());
                }))
            .StartAsync();

        return host.GetTestServer();
    }

    private static ProvisionTenantRequest ValidRequest() =>
        new()
        {
            TenantId = "tnt_acme",
            EntraTenantId = "11111111-1111-1111-1111-111111111111",
            DisplayName = "Acme Corp",
            FirstAdminUpn = "ada@acme.example",
        };

    private static Task<HttpResponseMessage> PostAsync(TestServer server, string? presentedSecret) =>
        PostBodyAsync(server, presentedSecret, ValidRequest());

    private static Task<HttpResponseMessage> PostBodyAsync<TBody>(
        TestServer server,
        string? presentedSecret,
        TBody body)
    {
        var client = server.CreateClient();
        if (presentedSecret is not null)
        {
            client.DefaultRequestHeaders.Add(OperatorSecretAuthAttribute.HeaderName, presentedSecret);
        }

        return client.PostAsJsonAsync(new Uri("/api/admin/tenants", UriKind.Relative), body);
    }

    /// <summary>
    /// A body that binds but fails every <c>[Required]</c> clause on
    /// <see cref="ProvisionTenantRequest"/>, so model validation has something to reject.
    /// </summary>
    private static object InvalidRequest() => new { };

    [Fact]
    public async Task WithoutTheOperatorSecretHeader_TheRequestIsRefused_AndNoTenantIsCreated()
    {
        using var server = await StartAsync(Secret);

        var response = await PostAsync(server, presentedSecret: null);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The refusal has to reach the store as a non-event. A 401 whose body was written after the
        // row was inserted would satisfy the status assertion and still have created the tenant.
        _ = _store.Provisioned.Should().BeEmpty();
    }

    [Fact]
    public async Task WithAWrongOperatorSecret_TheRequestIsRefused_AndNoTenantIsCreated()
    {
        using var server = await StartAsync(Secret);

        var response = await PostAsync(server, presentedSecret: "not-the-secret");

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = _store.Provisioned.Should().BeEmpty();
    }

    [Fact]
    public async Task WithTheOperatorSecretUnconfigured_TheRouteIsUnavailable_AndNeverSucceeds()
    {
        using var server = await StartAsync(secret: null);

        // Presenting a header cannot help: there is nothing to compare it against. The guard fails
        // CLOSED rather than borrowing the S2S guard's keyless-dev behaviour, whose failure mode
        // here would be a world-writable tenant registry.
        foreach (var presented in new[] { null, "anything", "" })
        {
            var response = await PostAsync(server, presented);

            _ = response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            _ = _store.Provisioned.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task WithTheCorrectOperatorSecret_TheTenantIsCreatedAndAudited()
    {
        using var server = await StartAsync(Secret);

        var response = await PostAsync(server, Secret);

        // The positive case is asserted for the same reason the guard exists: a test suite that
        // only proves refusals passes just as well against a route that refuses everything.
        _ = response.StatusCode.Should().Be(HttpStatusCode.Created);
        _ = _store.Provisioned.Should().ContainSingle().Which.TenantId.Should().Be("tnt_acme");

        var record = _audit.Administrations.Should().ContainSingle().Subject;
        _ = record.Operation.Should().Be("tenant.provision");
        _ = record.Outcome.Should().Be(AdministrationOutcome.Applied);
        _ = record.EventClass.Should().Be(AuditEventClass.Security);
    }

    [Fact]
    public async Task ADuplicateTenant_IsReportedAsAConflictRatherThanMerged()
    {
        using var server = await StartAsync(Secret);
        _store.NextOutcome = TenantProvisionOutcome.EntraTenantIdClaimed;

        var response = await PostAsync(server, Secret);

        // Repointing an existing id at another directory, or pointing two ids at one directory, are
        // both silent cross-tenant data leaks. An operator who means to change a mapping has to say
        // so explicitly.
        _ = response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _ = _store.Provisioned.Should().BeEmpty();
        _ = _audit.Administrations.Should().ContainSingle()
            .Which.Outcome.Should().Be(AdministrationOutcome.Rejected);
    }

    [Fact]
    public async Task WithAnInvalidBodyAndNoOperatorSecret_TheGuardAnswersFirst_WithA401()
    {
        using var server = await StartAsync(Secret);

        // [ApiController] installs model-state validation at Order = -2000. An unordered attribute
        // filter runs at Order = 0, so a guard that does not declare an order is answered over:
        // the caller learns the route exists and learns its schema, and reaches the JSON
        // deserializer, without ever presenting a secret. Every other test here sends a WELL-FORMED
        // body, which takes the one path where the ordering does not matter.
        var response = await PostBodyAsync(server, presentedSecret: null, InvalidRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = _store.Provisioned.Should().BeEmpty();
    }

    [Fact]
    public async Task WithAnInvalidBodyAndTheOperatorSecretUnconfigured_TheRouteIsStillUnavailable()
    {
        using var server = await StartAsync(secret: null);

        // The fail-closed 503 has to outrank model validation for the same reason the 401 does: a
        // 400 here would answer a caller that the route is one an operator forgot to configure.
        var response = await PostBodyAsync(server, presentedSecret: null, InvalidRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _ = _store.Provisioned.Should().BeEmpty();
    }

    [Fact]
    public async Task WithTheCorrectOperatorSecret_AnInvalidBodyStillGetsIts400()
    {
        using var server = await StartAsync(Secret);

        // Ordering the guard first must not suppress validation, only defer it. An authenticated
        // operator sending nonsense still gets the model-state 400 they need to fix their request.
        var response = await PostBodyAsync(server, Secret, InvalidRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = _store.Provisioned.Should().BeEmpty();
    }
}

