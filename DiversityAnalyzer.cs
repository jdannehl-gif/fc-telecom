using FcTelecom.Domain.Common;
using FcTelecom.Domain.Platform;
using FcTelecom.Domain.Services;
using FcTelecom.Domain.Vendors;

namespace FcTelecom.Domain.Contracts;

public enum RenewalType
{
    /// <summary>Ends on the end date. Nothing happens automatically.</summary>
    None = 1,

    /// <summary>Renews for another full term unless notice is given. The expensive one.</summary>
    AutoRenew = 2,

    /// <summary>Continues month to month after the initial term.</summary>
    EvergreenMonthToMonth = 3,

    /// <summary>Requires an affirmative new agreement.</summary>
    NegotiatedRenewal = 4,

    /// <summary>The paperwork does not say, or nobody has read it yet. Treated as risk.</summary>
    Unknown = 99,
}

public enum ContractStatus
{
    Draft = 1, Active = 2, InNoticePeriod = 3, Terminating = 4,
    Expired = 5, Renewed = 6, Cancelled = 7,
}

public enum EscalatorCadence { None = 1, Annual = 2, AtRenewal = 3, Other = 99 }

/// <summary>
/// A commercial agreement with a vendor covering one or more services.
/// </summary>
/// <remarks>
/// Three distinct dates live on this entity and conflating any two of them is the most
/// expensive modelling error in this domain:
/// <list type="number">
/// <item><see cref="EndDate"/> — when the paper ends.</item>
/// <item><see cref="ContractService.ServiceEndDate"/> — when a particular circuit's term
/// ends, which is often staggered because circuits were added mid-term.</item>
/// <item><see cref="NoticeDeadlineDate"/> — the date that actually matters, because
/// missing it triggers a renewal nobody wanted.</item>
/// </list>
/// </remarks>
public class Contract : AuditableEntity
{
    public required string ContractNumber { get; set; }

    public Guid VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;

    public string? Description { get; set; }

    public DateOnly StartDate { get; set; }
    public int InitialTermMonths { get; set; }
    public DateOnly? EndDate { get; set; }

    public RenewalType RenewalType { get; set; } = RenewalType.Unknown;
    public int? RenewalTermMonths { get; set; }
    public bool AutoRenew { get; set; }

    /// <summary>Days of notice required to prevent renewal.</summary>
    public int? NoticePeriodDays { get; set; }

    /// <summary>
    /// Computed by the system from <see cref="EndDate"/> minus <see cref="NoticePeriodDays"/>.
    /// Advisory only.
    /// </summary>
    public DateOnly? ProposedNoticeDeadlineDate { get; set; }

    /// <summary>
    /// The date alerts actually use. Falls back to the proposal when unconfirmed.
    /// </summary>
    /// <remarks>
    /// Real telecom contracts say things like "ninety days prior to the end of the
    /// then-current term", where "then-current term" is itself disputed after an
    /// auto-renewal. Computing this silently produces a number nobody trusts, which
    /// defeats the purpose. So the system proposes and a person confirms — and an
    /// unconfirmed deadline still raises alerts, labelled as unconfirmed, because
    /// suppressing an alert on a technicality is worse than sending an uncertain one.
    /// </remarks>
    public DateOnly? NoticeDeadlineDate { get; set; }

    public bool NoticeDeadlineConfirmed { get; set; }
    public Guid? NoticeDeadlineConfirmedByUserId { get; set; }
    public DateTime? NoticeDeadlineConfirmedUtc { get; set; }

    public string? EarlyTerminationTerms { get; set; }

    /// <summary>Free text. ETF formulas vary too much between carriers to model.</summary>
    public string? EarlyTerminationFormula { get; set; }

    public decimal? MinimumCommitmentAmount { get; set; }
    public string CurrencyCode { get; set; } = Money.DefaultCurrency;

    public decimal? PriceEscalatorPercent { get; set; }
    public EscalatorCadence EscalatorCadence { get; set; } = EscalatorCadence.None;

    public string? SlaSummary { get; set; }

    public Guid? ContractOwnerUserId { get; set; }
    public AppUser? ContractOwner { get; set; }

    public ContractStatus Status { get; set; } = ContractStatus.Active;
    public string? Notes { get; set; }

    public ICollection<ContractService> Services { get; set; } = [];
    public ICollection<ContractAmendment> Amendments { get; set; } = [];
    public ICollection<ContractAlert> Alerts { get; set; } = [];

    /// <summary>The deadline to act on — confirmed if there is one, otherwise the proposal.</summary>
    public DateOnly? EffectiveNoticeDeadline => NoticeDeadlineDate ?? ProposedNoticeDeadlineDate;

    /// <summary>
    /// True when the terms needed to manage this contract are missing. These are the
    /// records that quietly auto-renew for a decade because nobody knew there was a
    /// deadline, so they get their own dashboard state rather than an empty cell.
    /// </summary>
    public bool HasIncompleteTerms =>
        EndDate is null || NoticePeriodDays is null || RenewalType == RenewalType.Unknown;

    public int? DaysUntilNoticeDeadline(DateOnly today) =>
        EffectiveNoticeDeadline is { } deadline ? deadline.DayNumber - today.DayNumber : null;

    public bool IsActionable => Status is ContractStatus.Active or ContractStatus.InNoticePeriod;
}

/// <summary>
/// Links a contract to a service. Many-to-many because one master agreement covers forty
/// circuits and one circuit may be covered by a master agreement plus an SLA addendum.
/// </summary>
public class ContractService
{
    public Guid ContractId { get; set; }
    public Contract Contract { get; set; } = null!;

    public Guid ServiceId { get; set; }
    public TelecomService Service { get; set; } = null!;

    /// <summary>May differ from the contract's end date when this circuit was added mid-term.</summary>
    public DateOnly? ServiceEndDate { get; set; }

    public decimal? ContractedMonthlyRate { get; set; }
    public string? Notes { get; set; }
}

public class ContractAmendment : AuditableEntity
{
    public Guid ContractId { get; set; }
    public Contract Contract { get; set; } = null!;

    public required string AmendmentNumber { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public string? Summary { get; set; }

    public Guid? DocumentId { get; set; }
    public Document? Document { get; set; }
}

public enum ContractAlertKind { NoticeDeadline = 1, ContractExpiry = 2, ServiceEnd = 3 }

public enum ContractAlertStatus { Pending = 1, Sent = 2, Failed = 3, Suppressed = 4 }

public enum AlertChannel { Email = 1, Teams = 2, Both = 3 }

/// <summary>
/// One alert, for one contract, at one threshold. The unique index on
/// (ContractId, AlertKind, ThresholdDays) is what stops the nightly job re-sending the
/// same 90-day warning every night for a month.
/// </summary>
public class ContractAlert : BaseEntity
{
    // Not IImmutableRecord: the row is created pending and updated when the send
    // succeeds or fails. Its uniqueness constraint, not its immutability, is what
    // guarantees one alert per threshold.

    public Guid ContractId { get; set; }
    public Contract Contract { get; set; } = null!;

    /// <summary>180, 120, 90, 60, or 30 — configurable.</summary>
    public int ThresholdDays { get; set; }

    public ContractAlertKind AlertKind { get; set; } = ContractAlertKind.NoticeDeadline;
    public DateOnly DueOn { get; set; }
    public DateTime? SentUtc { get; set; }
    public string? Recipients { get; set; }
    public AlertChannel Channel { get; set; } = AlertChannel.Email;
    public ContractAlertStatus Status { get; set; } = ContractAlertStatus.Pending;
    public string? FailureReason { get; set; }
}
