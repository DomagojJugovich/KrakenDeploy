using FluentAssertions;
using KrakenDeploy.Server.Core.Domain.Ai;
using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Spaces;
using KrakenDeploy.Server.Data;
using KrakenDeploy.Server.Data.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KrakenDeploy.Server.Data.Tests;

/// <summary>
/// Model-metadata conventions. These inspect <see cref="DbContext.Model"/>,
/// which EF builds offline in <c>OnModelCreating</c> — no database connection is
/// opened — so the tests run on any CI without the Postgres container (unlike the
/// <c>[Collection("Postgres")]</c> integration tests).
/// </summary>
public class ModelConventionsTests
{
    private static KrakenDbContext BuildModelContext()
    {
        var spaceContext = new DefaultSpaceContext();
        var options = new DbContextOptionsBuilder<KrakenDbContext>()
            // Never connected — only the model is inspected.
            .UseNpgsql("Host=localhost;Database=model_only;Username=x;Password=x")
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new KrakenDbContext(options, spaceContext);
    }

    [Fact]
    public void Every_space_scoped_entity_anchors_space_id_by_fk()
    {
        using var context = BuildModelContext();

        // space_ai_settings is a known straggler removed by fix 7 (the settings
        // table consolidation). Remove this exemption when that table is dropped.
        var exempt = new HashSet<Type> { typeof(SpaceAiSettings) };

        // Post composite-FK hardening: every ISpaceScoped entity must anchor its
        // space_id through a foreign key that INCLUDES space_id and whose principal
        // is EITHER spaces (aggregate roots — direct FK) OR another ISpaceScoped
        // entity (children/joins — the composite (space_id, parent_id) →
        // parent(space_id, id) FK, which transitively reaches spaces through the
        // parent's own anchor). This guarantees a row can neither be orphaned from
        // its Space nor reference a parent in a different Space.
        var offenders = context.Model.GetEntityTypes()
            // TPH: the FK lives on the inheritance ROOT (ServerTask), so skip
            // derived types — mirrors KrakenDbContext.ApplySpaceQueryFilters.
            .Where(et => typeof(ISpaceScoped).IsAssignableFrom(et.ClrType))
            .Where(et => et.BaseType is null)
            .Where(et => !exempt.Contains(et.ClrType))
            .Where(et => !et.GetForeignKeys().Any(fk =>
                fk.Properties.Any(p => p.Name == nameof(ISpaceScoped.SpaceId))
                && (fk.PrincipalEntityType.ClrType == typeof(Space)
                    || typeof(ISpaceScoped).IsAssignableFrom(fk.PrincipalEntityType.ClrType))))
            .Select(et => et.ClrType.Name)
            .OrderBy(n => n)
            .ToList();

        offenders.Should().BeEmpty(because:
            "every ISpaceScoped entity must anchor space_id — roots via a direct FK to " +
            "spaces, children/joins via a composite (space_id, parent_id) FK to another " +
            "space-scoped entity");
    }
}
