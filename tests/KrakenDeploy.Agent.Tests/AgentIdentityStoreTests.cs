using FluentAssertions;
using KrakenDeploy.Agent.Config;
using KrakenDeploy.Agent.Identity;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Agent.Tests;

public sealed class AgentIdentityStoreTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), $"kraken-id-test-{Guid.NewGuid():N}");

    private AgentIdentityStore CreateStore()
    {
        var opts = Options.Create(new AgentConfig { DataPath = _dataDir });
        return new AgentIdentityStore(opts);
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

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }
}
