using FcTelecom.Domain.Calculations;
using FcTelecom.Domain.Common;
using FcTelecom.Domain.Financials;
using FcTelecom.Domain.Organization;
using FcTelecom.Domain.Services;
using Shouldly;

namespace FcTelecom.Domain.UnitTests;

public sealed class SpendCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 8, 19);

    private static TelecomService ServiceWith(
        decimal mrc, BillingFrequency frequency = BillingFrequency.Monthly,
        int downloadKbps = 1_000_000, int cirKbps = 1_000_000,
        ServiceType type = ServiceType.Internet, TransportMedia media = TransportMedia.Fiber,
        DateOnly? from = null, DateOnly? to = null)
    {
        var service = new TelecomService
        {
            ServiceType = type,
            Media = media,
            Status = ServiceStatus.Active,
            Bandwidth = new ServiceBandwidth
            {
                DownloadKbps = downloadKbps,
                UploadKbps = downloadKbps,
                CommittedInformationRateKbps = cirKbps,
            },
        };

        service.CostHistory.Add(new ServiceCost
        {
            EffectiveFrom = from ?? Today.AddMonths(-6),
            EffectiveTo = to,
            MonthlyRecurringCharge = mrc,
            CurrencyCode = "USD",
            BillingFrequency = frequency,
        });

        return service;
    }

    [Fact]
    public void MonthlySpend_SumsCurrentCostRecords()
    {
        var services = new[] { ServiceWith(2_480m), ServiceWith(389m), ServiceWith(612m) };

        SpendCalculator.MonthlySpend(services, Today).Amount.ShouldBe(3_481m);
    }

    /// <summary>
    /// Non-monthly billing cycles are normalised. An annually-billed $12,000 circuit is
    /// $1,000/month here — otherwise it either vanishes from eleven monthly reports or
    /// distorts the twelfth.
    /// </summary>
    [Fact]
    public void NonMonthlyBilling_IsNormalisedToAMonthlyEquivalent()
    {
        TelecomService annual = ServiceWith(12_000m, BillingFrequency.Annual);

        SpendCalculator.MonthlySpend([annual], Today).Amount.ShouldBe(1_000m);
    }

    /// <summary>
    /// A service with no cost record on the date contributes nothing, rather than zero.
    /// An ordered-but-not-yet-activated circuit genuinely has no cost yet, and that is a
    /// different fact from costing nothing.
    /// </summary>
    [Fact]
    public void ServiceWithNoCostOnThatDate_ContributesNothing()
    {
        TelecomService future = ServiceWith(500m, from: Today.AddMonths(3));

        SpendCalculator.MonthlySpend([future], Today).Amount.ShouldBe(0m);
    }

    [Fact]
    public void ExpiredCostRecord_IsNotCounted()
    {
        TelecomService expired = ServiceWith(
            500m, from: Today.AddYears(-2), to: Today.AddMonths(-1));

        SpendCalculator.MonthlySpend([expired], Today).Amount.ShouldBe(0m);
    }

    [Fact]
    public void AnnualizedSpend_IsTwelveTimesMonthly()
    {
        SpendCalculator.AnnualizedSpend([ServiceWith(1_000m)], Today).Amount.ShouldBe(12_000m);
    }

    /// <summary>
    /// Cost per Mbps uses the committed rate where one exists.
    /// </summary>
    /// <remarks>
    /// Comparing a 1 Gbps best-effort coax service against a 1 Gbps CIR fibre service at
    /// the advertised rate makes the coax look like a bargain — which is exactly the
    /// conclusion that puts a clinic's primary circuit on it.
    /// </remarks>
    [Fact]
    public void CostPerMbps_UsesTheCommittedRate_WhenOneExists()
    {
        // $2,000/month over a 500 Mbps CIR on a nominally 1 Gbps service.
        TelecomService service = ServiceWith(2_000m, downloadKbps: 1_000_000, cirKbps: 500_000);

        SpendCalculator.CostPerMbps(service, Today).ShouldBe(4m);
    }

    [Fact]
    public void CostPerMbps_FallsBackToAdvertisedSpeed_WhenThereIsNoCommittedRate()
    {
        TelecomService bestEffort = ServiceWith(400m, downloadKbps: 800_000, cirKbps: 0);

        SpendCalculator.CostPerMbps(bestEffort, Today).ShouldBe(0.5m);
    }

    [Fact]
    public void CostPerMbps_IsNullForVoiceServices()
    {
        // A cost-per-Mbps figure for a POTS alarm line is noise in a report meant to
        // surface outliers.
        TelecomService pots = ServiceWith(87.40m, type: ServiceType.Pots, media: TransportMedia.Copper);

        SpendCalculator.CostPerMbps(pots, Today).ShouldBeNull();
    }

    /// <summary>
    /// Outliers are found against the median, not the mean.
    /// </summary>
    /// <remarks>
    /// Telecom pricing has a long right tail. A handful of legacy T1s at $400/Mbps drag a
    /// mean so far up that genuinely overpriced circuits look reasonable beside it. The
    /// median is not fooled.
    /// </remarks>
    [Fact]
    public void CostOutliers_AreFoundAgainstTheMedian_NotTheMean()
    {
        var peers = new List<TelecomService>
        {
            ServiceWith(1_000m, downloadKbps: 1_000_000, cirKbps: 1_000_000), // $1.00/Mbps
            ServiceWith(1_100m, downloadKbps: 1_000_000, cirKbps: 1_000_000), // $1.10
            ServiceWith(900m, downloadKbps: 1_000_000, cirKbps: 1_000_000),   // $0.90
            ServiceWith(1_050m, downloadKbps: 1_000_000, cirKbps: 1_000_000), // $1.05
            ServiceWith(3_000m, downloadKbps: 1_000_000, cirKbps: 1_000_000), // $3.00 — the outlier
        };

        IReadOnlyList<CostOutlier> outliers = SpendCalculator.FindCostOutliers(peers, Today);

        outliers.Count.ShouldBe(1);
        outliers[0].CostPerMbps.ShouldBe(3m);
        outliers[0].RatioToMedian.ShouldBeGreaterThan(2m);
    }

    [Fact]
    public void CostOutliers_AreNotReported_WhenThePeerGroupIsTooSmallToJudge()
    {
        var pair = new List<TelecomService>
        {
            ServiceWith(1_000m, downloadKbps: 1_000_000, cirKbps: 1_000_000),
            ServiceWith(9_000m, downloadKbps: 1_000_000, cirKbps: 1_000_000),
        };

        SpendCalculator.FindCostOutliers(pair, Today).ShouldBeEmpty();
    }

    [Fact]
    public void MonthlySpendBy_GroupsCorrectly()
    {
        var services = new[]
        {
            ServiceWith(100m, type: ServiceType.Internet),
            ServiceWith(200m, type: ServiceType.Internet),
            ServiceWith(300m, type: ServiceType.MplsVpn, media: TransportMedia.Fiber),
        };

        var byType = SpendCalculator.MonthlySpendBy(services, service => service.ServiceType, Today);

        byType[ServiceType.Internet].Amount.ShouldBe(300m);
        byType[ServiceType.MplsVpn].Amount.ShouldBe(300m);
    }
}

