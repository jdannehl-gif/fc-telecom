using FcTelecom.Domain.Organization;
using FcTelecom.Domain.Services;

namespace FcTelecom.Domain.Calculations;

public enum DiversityVerdict
{
    /// <summary>One service only. Nothing to fail over to.</summary>
    NoBackup = 1,

    /// <summary>Two or more services, but they share something that can take both out.</summary>
    SharedRisk = 2,

    /// <summary>Two or more services with no known shared dependency.</summary>
    Diverse = 3,

    /// <summary>
    /// Enough services to be redundant, but the dependency assessment has not been done.
    /// Reported separately from <see cref="Diverse"/> because "we have not checked" is
    /// not the same as "we checked and it is fine".
    /// </summary>
    Unassessed = 4,
}

public readonly record struct DiversityRisk(
    DependencyType DependencyType,
    DependencyConfidence Confidence,
    Guid ServiceId,
    Guid ConflictingServiceId,
    string? Evidence,
    string Description);

public readonly record struct DiversityAssessment(
    Guid LocationId,
    DiversityVerdict Verdict,
    int LiveServiceCount,
    int LiveDataServiceCount,
    IReadOnlyList<DiversityRisk> Risks,
    string Summary);

/// <summary>
/// Decides whether a location's backup connectivity is real.
/// </summary>
/// <remarks>
/// The problem this solves: two circuits from two different carriers at the same address
/// routinely share the last-mile provider, the conduit into the building, the cell tower,
/// or an upstream transit provider. A fibre cut, a flooded vault, or a tower outage then
/// takes both, and the organisation discovers its redundancy was notional at the worst
/// possible moment.
/// <para>
/// The analyser is deliberately pessimistic. A dependency that is merely
/// <see cref="DependencyConfidence.Suspected"/> counts as a risk; only an explicit
/// <see cref="DependencyConfidence.RuledOut"/> clears it. Optimism here is expensive and
/// the cost lands during an outage.
/// </para>
/// </remarks>
public static class DiversityAnalyzer
{
    /// <summary>
    /// Assesses one location. <paramref name="services"/> should be that location's
    /// services with their <see cref="TelecomService.Dependencies"/> loaded.
    /// </summary>
    public static DiversityAssessment Assess(Location location, IReadOnlyList<TelecomService> services)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(services);

        var live = services.Where(service => service.IsLive).ToList();
        var liveData = live.Where(service => service.IsDataService).ToList();

        if (liveData.Count < 2)
        {
            return new DiversityAssessment(
                location.Id,
                DiversityVerdict.NoBackup,
                live.Count,
                liveData.Count,
                [],
                liveData.Count == 0
                    ? "No live data service at this location."
                    : "Only one live data service. There is nothing to fail over to.");
        }

        var risks = new List<DiversityRisk>();
        var liveDataIds = liveData.Select(service => service.Id).ToHashSet();

        // 1. Explicitly recorded dependencies between two live data services here.
        foreach (TelecomService service in liveData)
        {
            foreach (ServiceDependency dependency in service.Dependencies)
            {
                if (dependency.Confidence == DependencyConfidence.RuledOut)
                {
                    continue;
                }

                if (!liveDataIds.Contains(dependency.DependsOnServiceId))
                {
                    continue;
                }

                risks.Add(new DiversityRisk(
                    dependency.DependencyType,
                    dependency.Confidence,
                    service.Id,
                    dependency.DependsOnServiceId,
                    dependency.Evidence,
                    Describe(dependency.DependencyType, dependency.Confidence)));
            }
        }

        // 2. Inferred from the vendor roles: two services whose last-mile provider is the
        //    same company are not diverse, regardless of whose logo is on the invoice.
        //    This catch is the reason the schema carries four vendor roles instead of one.
        risks.AddRange(InferSharedVendor(
            liveData,
            service => service.LastMileVendorId,
            DependencyType.SharedLastMile,
            "Both services are delivered over the same last-mile provider. A cut or fault " +
            "in that provider's plant takes both, whatever the carriers on the invoices say."));

        risks.AddRange(InferSharedVendor(
            liveData,
            service => service.UnderlyingNetworkOwnerVendorId,
            DependencyType.SharedUpstreamTransit,
            "Both services ride the same underlying network. An upstream backbone event " +
            "affects both simultaneously."));

