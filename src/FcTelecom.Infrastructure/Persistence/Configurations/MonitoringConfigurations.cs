using FcTelecom.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FcTelecom.Infrastructure.Persistence.Configurations;

public sealed class ServiceMonitorConfiguration : IEntityTypeConfiguration<ServiceMonitor>
{
    public void Configure(EntityTypeBuilder<ServiceMonitor> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Monitors", table => table.HasCheckConstraint(
            "CK_Monitors_Thresholds",
            "[IntervalSeconds] > 0 AND [TimeoutMs] > 0 AND [FailureThreshold] > 0 " +
            "AND [SuccessThreshold] > 0 AND [RequiredProbeQuorum] > 0"));

        builder.Property(monitor => monitor.RowVersion).IsRowVersion();
        builder.Property(monitor => monitor.Name).HasMaxLength(200).IsRequired();
        builder.Property(monitor => monitor.Target).HasMaxLength(500).IsRequired();
        builder.Property(monitor => monitor.ExpectedContent).HasMaxLength(500);
        builder.Property(monitor => monitor.DnsQueryName).HasMaxLength(300);

        // Convenience projections over TargetKind. Without these, EF maps them by
        // convention and the same fact ends up in three columns that can disagree.
        builder.Ignore(monitor => monitor.IsInternalTarget);
        builder.Ignore(monitor => monitor.HasWeakInternalTarget);
        builder.Ignore(monitor => monitor.StalenessTolerance);
        builder.Ignore(monitor => monitor.HasReducedConfidence);

        builder.HasOne(monitor => monitor.Service)
            .WithMany(service => service.Monitors)
            .HasForeignKey(monitor => monitor.ServiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(monitor => monitor.Location)
            .WithMany()
            .HasForeignKey(monitor => monitor.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(monitor => new { monitor.Enabled, monitor.CurrentState });
        builder.HasIndex(monitor => monitor.ServiceId);
        builder.HasIndex(monitor => monitor.LocationId);
    }
}

