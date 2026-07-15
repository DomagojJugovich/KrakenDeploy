using System.Text;
using System.Text.Json;
using FluentAssertions;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Tests;

public sealed class AgentIdentityStoreTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"kraken-id-test-{Guid.NewGuid():N}");

    private AgentIdentityStore CreateStore()
    {
        var opts = Options.Create(new AgentConfig { DataPath = _dataDir });
        return new AgentIdentityStore(opts, NullLogger<AgentIdentityStore>.Instance);
    }

    [Fact]
    public async Task TryLoadAsync_returns_null_when_identity_file_does_not_exist()
    {
        Directory.CreateDirectory(_dataDir);
        var result = await CreateStore().TryLoadAsync(CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_then_TryLoadAsync_roundtrips_all_fields()
    {
        var expected = new AgentIdentity
        {
            AgentId = Guid.NewGuid(),
            AgentToken = "test.jwt.token",
            ServerUrl = "https://localhost:5443",
        };

        var store = CreateStore();
        await store.SaveAsync(expected, CancellationToken.None);
        var loaded = await store.TryLoadAsync(CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.AgentId.Should().Be(expected.AgentId);
        loaded.AgentToken.Should().Be(expected.AgentToken);
        loaded.ServerUrl.Should().Be(expected.ServerUrl);
    }

    [Fact]
    public async Task SaveAsync_creates_data_directory_if_it_does_not_exist()
    {
        // _dataDir has NOT been created — SaveAsync must create it.
        var store = CreateStore();
        var identity = new AgentIdentity
        {
            AgentId = Guid.NewGuid(),
            AgentToken = "token",
            ServerUrl = "https://localhost:5443",
        };

        await store.SaveAsync(identity, CancellationToken.None);

        Directory.Exists(_dataDir).Should().BeTrue();
        (await store.TryLoadAsync(CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task SaveAsync_encrypts_the_token_at_rest_on_windows()
    {
        // A8/T1-12: DPAPI is Windows-only; on other platforms the chmod-600 path
        // (covered elsewhere) applies and this assertion does not.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var identity = new AgentIdentity
        {
            AgentId = Guid.NewGuid(),
            AgentToken = "super-secret-jwt-value-must-not-be-plaintext",
            ServerUrl = "https://localhost:5443",
        };

        var store = CreateStore();
        await store.SaveAsync(identity, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_dataDir, "agent.json"));
        var text = Encoding.UTF8.GetString(bytes);
        text.Should().NotContain(identity.AgentToken,
            "the bearer token must not sit in plaintext at rest on Windows");
        text.Should().StartWith("KDPAPIv1", "the file must be marked DPAPI-protected");

        var loaded = await store.TryLoadAsync(CancellationToken.None);
        loaded!.AgentToken.Should().Be(identity.AgentToken, "the protected file must still round-trip");
    }

    [Fact]
    public async Task TryLoadAsync_migrates_legacy_plaintext_to_protected_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_dataDir);
        var path = Path.Combine(_dataDir, "agent.json");
        var identity = new AgentIdentity
        {
            AgentId = Guid.NewGuid(),
            AgentToken = "legacy-plaintext-token",
            ServerUrl = "https://localhost:5443",
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(identity));

        // Reads the legacy plaintext file...
        var loaded = await CreateStore().TryLoadAsync(CancellationToken.None);
        loaded!.AgentToken.Should().Be(identity.AgentToken);

        // ...and rewrites it in the protected form so the token stops being plaintext.
        var bytes = await File.ReadAllBytesAsync(path);
        Encoding.UTF8.GetString(bytes).Should().StartWith("KDPAPIv1",
            "a legacy plaintext agent.json must be migrated to DPAPI-protected form on read");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }
}