        // 3. Same carrier for primary and backup. Not automatically fatal — a carrier can
        //    genuinely deliver two diverse paths — but it needs to have been checked.
        risks.AddRange(InferSharedVendor(
            liveData,
            service => service.CarrierVendorId,
            DependencyType.Other,
            "Primary and backup are bought from the same carrier. This can still be diverse, " +
            "but it has to be confirmed rather than assumed — ask for a diversity letter."));

        risks = Deduplicate(risks);

        if (risks.Count > 0)
        {
            return new DiversityAssessment(
                location.Id,
                DiversityVerdict.SharedRisk,
                live.Count,
                liveData.Count,
                risks,
                $"{liveData.Count} live data services, but {risks.Count} shared-risk finding(s) " +
                "mean the backup may not survive an event that takes the primary.");
        }

        // No risks found — but was anybody actually looking?
        bool anyAssessment = liveData.Any(service => service.Dependencies.Count > 0);

        return anyAssessment
            ? new DiversityAssessment(
                location.Id, DiversityVerdict.Diverse, live.Count, liveData.Count, [],
                "Dependencies have been assessed and no shared risk was found.")
            : new DiversityAssessment(
                location.Id, DiversityVerdict.Unassessed, live.Count, liveData.Count, [],
                "Multiple data services are present, but no dependency assessment has been " +
                "recorded. Diversity is unverified, which is not the same as absent.");
    }

    private static IEnumerable<DiversityRisk> InferSharedVendor(
        IReadOnlyList<TelecomService> services,
        Func<TelecomService, Guid?> vendorSelector,
        DependencyType dependencyType,
        string description)
    {
        var byVendor = services
            .Where(service => vendorSelector(service).HasValue)
            .GroupBy(service => vendorSelector(service)!.Value)
            .Where(group => group.Count() > 1);

        foreach (var group in byVendor)
        {
            var members = group.ToList();
            for (int i = 0; i < members.Count - 1; i++)
            {
                for (int j = i + 1; j < members.Count; j++)
                {
                    yield return new DiversityRisk(
                        dependencyType,
                        DependencyConfidence.Suspected,
                        members[i].Id,
                        members[j].Id,
                        Evidence: "Inferred from vendor records rather than confirmed with the carrier.",
                        description);
                }
            }
        }
    }

    /// <summary>
    /// Collapses risks describing the same pair of services and the same dependency type,
    /// keeping the highest confidence. An explicitly confirmed shared last-mile and an
    /// inferred one are the same finding, and reporting it twice makes the list look
    /// alarming for the wrong reason.
    /// </summary>
    private static List<DiversityRisk> Deduplicate(List<DiversityRisk> risks) =>
        [.. risks
            .GroupBy(risk => (
                First: risk.ServiceId < risk.ConflictingServiceId ? risk.ServiceId : risk.ConflictingServiceId,
                Second: risk.ServiceId < risk.ConflictingServiceId ? risk.ConflictingServiceId : risk.ServiceId,
                risk.DependencyType))
            .Select(group => group.OrderByDescending(risk => (int)risk.Confidence).First())];

    private static string Describe(DependencyType type, DependencyConfidence confidence)
    {
        string qualifier = confidence == DependencyConfidence.Confirmed ? "Confirmed" : "Suspected";

        string detail = type switch
        {
            DependencyType.SharedLastMile =>
                "shared last-mile provider — one plant fault takes both services",
            DependencyType.SharedConduit =>
                "shared conduit — a single dig or flooded vault takes both services",
            DependencyType.SharedBuildingEntrance =>
                "shared building entrance — one damaged entrance facility takes both services",
            DependencyType.SharedTower =>
                "shared wireless tower — a tower or backhaul outage takes both services",
            DependencyType.SharedUpstreamTransit =>
                "shared upstream transit — a backbone event affects both services",
            DependencyType.SharedCpe =>
                "shared customer equipment — a single device failure takes both services",
            DependencyType.SharedPowerCircuit =>
                "shared power circuit — one breaker takes both services",
            _ => "shared dependency recorded between these services",
        };

        return $"{qualifier}: {detail}.";
    }
}