public sealed class DiversityAnalyzerTests
{
    private static Location Site() => new()
    {
        LocationCode = "ST-0001",
        Name = "Test Site",
        TimeZoneId = "America/Chicago",
        PhysicalAddress = new Address { Line1 = "1 Test St", City = "Chicago" },
    };

    private static TelecomService Circuit(
        Guid carrierId, Guid? lastMileId = null, Guid? backboneId = null,
        ServiceRole role = ServiceRole.Primary) =>
        new()
        {
            ServiceType = ServiceType.Internet,
            Status = ServiceStatus.Active,
            ServiceRole = role,
            CarrierVendorId = carrierId,
            LastMileVendorId = lastMileId,
            UnderlyingNetworkOwnerVendorId = backboneId,
        };

    [Fact]
    public void SingleService_IsNoBackup()
    {
        DiversityAssessment assessment = DiversityAnalyzer.Assess(
            Site(), [Circuit(Guid.NewGuid())]);

        assessment.Verdict.ShouldBe(DiversityVerdict.NoBackup);
        assessment.Summary.ShouldContain("nothing to fail over to");
    }

    [Fact]
    public void NoServicesAtAll_IsNoBackup()
    {
        DiversityAnalyzer.Assess(Site(), []).Verdict.ShouldBe(DiversityVerdict.NoBackup);
    }

