using System.Security.Cryptography;
using FluentAssertions;
using KrakenDeploy.Contracts.Crypto;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Environments;
using KrakenDeploy.Server.Core.Domain.Notifications;
using KrakenDeploy.Server.Core.Domain.Processes;
using KrakenDeploy.Server.Core.Domain.Projects;
using KrakenDeploy.Server.Core.Domain.Releases;
using KrakenDeploy.Server.Core.Domain.Security;
using KrakenDeploy.Server.Core.Domain.Targets;
using KrakenDeploy.Server.Core.Domain.Variables;
using KrakenDeploy.Server.Data.Encryption;
using KrakenDeploy.Server.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// End-to-end round-trip for the DEK-rotation walk (M13.D.2): seed every secret
/// store under DEK_old, run <see cref="DekRotationWalk.ReEncryptAllAsync"/>
/// (old → new) in a transaction, and assert each secret now decrypts under
/// DEK_new (and NOT under DEK_old — proving the ciphertext actually changed).
/// Exercises the two JSONB reassignment hazards (release snapshots + offline-drop
/// config) plus the scalar stores.
/// <para>
/// The walk is whole-DB, so the shared fixture (which holds other tests' data
/// under other keys) is TRUNCATEd first — otherwise the walk would try to
/// decrypt a foreign-key row under DEK_old and throw.
/// </para>
/// </summary>
[Trait("Category", "Docker")]
[Collection("Postgres")]
public sealed class DekRotationWalkTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly byte[] OldDek = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
    private static readonly byte[] NewDek = RandomNumberGenerator.GetBytes(AesGcmCipher.KeyBytes);
    private static readonly string OldDekB64 = Convert.ToBase64String(OldDek);

    public async Task InitializeAsync()
    {
        // The walk decrypts EVERY encrypted row under DEK_old; foreign rows left
        // by sibling tests are under other keys and would make it throw. Start
        // from a clean slate (CASCADE clears FK dependents like deployments).
        await using var db = postgres.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE variables, variable_sets, releases, space_ai_settings, " +
            "identity_providers, smtp_settings, deployment_targets RESTART IDENTITY CASCADE");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Rotate_re_encrypts_every_store_from_old_DEK_to_new_DEK()
    {
        // ── Seed under DEK_old ────────────────────────────────────────────────
        // Variable + release snapshot via the real services (encrypt under old).
        var (project, env, _) = await SeedProjectWithEnvAsync();
        var vars = new VariableService(postgres, TestCrypto.Service(OldDekB64));
        await vars.CreateVariableAsync(project.Id, "ApiKey", "super-secret",
            VariableType.Sensitive, scope: new VariableScope());
        await SeedSimpleProcessAsync(project.Id);
        var release = await new ReleaseService(postgres).CreateAsync(project.Id, "1.0.0");
        // Sanity: snapshot froze the ciphertext (not plaintext) under DEK_old.
        var seededSnap = release.VariableSnapshot.Single(v => v.Name == "ApiKey");
        AesGcmCipher.Decrypt(OldDek, seededSnap.Value).Should().Be("super-secret");

        // Direct-seed the scalar + offline-drop stores under DEK_old.
        Guid targetId;
        await using (var db = postgres.CreateContext())
        {
            db.SpaceAiSettings.Add(new SpaceAiSettings
            {
                SpaceId = Guid.NewGuid(),
                ApiKeyEncrypted = AesGcmCipher.Encrypt(OldDek, "ai-provider-key"),
            });
            db.SmtpSettings.Add(new SmtpSettings
            {
                Host = "smtp.local", FromAddress = "noreply@laus.hr",
                PasswordEncrypted = AesGcmCipher.Encrypt(OldDek, "smtp-password"),
            });
            db.IdentityProviders.Add(new IdentityProvider
            {
                Name = "Entra", Type = IdentityProviderType.AzureAd,
                ClientSecretEncrypted = AesGcmCipher.Encrypt(OldDek, "oidc-client-secret"),
            });
            var target = new DeploymentTarget
            {
                Name = $"drop-{Guid.NewGuid():N}", Roles = ["web"],
                TransportMode = TransportMode.Reverse,
                OfflineDropConfig = new OfflineDropConfig
                {
                    HmacKeyEncrypted = AesGcmCipher.Encrypt(OldDek, "hmac-key"),
                    BundleKeyEncrypted = AesGcmCipher.Encrypt(OldDek, "bundle-key"),
                    SmtpPasswordEncrypted = AesGcmCipher.Encrypt(OldDek, "drop-smtp-pw"),
                    WebhookSecretEncrypted = AesGcmCipher.Encrypt(OldDek, "webhook-secret"),
                    FileSharePasswordEncrypted = AesGcmCipher.Encrypt(OldDek, "fileshare-pw"),
                },
            };
            db.DeploymentTargets.Add(target);
            await db.SaveChangesAsync();
            targetId = target.Id;
        }

        // ── Rotate: DEK_old → DEK_new, in one transaction ─────────────────────
        DekReEncryptCounts counts;
        await using (var db = postgres.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            counts = await DekRotationWalk.ReEncryptAllAsync(db, OldDek, NewDek);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        counts.Variables.Should().Be(1);
        counts.SnapshotEntries.Should().Be(1);
        counts.Releases.Should().Be(1);
        counts.AiSettings.Should().Be(1);
        counts.IdentityProviders.Should().Be(1);
        counts.Smtp.Should().Be(1);
        counts.OfflineDropFields.Should().Be(1);

        // ── Assert: everything now decrypts under DEK_new, not DEK_old ────────
        await using (var db = postgres.CreateContext())
        {
            var v = await db.Variables.IgnoreQueryFilters()
                .SingleAsync(x => x.Type == VariableType.Sensitive);
            AesGcmCipher.Decrypt(NewDek, v.Value).Should().Be("super-secret");
            // The ciphertext genuinely moved keys — old key can no longer read it.
            var decryptUnderOld = () => AesGcmCipher.Decrypt(OldDek, v.Value);
            decryptUnderOld.Should().Throw<CryptographicException>(
                "the value was re-encrypted under a new DEK; the old DEK must fail");

            var snap = (await db.Releases.IgnoreQueryFilters().SingleAsync())
                .VariableSnapshot.Single(s => s.Name == "ApiKey");
            AesGcmCipher.Decrypt(NewDek, snap.Value).Should().Be("super-secret",
                "the JSONB snapshot list was rebuilt + reassigned so the UPDATE persisted");

            var ai = await db.SpaceAiSettings.IgnoreQueryFilters().SingleAsync();
            AesGcmCipher.Decrypt(NewDek, ai.ApiKeyEncrypted!).Should().Be("ai-provider-key");

            var smtp = await db.SmtpSettings.IgnoreQueryFilters().SingleAsync();
            AesGcmCipher.Decrypt(NewDek, smtp.PasswordEncrypted!).Should().Be("smtp-password");

            var idp = await db.IdentityProviders.IgnoreQueryFilters().SingleAsync();
            AesGcmCipher.Decrypt(NewDek, idp.ClientSecretEncrypted!).Should().Be("oidc-client-secret");

            var cfg = (await db.DeploymentTargets.IgnoreQueryFilters()
                .SingleAsync(t => t.Id == targetId)).OfflineDropConfig!;
            AesGcmCipher.Decrypt(NewDek, cfg.HmacKeyEncrypted!).Should().Be("hmac-key",
                "the offline-drop JSONB config was flagged IsModified so the UPDATE persisted");
            AesGcmCipher.Decrypt(NewDek, cfg.BundleKeyEncrypted!).Should().Be("bundle-key");
            AesGcmCipher.Decrypt(NewDek, cfg.SmtpPasswordEncrypted!).Should().Be("drop-smtp-pw");
            AesGcmCipher.Decrypt(NewDek, cfg.WebhookSecretEncrypted!).Should().Be("webhook-secret");
            AesGcmCipher.Decrypt(NewDek, cfg.FileSharePasswordEncrypted!).Should().Be("fileshare-pw");
        }
    }

    // ── Seed helpers (mirror ReleaseVariableSnapshotTests) ────────────────────

    private async Task<(Project project, DeploymentEnvironment env, DeploymentTarget target)>
        SeedProjectWithEnvAsync()
    {
        await using var db = postgres.CreateContext();
        var slug = $"dek-{Guid.NewGuid():N}";
        var project = new Project { Slug = slug, Name = slug };
        var env = new DeploymentEnvironment { Slug = $"env-{Guid.NewGuid():N}", Name = "Production" };
        var target = new DeploymentTarget
        {
            Name = $"tgt-{Guid.NewGuid():N}", Roles = [], TransportMode = TransportMode.Reverse,
        };
        db.Projects.Add(project);
        db.Environments.Add(env);
        db.DeploymentTargets.Add(target);
        await db.SaveChangesAsync();
        return (project, env, target);
    }

    private async Task SeedSimpleProcessAsync(Guid projectId)
    {
        await using var db = postgres.CreateContext();
        var process = new Process { OwnerKind = ProcessOwnerKind.Project, OwnerId = projectId };
        db.Processes.Add(process);
        await db.SaveChangesAsync();

        db.ProcessSteps.Add(new ProcessStep
        {
            ProcessId = process.Id, Name = "Approve", StepType = "Octopus.Manual",
            PackageId = "", TargetRoles = [], Config = [], SortOrder = 0,
        });
        await db.SaveChangesAsync();
    }
}
