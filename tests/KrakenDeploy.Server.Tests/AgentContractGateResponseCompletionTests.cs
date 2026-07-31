using System.Net;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// The 426 must reach the client BEFORE the tenant-database recording runs, over a real
/// Kestrel — which a <c>DefaultHttpContext</c> cannot show, because it has no response
/// completion semantics at all.
/// <para>
/// This exists because the "answer FIRST, record after" reordering did not work.
/// <c>WriteAsync</c> only writes into the body; with no <c>ContentLength</c> the response is
/// chunked and is not finished until the pipeline returns, so a client reading to completion —
/// which <c>HttpConnection.NegotiateAsync</c> does — stayed blocked for the whole duration of
/// the recording. Measured before the fix: 3 s of recorder work delayed the client by 3056 ms.
/// That latency is what turned a diagnosable 426 into a client-side TIMEOUT on a slow tenant
/// database, and a timeout is not an <c>HttpRequestException</c> with status 426, so the agent
/// could not classify it and never opened its self-upgrade escape hatch.
/// </para>
/// <para>
/// Deterministic rather than timing-based: the recorder blocks on a semaphore that the test
/// releases only AFTER asserting the client already has its 426.
/// </para>
/// </summary>
public sealed class AgentContractGateResponseCompletionTests
{
    [Fact]
    public async Task The_426_reaches_the_client_before_the_recorder_finishes()
    {
        using var recorderEntered = new SemaphoreSlim(0);
        using var releaseRecorder = new SemaphoreSlim(0);
        var recorder = new BlockingRecorder(recorderEntered, releaseRecorder);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAgentContractRefusalRecorder>(recorder);
        var app = builder.Build();

        // Stand in for UseAuthentication/UseAuthorization: the gate only records against a
        // target it can name, so without an agent principal it would refuse without ever
        // reaching the recorder and this test would pass vacuously.
        var targetId = Guid.NewGuid();
        app.Use(async (ctx, nextMw) =>
        {
            ctx.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(
                        System.Security.Claims.ClaimTypes.NameIdentifier, targetId.ToString())],
                    "AgentJwt"));
            await nextMw(ctx).ConfigureAwait(false);
        });

        app.UseAgentContractGate();
        // A trivial endpoint carrying the marker: this test is about the gate's response
        // handling, not about the hub, so nothing here needs SignalR or a database.
        app.MapGet("/hubs/agent/negotiate", () => "reached the hub")
            .WithMetadata(new RequiresAgentContract());
        await app.StartAsync();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var url = $"{app.Urls.First()}/hubs/agent/negotiate";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(AgentContract.VersionHeader, "1");

            // Default completion option is ResponseContentRead — the same "read the whole body"
            // behaviour the SignalR negotiate uses, which is precisely what made the missing
            // CompleteAsync observable.
            var send = http.SendAsync(request);

            // The recorder must have been entered (so the gate did reach it) and must still be
            // blocked when the client's response is already complete.
            (await recorderEntered.WaitAsync(TimeSpan.FromSeconds(10)))
                .Should().BeTrue("the gate must invoke the recorder");

            var completed = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().BeSameAs(send,
                "the 426 must be complete on the wire while the recorder is still running — " +
                "otherwise a slow tenant database turns the refusal into a client timeout");

            using var response = await send;
            response.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired);
            (await response.Content.ReadAsStringAsync())
                .Should().Contain($"v{AgentContract.CurrentVersion}");

            recorder.Completed.Should().BeFalse(
                "the assertion above is only meaningful while the recorder has not returned");
        }
        finally
        {
            releaseRecorder.Release();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await app.StopAsync(cts.Token); } catch (OperationCanceledException) { }
            await app.DisposeAsync();
        }
    }

    private sealed class BlockingRecorder(SemaphoreSlim entered, SemaphoreSlim release)
        : IAgentContractRefusalRecorder
    {
        internal bool Completed { get; private set; }

        public async Task RecordAsync(
            Guid targetId, string presentedContract, CancellationToken ct = default)
        {
            entered.Release();
            await release.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            Completed = true;
        }
    }
}
