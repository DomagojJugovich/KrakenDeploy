using KrakenDeploy.Server.Core.Domain.Processes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// Mapping for the unified <see cref="Process"/> (<c>processes</c>) — one row per
/// (owner_kind, owner_id). No FK to the owner (polymorphic); the owning service
/// deletes the process when the project/runbook is deleted.
/// </summary>
public class ProcessConfiguration : IEntityTypeConfiguration<Process>
{
    public void Configure(EntityTypeBuilder<Process> builder)
    {
        builder.ToTable("processes");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.OwnerKind).IsRequired().HasConversion<int>();
        builder.Property(x => x.OwnerId).IsRequired();

        // One process per owner.
        builder.HasIndex(x => new { x.OwnerKind, x.OwnerId }).IsUnique();
    }
}