    /// <summary>
    /// The case this whole model exists for: two circuits, two different carriers on the
    /// invoices, one shared physical path. Collapse the vendor roles into a single field
    /// and this becomes undetectable.
    /// </summary>
    [Fact]
    public void TwoCarriers_SharingALastMileProvider_IsSharedRisk()
    {
        Guid everstream = Guid.NewGuid();

        var services = new List<TelecomService>
        {
            Circuit(Guid.NewGuid(), lastMileId: everstream),
            Circuit(Guid.NewGuid(), lastMileId: everstream, role: ServiceRole.Secondary),
        };

        DiversityAssessment assessment = DiversityAnalyzer.Assess(Site(), services);

        assessment.Verdict.ShouldBe(DiversityVerdict.SharedRisk);
        assessment.Risks.ShouldContain(risk => risk.DependencyType == DependencyType.SharedLastMile);
        assessment.Risks.ShouldContain(risk => risk.Description.Contains("whatever the carriers on the invoices say"));
    }

    [Fact]
    public void SharedBackbone_IsSharedRisk()
    {
        Guid backbone = Guid.NewGuid();

        var services = new List<TelecomService>
        {
            Circuit(Guid.NewGuid(), backboneId: backbone),
            Circuit(Guid.NewGuid(), backboneId: backbone, role: ServiceRole.Secondary),
        };

        DiversityAnalyzer.Assess(Site(), services).Verdict.ShouldBe(DiversityVerdict.SharedRisk);
    }

    /// <summary>
    /// A merely <i>suspected</i> dependency still counts as a risk. Optimism here is
    /// expensive, and the cost lands during an outage.
    /// </summary>
    [Fact]
    public void SuspectedDependency_CountsAsRisk()
    {
        TelecomService primary = Circuit(Guid.NewGuid());
        TelecomService backup = Circuit(Guid.NewGuid(), role: ServiceRole.Secondary);

        backup.Dependencies.Add(new ServiceDependency
        {
            ServiceId = backup.Id,
            DependsOnServiceId = primary.Id,
            DependencyType = DependencyType.SharedConduit,
            Confidence = DependencyConfidence.Suspected,
        });

        DiversityAssessment assessment = DiversityAnalyzer.Assess(Site(), [primary, backup]);

        assessment.Verdict.ShouldBe(DiversityVerdict.SharedRisk);
    }

    [Fact]
    public void RuledOutDependency_DoesNotCountAsRisk()
    {
        TelecomService primary = Circuit(Guid.NewGuid());
        TelecomService backup = Circuit(Guid.NewGuid(), role: ServiceRole.Secondary);

        backup.Dependencies.Add(new ServiceDependency
        {
            ServiceId = backup.Id,
            DependsOnServiceId = primary.Id,
            DependencyType = DependencyType.SharedBuildingEntrance,
            Confidence = DependencyConfidence.RuledOut,
            Evidence = "Diversity letters from both carriers; separate entrance facilities verified on site.",
        });

        DiversityAssessment assessment = DiversityAnalyzer.Assess(Site(), [primary, backup]);

        assessment.Verdict.ShouldBe(DiversityVerdict.Diverse);
    }

