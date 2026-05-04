using KrakenDeploy.Server.Core.Domain.Lifecycles;
using KrakenDeploy.Server.Data.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class LifecycleConfiguration : IEntityTypeConfiguration<Lifecycle>
{
    public void Configure(EntityTypeBuilder<Lifecycle> builder)
    {
        builder.ToTable("lifecycles");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(1000);

        // Phases are a JSONB value-object array — not a separate table.
        builder.Property(x => x.Phases)
            .HasJsonbColumn<List<LifecyclePhase>>();

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}
