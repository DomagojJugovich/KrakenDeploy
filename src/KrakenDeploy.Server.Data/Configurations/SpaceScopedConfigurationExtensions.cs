using KrakenDeploy.Server.Core.Domain.Common;
using KrakenDeploy.Server.Core.Domain.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

/// <summary>
/// Shared configuration helpers for entities that implement <see cref="ISpaceScoped"/>.
/// Apply <see cref="ConfigureSpaceScope{T}"/> from each entity's
/// <c>IEntityTypeConfiguration&lt;T&gt;.Configure</c> method to add the
/// <c>SpaceId</c> property metadata, an index on <c>SpaceId</c> for query-filter
/// performance, and the FK to <c>spaces.id</c> with <see cref="DeleteBehavior.Restrict"/>
/// (deleting a Space requires the caller to first move or delete its contents —
/// no implicit cascade across the entire entity graph).
/// </summary>
public static class SpaceScopedConfigurationExtensions
{
    public static EntityTypeBuilder<T> ConfigureSpaceScope<T>(this EntityTypeBuilder<T> builder)
        where T : class, ISpaceScoped
    {
        builder.Property(x => x.SpaceId).IsRequired();
        builder.HasIndex(x => x.SpaceId);

        builder.HasOne<Space>()
            .WithMany()
            .HasForeignKey(x => x.SpaceId)
            .OnDelete(DeleteBehavior.Restrict);

        return builder;
    }
}