public sealed class ProbeConfiguration : IEntityTypeConfiguration<Probe>
{
    public void Configure(EntityTypeBuilder<Probe> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Probes");
        builder.Property(probe => probe.RowVersion).IsRowVersion();
        builder.Property(probe => probe.Name).HasMaxLength(150).IsRequired();
        builder.Property(probe => probe.EntraAppObjectId).HasMaxLength(100);

        // The NAME of the Key Vault secret. Never the key. A probe row is readable by the
        // reporting principal and appears in backups; a shared secret in it would be too.
        builder.Property(probe => probe.HmacKeyVaultSecretName).HasMaxLength(150);

        builder.Property(probe => probe.AgentVersion).HasMaxLength(50);

        builder.HasOne(probe => probe.Location)
            .WithMany()
            .HasForeignKey(probe => probe.LocationId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(probe => probe.Name).IsUnique().HasFilter("[IsArchived] = 0");
        builder.HasIndex(probe => probe.LastHeartbeatUtc);
    }
}

public sealed class MonitorProbeAssignmentConfiguration : IEntityTypeConfiguration<MonitorProbeAssignment>
{
    public void Configure(EntityTypeBuilder<MonitorProbeAssignment> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MonitorProbeAssignments");
        builder.HasKey(assignment => new { assignment.MonitorId, assignment.ProbeId });

        builder.HasOne(assignment => assignment.Monitor)
            .WithMany(monitor => monitor.ProbeAssignments)
            .HasForeignKey(assignment => assignment.MonitorId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(assignment => assignment.Probe)
            .WithMany(probe => probe.MonitorAssignments)
            .HasForeignKey(assignment => assignment.ProbeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class CheckResultConfiguration : IEntityTypeConfiguration<CheckResult>
{
    public void Configure(EntityTypeBuilder<CheckResult> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CheckResults");

        builder.Property(result => result.PacketLossPercent).HasPrecision(5, 2);
        builder.Property(result => result.ErrorCode).HasMaxLength(60);
        builder.Property(result => result.Detail).HasMaxLength(1000);

        builder.HasOne(result => result.Monitor)
            .WithMany()
            .HasForeignKey(result => result.MonitorId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(result => result.Probe)
            .WithMany()
            .HasForeignKey(result => result.ProbeId)
            .OnDelete(DeleteBehavior.Restrict);

        // The clustered key is (MonitorId, ObservedAtUtc), not the identity column. Both
        // access patterns against this table are ranges over exactly that: the correlation
        // engine reads the last few minutes for one monitor, and the retention job deletes
        // everything older than a cutoff. Clustering on Id instead would turn both into
        // scans, and this is by far the largest table in the schema.
        builder.HasKey(result => result.Id).IsClustered(false);

        builder.HasIndex(result => new { result.MonitorId, result.ObservedAtUtc })
            .IsClustered()
            .HasDatabaseName("CX_CheckResults_Monitor_ObservedAt");

        builder.HasIndex(result => result.ObservedAtUtc)
            .HasDatabaseName("IX_CheckResults_ObservedAt");
    }
}

public sealed class OutageEventConfiguration : IEntityTypeConfiguration<OutageEvent>
{
    public void Configure(EntityTypeBuilder<OutageEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OutageEvents", table => table.HasCheckConstraint(
            "CK_OutageEvents_EndAfterStart", "[EndUtc] IS NULL OR [EndUtc] >= [StartUtc]"));

        builder.Property(outage => outage.RowVersion).IsRowVersion();
        builder.Property(outage => outage.Cause).HasMaxLength(500);
        builder.Property(outage => outage.ClassificationReason).HasMaxLength(1000);
        builder.Property(outage => outage.CarrierTicketNumber).HasMaxLength(100);
        builder.Property(outage => outage.InternalTicketNumber).HasMaxLength(100);

        builder.HasOne(outage => outage.Monitor)
            .WithMany(monitor => monitor.Outages)
            .HasForeignKey(outage => outage.MonitorId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(outage => outage.Service)
            .WithMany()
            .HasForeignKey(outage => outage.ServiceId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(outage => outage.Location)
            .WithMany()
            .HasForeignKey(outage => outage.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(outage => outage.MaintenanceWindow)
            .WithMany()
            .HasForeignKey(outage => outage.MaintenanceWindowId)
            .OnDelete(DeleteBehavior.SetNull);

        // A tiny index over ongoing outages only. The dashboard tile and the outage queue
        // both read it, and it stays a handful of pages forever no matter how many
        // historical outages accumulate.
        builder.HasIndex(outage => outage.EndUtc)
            .HasFilter("[EndUtc] IS NULL")
            .HasDatabaseName("IX_OutageEvents_Ongoing");

        builder.HasIndex(outage => new { outage.LocationId, outage.StartUtc });
        builder.HasIndex(outage => new { outage.ServiceId, outage.StartUtc });
        builder.HasIndex(outage => outage.SlaCreditStatus);
    }
}

public sealed class MaintenanceWindowConfiguration : IEntityTypeConfiguration<MaintenanceWindow>
{
    public void Configure(EntityTypeBuilder<MaintenanceWindow> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MaintenanceWindows", table => table.HasCheckConstraint(
            "CK_MaintenanceWindows_EndAfterStart", "[EndUtc] > [StartUtc]"));

        builder.Property(window => window.RowVersion).IsRowVersion();
        builder.Property(window => window.RecurrenceRule).HasMaxLength(500);
        builder.Property(window => window.Description).HasMaxLength(1000);

        builder.HasOne(window => window.Service).WithMany()
            .HasForeignKey(window => window.ServiceId).OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(window => window.Location).WithMany()
            .HasForeignKey(window => window.LocationId).OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(window => window.Vendor).WithMany()
            .HasForeignKey(window => window.VendorId).OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(window => new { window.StartUtc, window.EndUtc });
    }
}

public sealed class CoverageGapConfiguration : IEntityTypeConfiguration<CoverageGap>
{
    public void Configure(EntityTypeBuilder<CoverageGap> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("CoverageGaps", table => table.HasCheckConstraint(
            "CK_CoverageGaps_EndAfterStart", "[EndUtc] IS NULL OR [EndUtc] >= [StartUtc]"));

        builder.Property(gap => gap.Detail).HasMaxLength(500);

        builder.HasOne(gap => gap.Monitor)
            .WithMany()
            .HasForeignKey(gap => gap.MonitorId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(gap => new { gap.MonitorId, gap.StartUtc });

        builder.HasIndex(gap => gap.EndUtc)
            .HasFilter("[EndUtc] IS NULL")
            .HasDatabaseName("IX_CoverageGaps_Open");
    }
}

public sealed class AvailabilityRollupConfiguration : IEntityTypeConfiguration<AvailabilityRollup>
{
    public void Configure(EntityTypeBuilder<AvailabilityRollup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("AvailabilityRollups");
        builder.HasKey(rollup => rollup.Id);

        builder.Property(rollup => rollup.AvailabilityPercent).HasPrecision(9, 6);
        builder.Property(rollup => rollup.AvgPacketLossPercent).HasPrecision(5, 2);

        builder.HasOne(rollup => rollup.Monitor)
            .WithMany()
            .HasForeignKey(rollup => rollup.MonitorId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique on (monitor, grain, period start), which is what makes the rollup jobs
        // idempotent: re-running a period upserts rather than duplicating. That property
        // is the difference between an availability history you can correct after finding
        // a bug and one you are stuck with.
        builder.HasIndex(rollup => new { rollup.MonitorId, rollup.Grain, rollup.PeriodStartUtc })
            .IsUnique()
            .HasDatabaseName("UX_AvailabilityRollups_Monitor_Grain_Period");

        builder.HasIndex(rollup => new { rollup.ServiceId, rollup.Grain, rollup.PeriodStartUtc });
    }
}
