using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Core.Domain.Accounts;
using KrakenDeploy.Server.Core.Domain.Audit;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Spaces;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// P3-8 — live agent-transport end-to-end test for the SaaS multi-account
/// (host-derived) identity path. Drives the <b>real</b> SignalR hub pipeline
/// (<see cref="AgentAccountHubFilter"/> → <c>WithAccount</c> →
/// <see cref="AgentHub"/> → account-routed <see cref="KrakenDbContext"/> →
/// <see cref="IAgentConnectionRegistry"/>) over an in-memory
/// <see cref="TestServer"/>, with a real <see cref="HubConnection"/> client
/// connecting under different <c>Host</c> headers against two real tenant
/// databases.
/// <para>
/// What is REAL: the hub filter, the hub, the account-routing
/// <c>KrakenDbContext.OnConfiguring</c> seam (via the production
/// <c>AddKrakenDeployData(multiAccount: true)</c> data registration), the
/// in-memory registry, and the <c>AgentJwt</c> bearer scheme. What is
/// SUBSTITUTED (and why it is faithful): the transport is LongPolling rather
/// than WebSocket — the hub pipeline, auth, and filter run identically across
/// transports; the account resolver is a host→account stub rather than
/// <c>CatalogAccountResolver</c> — resolution-from-catalog is a separate unit,
/// and the E2E concern is "given host→account, does the agent path isolate";
/// the <see cref="IAccountContext"/> is an <c>AsyncLocal</c> test double
/// mirroring <c>HttpAccountContext</c>'s <c>WithAccount</c>/
/// <c>ResolveTenantConnectionString</c> contract (the production type lives in
/// the un-referenced <c>KrakenDeploy.Server</c> app project); and the JWT is
/// minted inline exactly as <c>AgentJwtService</c> does. The cross-account
/// <i>dispatch</i> guard's enforcement is unit-covered by
/// <c>AdhocDispatcherTests</c>; this test proves the connection-side input it
/// relies on (the host-derived account recorded on the registry).
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class MultiAccountAgentTransportE2ETests(MultiAccountAgentTransportFixture fixture)
    : IClassFixture<MultiAccountAgentTransportFixture>
{
    [Fact]
    public async Task Agent_on_its_own_subdomain_registers_and_routes_to_its_account_database()
    {
        var targetId = await fixture.Alpha.SeedTargetAsync();

        await using var host = await fixture.BuildHostAsync();
        var registry = host.Services.GetRequiredService<IAgentConnectionRegistry>();

        await using var connection = fixture.BuildConnection(host, fixture.Alpha, targetId);
        await connection.StartAsync();

        // The Status=Online write landing in ALPHA's database proves the hub's DbContext
        // was routed there by WithAccount → OnConfiguring (not the fallback connection).
        // Waiting on this — rather than on registry.Add, which runs earlier in the hub's
        // OnConnectedAsync — also guarantees the full connect path completed.
        (await WaitUntilTargetOnlineAsync(fixture.Alpha, targetId, TimeSpan.FromSeconds(15)))
            .Should().BeTrue("the hub resolves the account from the host and marks its target Online");

        // The host-derived account is recorded on the registry — this is exactly the
        // value the cross-account dispatch guard (DeploymentWorker / AdhocDispatcher)
        // compares the dispatch account against.
        registry.GetAccountForTarget(targetId).Should().Be(fixture.Alpha.AccountId);

        // CONNECTED (liveness) — the hub accepted the socket and tracked it.
        registry.HasConnectionFor(targetId).Should().BeTrue();

        // ...and therefore dispatchable, with nothing further required. The hub only Adds a
        // connection whose target resolved in this account, and the wire-contract version was
        // already verified on the handshake, so there is no second step and no window in
        // which a tracked connection is not yet eligible.
        registry.GetConnectionId(targetId).Should().NotBeNull(
            "the handshake contract gate ran before the connection was admitted, so a " +
            "tracked connection is a dispatchable one");
    }

    [Fact]
    public async Task Two_agents_on_different_subdomains_each_bind_to_their_own_account()
    {
        var alphaTarget = await fixture.Alpha.SeedTargetAsync();
        var betaTarget = await fixture.Beta.SeedTargetAsync();

        await using var host = await fixture.BuildHostAsync();
        var registry = host.Services.GetRequiredService<IAgentConnectionRegistry>();

        await using var alpha = fixture.BuildConnection(host, fixture.Alpha, alphaTarget);
        await using var beta = fixture.BuildConnection(host, fixture.Beta, betaTarget);
        await alpha.StartAsync();
        await beta.StartAsync();

        // Each Online write must land in the MATCHING tenant DB — proving the two
        // concurrent connections routed to their own account databases with no cross-talk.
        (await WaitUntilTargetOnlineAsync(fixture.Alpha, alphaTarget, TimeSpan.FromSeconds(15)))
            .Should().BeTrue("alpha's connection marks alpha's target Online in alpha's DB");
        (await WaitUntilTargetOnlineAsync(fixture.Beta, betaTarget, TimeSpan.FromSeconds(15)))
            .Should().BeTrue("beta's connection marks beta's target Online in beta's DB");

        // Each connection is pinned to its own host-derived account on the registry.
        registry.GetAccountForTarget(alphaTarget).Should().Be(fixture.Alpha.AccountId);
        registry.GetAccountForTarget(betaTarget).Should().Be(fixture.Beta.AccountId);
    }

    [Theory]
    [InlineData(true)]   // declares an older wire version
    [InlineData(false)]  // declares nothing at all
    public async Task Agent_with_a_skewed_contract_version_is_refused(bool declaresAVersion)
    {
        // The property that justifies deleting the registration gate: a version-skewed
        // agent never becomes a connection. Both shapes must be refused — an older
        // declared version AND an absent header, because an agent old enough to predate
        // the header is exactly the case that must not be read as compatible.
        //
        // Refusing on the handshake rather than in RegisterAsync is what makes "tracked"
        // mean "dispatchable". While the check lived in a hub method the server had to
        // admit a connection it could not yet trust, and a v2 agent reads v3's
        // AllowParallelTaskExecution = true as "skip the machine gate entirely" — so any
        // window at all meant an approved script could run with no lock while the server
        // believed the gate was honoured.
        var target = await fixture.Alpha.SeedTargetAsync();

        await using var host = await fixture.BuildHostAsync();
        var registry = host.Services.GetRequiredService<IAgentConnectionRegistry>();

        await using var connection = fixture.BuildConnection(
            host, fixture.Alpha, target,
            contract: declaresAVersion
                ? PresentedContract.Version(AgentContract.CurrentVersion - 1)
                : PresentedContract.Absent);

        await AssertConnectionRejectedAsync(
            connection, expectedHandshakeStatus: HttpStatusCode.UpgradeRequired);

        registry.HasConnectionFor(target).Should().BeFalse(
            "a skewed agent must never enter the registry — the refusal precedes OnConnectedAsync");
        registry.GetConnectionId(target).Should().BeNull();

        // And it never looked healthy: OnConnectedAsync is what writes Online, and it did
        // not run. The seeded target keeps the status it was created with.
        await using var db = fixture.Alpha.OpenContext();
        (await db.DeploymentTargets.IgnoreQueryFilters().FirstAsync(t => t.Id == target))
            .Status.Should().NotBe(TargetStatus.Online);

        // The audit row is asserted in AgentContractHandshakeGateTests instead, not here:
        // this fixture has no AccountResolutionMiddleware, so no tenant database is resolved
        // at gate time and the (best-effort) audit write cannot succeed. That is a property
        // of the fixture, not of production — AccountResolutionMiddleware whitelists
        // /hubs/agent, so the account is pinned from the host before the gate runs. Asserting
        // the row here would only ever have tested the fixture's wiring.
    }

    [Fact]
    public async Task Agent_presenting_a_foreign_accounts_target_id_is_rejected()
    {
        // Beta's target exists only in beta's DB. We connect to ALPHA's subdomain but
        // authenticate as beta's target id. Host-derived resolution pins the connection
        // to alpha, the hub looks beta's id up in ALPHA's DB, does not find it (the id is
        // globally unique and lives only in beta's DB), and aborts — fail closed.
        var betaTarget = await fixture.Beta.SeedTargetAsync();

        await using var host = await fixture.BuildHostAsync();
        var registry = host.Services.GetRequiredService<IAgentConnectionRegistry>();

        await using var connection = fixture.BuildConnection(host, fixture.Alpha, betaTarget);

        await AssertConnectionRejectedAsync(connection);

        // `Add` writes the target mapping AND the account side-table together, so if the hub
        // ever admitted a foreign account's target to the registry (adding before the account
        // check), these two catch it. HasConnectionFor and GetConnectionId now answer the same
        // question — the registration flag they used to differ over is gone with the
        // registration gate — so either is a real guard here; both are asserted because the
        // pair is what dispatch actually consults.
        registry.HasConnectionFor(betaTarget).Should().BeFalse(
            "a foreign account's target must never enter the registry at all");
        registry.GetConnectionId(betaTarget).Should().BeNull();
        registry.GetAccountForTarget(betaTarget).Should().BeNull();

        // Beta's own target was never touched (its agent never reached beta's account).
        await using (var betaDb = fixture.Beta.OpenContext())
        {
            (await betaDb.DeploymentTargets.IgnoreQueryFilters().FirstAsync(t => t.Id == betaTarget))
                .Status.Should().Be(TargetStatus.Offline);
        }
        // And alpha's DB never contained beta's target.
        await using (var alphaDb = fixture.Alpha.OpenContext())
        {
            (await alphaDb.DeploymentTargets.IgnoreQueryFilters().AnyAsync(t => t.Id == betaTarget))
                .Should().BeFalse();
        }
    }

    [Fact]
    public async Task Agent_connecting_to_an_unresolvable_host_is_rejected_before_the_hub_runs()
    {
        // A valid target token, but a host that resolves to no account. The filter
        // aborts before the hub runs (fail closed) — no tenant DB is ever opened.
        var alphaTarget = await fixture.Alpha.SeedTargetAsync();

        await using var host = await fixture.BuildHostAsync();
        var registry = host.Services.GetRequiredService<IAgentConnectionRegistry>();

        await using var connection = fixture.BuildConnection(
            host, account: fixture.Alpha, tokenTargetId: alphaTarget, hostOverride: "ghost.kraken.test");

        await AssertConnectionRejectedAsync(connection);

        registry.HasConnectionFor(alphaTarget).Should().BeFalse();
        await using var db = fixture.Alpha.OpenContext();
        (await db.DeploymentTargets.IgnoreQueryFilters().FirstAsync(t => t.Id == alphaTarget))
            .Status.Should().Be(TargetStatus.Offline, "the hub never ran, so nothing was marked online");
    }

    [Theory]
    // The real negotiate, unauthenticated: the hub endpoint's
    // [Authorize(AuthenticationSchemes = "AgentJwt")] must be enforced BEFORE the gate.
    [InlineData("/hubs/agent/negotiate", HttpStatusCode.Unauthorized)]
    // A sub-path that matches NO endpoint: routing has nothing to authorize and the gate
    // must not fire either. Under the old path-matched gate this reached the refusal branch
    // with whatever principal happened to be present, and wrote an audit row.
    [InlineData("/hubs/agent/x", HttpStatusCode.NotFound)]
    public async Task The_gate_is_unreachable_without_a_valid_agent_credential(
        string path, HttpStatusCode expected)
    {
        // Finding 4: the gate reads NameIdentifier off context.User with no scheme check, so
        // whether it can be reached by a non-agent principal is the whole question. Scoping
        // it to the hub ENDPOINT answers it structurally — the endpoint carries the authorize
        // metadata, so UseAuthorization short-circuits first, and a path that matches no
        // endpoint carries no marker so the gate never runs.
        await using var host = await fixture.BuildHostAsync();
        var server = (TestServer)host.Services.GetRequiredService<IServer>();
        using var client = server.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{fixture.Alpha.Host}{path}");
        // A skewed contract header, so a gate that DID run would answer 426 and this test
        // would fail with a concrete diagnosis rather than a vague one.
        request.Headers.Add(AgentContract.VersionHeader, "1");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(expected,
            "426 here would mean the wire-contract gate ran on a request that carried no " +
            "agent credential");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts the server rejects the connection: either <c>StartAsync</c> throws
    /// (refused during the handshake), or the connection is closed by the server shortly
    /// after connecting (aborted inside the hub's <c>OnConnectedAsync</c> or a hub filter).
    /// <para>
    /// Pass <paramref name="expectedHandshakeStatus"/> whenever the rejection is supposed
    /// to be a specific status. Without it this helper cannot tell one failure from another
    /// — a bare <c>try { … } catch { return; }</c> passes for a 500 and for a 401, and 401
    /// is the one that matters: it routes the agent's reconnect policy to the auth lane,
    /// whose operator instruction is "re-enroll this agent", which is the wrong action for
    /// a version skew and the right one for a revoked token.
    /// </para>
    /// </summary>
    private static async Task AssertConnectionRejectedAsync(
        HubConnection connection, HttpStatusCode? expectedHandshakeStatus = null)
    {
        var closed = new TaskCompletionSource();
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };

        try
        {
            await connection.StartAsync();
        }
        catch (Exception ex)
        {
            if (expectedHandshakeStatus is { } status)
            {
                ex.Should().BeOfType<HttpRequestException>(
                    "a handshake refusal must surface as an HTTP failure the agent can route on");
                ((HttpRequestException)ex).StatusCode.Should().Be(status);
            }
            return; // rejected during the handshake — that is the rejection.
        }

        expectedHandshakeStatus.Should().BeNull(
            "the refusal was supposed to happen during the handshake, but StartAsync succeeded");

        // Transport connected; the server must abort it from within the pipeline.
        var completed = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        completed.Should().Be(closed.Task, "the server must reject the connection, not leave it open");
    }

    /// <summary>
    /// Polls the account's tenant DB until the target reads <see cref="TargetStatus.Online"/>
    /// or the timeout elapses. Online means the hub's <c>OnConnectedAsync</c> ran fully
    /// (target resolved in the right account DB, registry recorded, Online committed) — so
    /// it is the race-free signal that the host-derived connect path completed.
    /// </summary>
    private static async Task<bool> WaitUntilTargetOnlineAsync(
        AccountInfo account, Guid targetId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            await using (var db = account.OpenContext())
            {
                var status = await db.DeploymentTargets.IgnoreQueryFilters()
                    .Where(t => t.Id == targetId)
                    .Select(t => (TargetStatus?)t.Status)
                    .FirstOrDefaultAsync();
                if (status == TargetStatus.Online) { return true; }
            }
            try { await Task.Delay(50, cts.Token); } catch (OperationCanceledException) { break; }
        }
        return false;
    }
}
