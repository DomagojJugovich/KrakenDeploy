using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Health;

namespace KrakenDeploy.Server.Tests;

/// <summary>
/// Unit tests for the C3/P1 readiness sub-probes that carry the bug-prone logic
/// (encryption round-trip + data-dir write). The DB reachability leg is a thin
/// CanConnectAsync wrapper exercised by the integration/host tests, not here.
/// </summary>
public sealed class ReadinessProbeTests
{
    private sealed class FakeEncryption(
        Func<string, string>? encrypt = null,
        Func<string, string>? decrypt = null) : IEncryptionService
    {
        public string Encrypt(string plaintext) => (encrypt ?? (s => s))(plaintext);
        public string Decrypt(string ciphertext) => (decrypt ?? (s => s))(ciphertext);
    }

    [Fact]
    public void ProbeEncryption_is_healthy_when_round_trip_matches()
    {
        var probe = new ReadinessProbe(new FakeEncryption(), Path.GetTempPath());

        probe.ProbeEncryption(out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void ProbeEncryption_is_unhealthy_when_decrypt_throws()
    {
        // Simulates a bricked DEK / wrong KEK: the DEK can't decrypt.
        var probe = new ReadinessProbe(
            new FakeEncryption(decrypt: _ => throw new CryptographicException("auth tag mismatch")),
            Path.GetTempPath());

        probe.ProbeEncryption(out var error).Should().BeFalse();
        error.Should().Contain(nameof(CryptographicException));
        error.Should().NotContain("auth tag", "the sanitised reason must not leak crypto detail");
    }

    [Fact]
    public void ProbeEncryption_is_unhealthy_when_round_trip_does_not_match()
    {
        var probe = new ReadinessProbe(
            new FakeEncryption(decrypt: _ => "something else"), Path.GetTempPath());

        probe.ProbeEncryption(out var error).Should().BeFalse();
        error.Should().Contain("mismatch");
    }

    [Fact]
    public void ProbeDataDirectory_is_healthy_for_a_writable_dir_and_leaves_nothing_behind()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kraken-readiness-" + Guid.NewGuid().ToString("N"));
        try
        {
            var probe = new ReadinessProbe(new FakeEncryption(), dir);

            probe.ProbeDataDirectory(out var error).Should().BeTrue();
            error.Should().BeNull();
            Directory.GetFiles(dir).Should().BeEmpty("the probe file is deleted after the write");
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }

    [Fact]
    public void ProbeDataDirectory_is_unhealthy_when_the_path_cannot_be_created()
    {
        // A real FILE stands where the data dir's parent should be, so
        // Directory.CreateDirectory on a path *under* it must fail.
        var file = Path.Combine(Path.GetTempPath(), "kraken-readiness-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "x");
        try
        {
            var probe = new ReadinessProbe(new FakeEncryption(), Path.Combine(file, "sub"));

            probe.ProbeDataDirectory(out var error).Should().BeFalse();
            error.Should().StartWith("data directory not writable");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
