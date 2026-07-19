using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Contracts;
using KrakenDeploy.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// C6 — the update-info source of truth: a MANDATORY server-computed SHA-256 and
/// both contract versions on every offered update; no offer for an unhashable or
/// unknown binary.
/// </summary>
public sealed class ServerAgentUpdateServiceTests : IDisposable
{
    private const string Rid = "win-x64";

    private readonly string _binariesDir =
        Path.Combine(Path.GetTempPath(), $"kraken-agent-binaries-{Guid.NewGuid():N}");

    public ServerAgentUpdateServiceTests() => Directory.CreateDirectory(_binariesDir);

    public void Dispose()
    {
        try { Directory.Delete(_binariesDir, recursive: true); }
        catch { /* non-fatal */ }
    }

    private ServerAgentUpdateService CreateService()
    {
        var settings = Options.Create(new AgentUpdateSettings { BinariesPath = _binariesDir });
        var config = new ConfigurationBuilder().Build();
        return new ServerAgentUpdateService(
            settings, config, NullLogger<ServerAgentUpdateService>.Instance);
    }

    private string WriteBinary(string fileName, byte[] content)
    {
        var path = Path.Combine(_binariesDir, fileName);
        File.WriteAllBytes(path, content);
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private void WriteManifest(
        string version, string fileName, int contractVersion, string manifestSha = "deadbeef")
    {
        // Note the deliberately WRONG manifest sha256 — the service must ignore it
        // and serve its own computed hash instead.
        var json = $$"""
        {
          "latestVersion": "{{version}}",
          "rids": {
            "{{Rid}}": {
              "version": "{{version}}",
              "fileName": "{{fileName}}",
              "sizeBytes": 4,
              "sha256": "{{manifestSha}}",
              "contractVersion": {{contractVersion}}
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(_binariesDir, "version.json"), json);
    }

    [Fact]
    public void GetUpdateInfo_offers_update_with_computed_hash_and_contract_versions()
    {
        var realSha = WriteBinary("agent-win-x64.zip", "MZ01"u8.ToArray());
        WriteManifest("1.2.4", "agent-win-x64.zip", contractVersion: 1);

        var info = CreateService().GetUpdateInfo(Rid, currentVersion: "1.2.3");

        info.UpdateAvailable.Should().BeTrue();
        info.LatestVersion.Should().Be("1.2.4");
        info.DownloadUrl.Should().Be($"/api/agents/download/{Rid}");
        // The hash is computed from the file, NOT echoed from the manifest.
        info.Sha256.Should().Be(realSha);
        info.Sha256.Should().NotBe("deadbeef");
        info.ServerContractVersion.Should().Be(AgentContract.CurrentVersion);
        info.TargetContractVersion.Should().Be(1);
    }

    [Fact]
    public void GetUpdateInfo_reports_no_update_when_current_matches_latest()
    {
        WriteBinary("agent-win-x64.zip", "MZ01"u8.ToArray());
        WriteManifest("1.2.4", "agent-win-x64.zip", contractVersion: 1);

        var info = CreateService().GetUpdateInfo(Rid, currentVersion: "1.2.4");

        info.UpdateAvailable.Should().BeFalse();
        info.ServerContractVersion.Should().Be(AgentContract.CurrentVersion);
    }

    [Fact]
    public void GetUpdateInfo_refuses_to_offer_when_binary_is_missing()
    {
        // Manifest advertises a file that is not on disk — it cannot be hashed.
        WriteManifest("1.2.4", "does-not-exist.zip", contractVersion: 1);

        var info = CreateService().GetUpdateInfo(Rid, currentVersion: "1.2.3");

        info.UpdateAvailable.Should().BeFalse();
        info.Sha256.Should().BeNull();
    }

    [Fact]
    public void GetUpdateInfo_reports_no_update_for_unknown_rid()
    {
        WriteBinary("agent-win-x64.zip", "MZ01"u8.ToArray());
        WriteManifest("1.2.4", "agent-win-x64.zip", contractVersion: 1);

        var info = CreateService().GetUpdateInfo("linux-arm64", currentVersion: "1.2.3");

        info.UpdateAvailable.Should().BeFalse();
    }
}