    /// <summary>
    /// "We have not checked" is reported separately from "we checked and it is fine".
    /// Conflating them is how an unverified assumption becomes an assurance.
    /// </summary>
    [Fact]
    public void TwoIndependentServices_WithNoAssessmentRecorded_AreUnassessed()
    {
        var services = new List<TelecomService>
        {
            Circuit(Guid.NewGuid()),
            Circuit(Guid.NewGuid(), role: ServiceRole.Secondary),
        };

        DiversityAssessment assessment = DiversityAnalyzer.Assess(Site(), services);

        assessment.Verdict.ShouldBe(DiversityVerdict.Unassessed);
        assessment.Summary.ShouldContain("not the same as absent");
    }

    [Fact]
    public void SameCarrierForBothCircuits_IsFlaggedButExplainedAsCheckable()
    {
        Guid carrier = Guid.NewGuid();

        var services = new List<TelecomService>
        {
            Circuit(carrier),
            Circuit(carrier, role: ServiceRole.Secondary),
        };

        DiversityAssessment assessment = DiversityAnalyzer.Assess(Site(), services);

        assessment.Verdict.ShouldBe(DiversityVerdict.SharedRisk);
        assessment.Risks.ShouldContain(risk => risk.Description.Contains("diversity letter"));
    }

    [Fact]
    public void DisconnectedServices_DoNotCountTowardBackup()
    {
        TelecomService live = Circuit(Guid.NewGuid());
        TelecomService dead = Circuit(Guid.NewGuid(), role: ServiceRole.Secondary);
        dead.Status = ServiceStatus.Disconnected;

        DiversityAnalyzer.Assess(Site(), [live, dead]).Verdict.ShouldBe(DiversityVerdict.NoBackup);
    }
}

public sealed class ValueObjectTests
{
    [Fact]
    public void AddingDifferentCurrencies_Throws()
    {
        // The failure mode this prevents is a spend report that quietly sums USD and CAD
        // and produces a number that looks entirely plausible.
        Money usd = Money.Usd(100m);
        var cad = new Money(100m, "CAD");

        Should.Throw<InvalidOperationException>(() => { _ = usd + cad; })
            .Message.ShouldContain("Convert to a common currency");
    }

    [Fact]
    public void SummingAnEmptySequence_ReturnsZeroInTheFallbackCurrency()
    {
        Money total = Money.Sum([], "GBP");

        total.Amount.ShouldBe(0m);
        total.CurrencyCode.ShouldBe("GBP");
    }

    [Theory]
    [InlineData(0, "—")]
    [InlineData(512, "512 kbps")]
    [InlineData(1_500, "1.5 Mbps")]
    [InlineData(100_000, "100 Mbps")]
    [InlineData(1_000_000, "1 Gbps")]
    [InlineData(10_000_000, "10 Gbps")]
    public void Bandwidth_FormatsInTheUnitThatReadsBest(int kbps, string expected)
    {
        Bandwidth.FromKbps(kbps).ToString().ShouldBe(expected);
    }

    [Fact]
    public void Bandwidth_UsesDecimalMultiples_AsCarriersDo()
    {
        // 1 Gbps is 1,000,000 kbps, not 1,048,576. Carrier marketing is decimal, and a
        // binary interpretation here would put every speed figure slightly out.
        Bandwidth.FromGbps(1).Kbps.ShouldBe(1_000_000);
        Bandwidth.FromMbps(100).Kbps.ShouldBe(100_000);
    }

    [Fact]
    public void SequentialGuids_SortInCreationOrder_UnderSqlServerComparisonRules()
    {
        // SQL Server orders uniqueidentifier by the last six bytes, most significant first.
        // Random GUIDs as a clustered key cause a page split on every insert.
        var first = SequentialGuid.Create(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = SequentialGuid.Create(new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));

        byte[] firstBytes = first.ToByteArray();
        byte[] secondBytes = second.ToByteArray();

        // Compare the six bytes SQL Server sorts on first.
        int comparison = 0;
        for (int index = 10; index < 16 && comparison == 0; index++)
        {
            comparison = firstBytes[index].CompareTo(secondBytes[index]);
        }

        comparison.ShouldBeLessThan(0);
    }
}
