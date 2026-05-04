using KrakenDeploy.Server.Core.Domain.Runbooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace KrakenDeploy.Server.Data.Configurations;

public class RunbookConfiguration : IEntityTypeConfiguration<Runbook>
{
    public void Configure(EntityTypeBuilder<Runbook> builder)
    {
        builder.ToTable("runbooks");
        builder.HasKey(x => x.Id);

        builder.ConfigureSpaceScope();

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Description).HasMaxLength(1000);

        builder.HasOne(x => x.Project)
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();

        builder.Property(x => x.CreatedUtc).IsRequired();
    }
}

public class RunbookProcessConfiguration : IEntityTypeConfiguration<RunbookProcess>
{
    public void Configure(EntityTypeBuilder<RunbookProcess> builder)
    {
        builder.ToTable("runbook_processes");
        builder.HasKey(x => x.Id);

        builder.HasOne(x => x.Runbook)
            .WithOne(r => r.Process)
            .HasForeignKey<RunbookProcess>(x => x.RunbookId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RunbookStepConfiguration : IEntityTypeConfiguration<RunbookStep>
{
    public void Configure(EntityTypeBuilder<RunbookStep> builder)
    {
        builder.ToTable("runbook_steps");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.StepType).IsRequired().HasMaxLength(100);
        builder.Property(x => x.PackageId).HasMaxLength(200);

        builder.Property(x => x.TargetRoles)
            .HasColumnType("jsonb")
            .HasConversion(
                new KrakenDeploy.Server.Data.Conventions.JsonbValueConverter<List<string>>());

        builder.Property(x => x.Config)
            .HasColumnType("jsonb")
            .HasConversion(
                new KrakenDeploy.Server.Data.Conventions.JsonbValueConverter<Dictionary<string, string>>());

        builder.HasOne(x => x.Process)
            .WithMany(p => p.Steps)
            .HasForeignKey(x => x.ProcessId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ProcessId, x.SortOrder });
    }
}
