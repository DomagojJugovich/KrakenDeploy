using System.IO.Compression;
using System.Text;
using FluentAssertions;
using KrakenDeploy.Ai;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Packages;
using KrakenDeploy.Server.Data.Services.Ai.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// M11.D — tests for the process-builder assistant backend with a fake
/// <see cref="IKrakenAi"/> + a fake package store. Pins: the suggester reads
/// the package layout into the prompt and returns parsed suggestions; field
/// explanations return text; unknown package → null; AI-unavailable
/// propagates for the UI to surface.
/// </summary>
[Collection("Postgres")]
public sealed class ProcessAssistantServiceTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = postgres.CreateContext();
        await db.Packages.IgnoreQueryFilters().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SuggestSteps_reads_package_layout_into_prompt_and_returns_suggestions()
    {
        await SeedPackageAsync("Argosy.Web", "1.0.0", "stored/argosy.zip");
        var zip = MakeZip("src/Argosy.Web.csproj", "wwwroot/index.html", "web.config");
        var ai = new FakeKrakenAi(new StepSuggestionResult
        {
            OverallRationale = "ASP.NET site → IIS deploy.",
            Steps = [new SuggestedStep { Name = "Deploy IIS site", StepType = "Kraken.IIS", Rationale = "web.config present" }],
        });
        var svc = new ProcessAssistantService(
            postgres, new FakePackageStore("stored/argosy.zip", zip), ai,
            NullLogger<ProcessAssistantService>.Instance);

        var result = await svc.SuggestStepsAsync("Argosy.Web", "1.0.0");

        result.Should().NotBeNull();
        result!.Steps.Should().ContainSingle().Which.StepType.Should().Be("Kraken.IIS");

        // The package layout reached the prompt — the model saw the signals.
        ai.LastUserText.Should().Contain("web.config").And.Contain(".csproj").And.Contain("wwwroot/");
    }

    [Fact]
    public async Task SuggestSteps_returns_null_for_unknown_package()
    {
        var svc = new ProcessAssistantService(
            postgres, new FakePackageStore("x", []), new FakeKrakenAi(new StepSuggestionResult()),
            NullLogger<ProcessAssistantService>.Instance);

        (await svc.SuggestStepsAsync("nope", "1.0.0")).Should().BeNull();
    }

    [Fact]
    public async Task SuggestSteps_propagates_ai_disabled_for_the_ui_to_surface()
    {
        await SeedPackageAsync("P", "1.0", "stored/p.zip");
        var svc = new ProcessAssistantService(
            postgres, new FakePackageStore("stored/p.zip", MakeZip("a.txt")),
            new FakeKrakenAi(disabled: true), NullLogger<ProcessAssistantService>.Instance);

        var act = async () => await svc.SuggestStepsAsync("P", "1.0");
        await act.Should().ThrowAsync<KrakenAiDisabledException>();
    }

    [Fact]
    public async Task ExplainField_returns_text()
    {
        var svc = new ProcessAssistantService(
            postgres, new FakePackageStore("x", []),
            new FakeKrakenAi(explainText: "This sets the IIS site name; e.g. 'Argosy'."),
            NullLogger<ProcessAssistantService>.Instance);

        var text = await svc.ExplainFieldAsync(
            "Kraken.IIS", "Kraken.IIS.SiteName", "Site name", "The IIS site to create.");

        text.Should().Contain("site name");
    }

    // ── Fakes + helpers ──────────────────────────────────────────────────

    private async Task SeedPackageAsync(string packageId, string version, string storedPath)
    {
        await using var db = postgres.CreateContext();
        db.Packages.Add(new Package
        {
            SpaceId   = WellKnown.DefaultSpaceId,
            PackageId = packageId,
            Version   = version,
            FileName  = $"{packageId}.{version}.zip",
            StoredPath = storedPath,
        });
        await db.SaveChangesAsync();
    }

    private static byte[] MakeZip(params string[] entryPaths)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in entryPaths)
            {
                var entry = archive.CreateEntry(path);
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write("x");
            }
        }
        return ms.ToArray();
    }

    private sealed class FakePackageStore(string expectedPath, byte[] zipBytes) : IPackageStore
    {
        public Task<Stream> OpenReadAsync(string storedPath, CancellationToken ct)
        {
            storedPath.Should().Be(expectedPath);
            return Task.FromResult<Stream>(new MemoryStream(zipBytes));
        }

        public Task<string> StoreAsync(string packageId, string version, string fileName, Stream content, CancellationToken ct)
            => throw new NotSupportedException();
        public string GetFullPath(string storedPath) => storedPath;
        public Task DeleteAsync(string storedPath, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeKrakenAi : IKrakenAi
    {
        private readonly StepSuggestionResult? _suggestion;
        private readonly string? _explainText;
        private readonly bool _disabled;

        public FakeKrakenAi(StepSuggestionResult suggestion) { _suggestion = suggestion; }
        public FakeKrakenAi(string explainText) { _explainText = explainText; }
        public FakeKrakenAi(bool disabled) { _disabled = disabled; }

        public string? LastUserText { get; private set; }

        public Task<TResult> CompleteAsync<TResult>(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            where TResult : class
        {
            if (_disabled) { throw new KrakenAiDisabledException("disabled"); }
            LastUserText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            return Task.FromResult((TResult)(object)_suggestion!);
        }

        public Task<KrakenAiCompletion> CompleteAsync(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
        {
            if (_disabled) { throw new KrakenAiDisabledException("disabled"); }
            LastUserText = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;
            return Task.FromResult(new KrakenAiCompletion(
                _explainText ?? "", 1, 1, TimeSpan.Zero, "Fake", "fake-model"));
        }

        public IAsyncEnumerable<string> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages, KrakenAiFeature feature,
            KrakenAiRequestOptions? options = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
