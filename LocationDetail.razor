using FcTelecom.Domain.Common;
using FcTelecom.Domain.Organization;
using FcTelecom.Domain.Services;

namespace FcTelecom.Domain.Financials;

public enum BillingFrequency { Monthly = 1, Quarterly = 3, SemiAnnual = 6, Annual = 12 }

public enum CostSource { Contract = 1, Invoice = 2, Quote = 3, Manual = 4, Import = 5 }

public enum AllocationMethod { SingleCostCenter = 1, SplitByPercent = 2, SplitByHeadcount = 3, Corporate = 4 }

/// <summary>
/// What a service costs, over a period. <b>Append-only and effective-dated.</b>
/// </summary>
/// <remarks>
/// A price change closes the current row by setting <see cref="EffectiveTo"/> and inserts
/// a new one. Nothing is ever updated in place, and the UI action is labelled
/// "Record a price change" rather than "Edit cost" so the behaviour is obvious before
/// the user commits to it.
/// <para>
/// Two database constraints enforce this rather than leaving it to discipline: a check
/// constraint preventing overlapping <c>[EffectiveFrom, EffectiveTo)</c> ranges per
/// service, and a filtered unique index allowing at most one open row (<c>EffectiveTo IS NULL</c>)
/// per service.
/// </para>
/// <para>
/// The payoff is that every historical report is reproducible. "What did we pay at this
/// location in March 2024" has one answer, a year from now, regardless of how many price
/// changes happened since.
/// </para>
/// </remarks>
public class ServiceCost : BaseEntity, IAuditable, IImmutableRecord
{
    public Guid ServiceId { get; set; }
    public TelecomService Service { get; set; } = null!;

    public DateOnly EffectiveFrom { get; set; }

    /// <summary>Null means this is the current cost.</summary>
    public DateOnly? EffectiveTo { get; set; }

    public decimal MonthlyRecurringCharge { get; set; }
    public decimal TaxesAndFees { get; set; }
    public decimal EquipmentRental { get; set; }

    /// <summary>Budgeted variable usage — overage, per-minute voice. Not actual usage.</summary>
    public decimal EstimatedVariableUsage { get; set; }

    public string CurrencyCode { get; set; } = Money.DefaultCurrency;

    public BillingFrequency BillingFrequency { get; set; } = BillingFrequency.Monthly;

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public string? GlCode { get; set; }
    public AllocationMethod AllocationMethod { get; set; } = AllocationMethod.SingleCostCenter;
    public CostSource Source { get; set; } = CostSource.Manual;
    public string? Notes { get; set; }

    // IAuditable — note there is no ISoftDeletable. Cost history is never archived.
    public DateTime CreatedUtc { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    public ICollection<CostAllocation> Allocations { get; set; } = [];

    /// <summary>Everything billed on the recurring cycle, in the cycle's own units.</summary>
    public Money TotalPerCycle => new(
        MonthlyRecurringCharge + TaxesAndFees + EquipmentRental + EstimatedVariableUsage,
        CurrencyCode);

    /// <summary>
    /// Normalised to a monthly figure so services on different billing cycles can be
    /// summed. An annually billed circuit at $12,000 is $1,000/month here.
    /// </summary>
    public Money MonthlyEquivalent => TotalPerCycle / (int)BillingFrequency;

    public Money AnnualizedCost => MonthlyEquivalent * 12m;

    public bool IsCurrent => EffectiveTo is null;

    public bool AppliesOn(DateOnly date) =>
        EffectiveFrom <= date && (EffectiveTo is null || EffectiveTo >= date);
}

/// <summary>Splits one cost record across cost centres. Percentages must total 100.</summary>
public class CostAllocation
{
    public Guid Id { get; set; } = SequentialGuid.Create();

    public Guid ServiceCostId { get; set; }
    public ServiceCost ServiceCost { get; set; } = null!;

    public int CostCenterId { get; set; }
    public CostCenter CostCenter { get; set; } = null!;

    /// <summary>0–100. Validated to sum to exactly 100 across a cost record.</summary>
    public decimal Percent { get; set; }
}

public enum OneTimeChargeType
{
    Installation = 1, Equipment = 2, Expedite = 3, EarlyTermination = 4,
    Credit = 5, Restoration = 6, Other = 99,
}

/// <summary>A charge that happens once rather than recurring.</summary>
public class OneTimeCharge : AuditableEntity
{
    public Guid ServiceId { get; set; }
    public TelecomService Service { get; set; } = null!;

    public OneTimeChargeType ChargeType { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = Money.DefaultCurrency;
    public DateOnly IncurredOn { get; set; }

    public Guid? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public string? Description { get; set; }

    /// <summary>Credits are negative amounts. Kept as one field so the maths just works.</summary>
    public Money Value => new(ChargeType == OneTimeChargeType.Credit ? -Math.Abs(Amount) : Amount, CurrencyCode);
}
