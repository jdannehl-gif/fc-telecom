using FcTelecom.Domain.Common;
using FcTelecom.Domain.Services;
using FcTelecom.Domain.Vendors;

namespace FcTelecom.Domain.Financials;

public enum InvoiceStatus { Imported = 1, Reconciled = 2, Disputed = 3, Approved = 4, Paid = 5 }

public class Invoice : AuditableEntity
{
    public Guid VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;

    public Guid? VendorAccountId { get; set; }
    public VendorAccount? VendorAccount { get; set; }

    public required string InvoiceNumber { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public DateOnly? PeriodStart { get; set; }
    public DateOnly? PeriodEnd { get; set; }

    public decimal TotalAmount { get; set; }
    public string CurrencyCode { get; set; } = Money.DefaultCurrency;
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Imported;

    public Guid? ImportBatchId { get; set; }
    public ImportBatch? ImportBatch { get; set; }

    public string? Notes { get; set; }

    public ICollection<InvoiceLine> Lines { get; set; } = [];

    public Money Total => new(TotalAmount, CurrencyCode);

    /// <summary>Sum of line-level variance. The number Finance actually wants to see.</summary>
    public Money TotalVariance => Money.Sum(
        Lines.Where(line => line.VarianceAmount.HasValue)
             .Select(line => new Money(line.VarianceAmount!.Value, CurrencyCode)),
        CurrencyCode);

    public bool HasUnmatchedLines => Lines.Any(line => line.MatchStatus == LineMatchStatus.Unmatched);
}

public enum ChargeCategory { Recurring = 1, OneTime = 2, Tax = 3, Fee = 4, Usage = 5, Credit = 6, Unknown = 99 }

public enum LineMatchStatus
{
    Unmatched = 1,
    AutoMatched = 2,
    ManuallyMatched = 3,

    /// <summary>
    /// Matched to nothing because nothing matches — you are being billed for a service
    /// that is not in the inventory. This is the "disconnected but still billed" detector,
    /// and it is usually the single largest recoverable amount in a telecom estate.
    /// </summary>
    NoServiceExists = 4,

    /// <summary>Reviewed and deliberately excluded from reconciliation.</summary>
    Ignored = 5,
}

public class InvoiceLine : BaseEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice Invoice { get; set; } = null!;

    public Guid? ServiceId { get; set; }
    public TelecomService? Service { get; set; }

    /// <summary>
    /// The circuit reference exactly as printed on the bill, kept before any matching.
    /// When a carrier renames a circuit mid-contract — which happens after every
    /// acquisition — this column is the only way to reconstruct why the match broke.
    /// </summary>
    public string? RawCircuitReference { get; set; }

    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public ChargeCategory ChargeCategory { get; set; } = ChargeCategory.Unknown;
    public LineMatchStatus MatchStatus { get; set; } = LineMatchStatus.Unmatched;

    /// <summary>
    /// What we expected, derived from the effective-dated <see cref="ServiceCost"/> for
    /// the billing period. Null when the line is unmatched or the service had no cost record.
    /// </summary>
    public decimal? ExpectedAmount { get; set; }

    public decimal? VarianceAmount { get; set; }
    public decimal? VariancePercent { get; set; }

    /// <summary>
    /// Recomputes variance against the expected amount.
    /// </summary>
    /// <remarks>
    /// This raises a variance; it never writes back to <see cref="ServiceCost"/>. Silently
    /// updating the expected cost to match the invoice is how a 4% annual creep becomes
    /// invisible — which is exactly the waste this product is supposed to surface.
    /// </remarks>
    public void RecalculateVariance()
    {
        if (ExpectedAmount is null)
        {
            VarianceAmount = null;
            VariancePercent = null;
            return;
        }

        VarianceAmount = Amount - ExpectedAmount.Value;
        VariancePercent = ExpectedAmount.Value == 0m
            ? null
            : Math.Round(VarianceAmount.Value / ExpectedAmount.Value * 100m, 2);
    }

    public bool ExceedsVarianceThreshold(decimal thresholdPercent) =>
        VariancePercent.HasValue && Math.Abs(VariancePercent.Value) >= thresholdPercent;
}

public enum ImportBatchType { Locations = 1, Vendors = 2, Services = 3, Costs = 4, Contracts = 5, Invoices = 6 }

public enum ImportBatchStatus { Parsing = 1, Preview = 2, Committed = 3, Failed = 4, Discarded = 5 }

/// <summary>
/// One upload. Retained even when discarded, so "who tried to import what" is answerable.
/// </summary>
public class ImportBatch : BaseEntity
{
    public ImportBatchType BatchType { get; set; }
    public required string FileName { get; set; }
    public string? BlobPath { get; set; }

    public Guid UploadedByUserId { get; set; }
    public DateTime UploadedUtc { get; set; }

    /// <summary>
    /// True until the user approves the preview. A dry run parses, validates, and
    /// detects duplicates, and writes nothing to the inventory tables.
    /// </summary>
    public bool IsDryRun { get; set; } = true;

    public ImportBatchStatus Status { get; set; } = ImportBatchStatus.Parsing;

    public int RowCount { get; set; }
    public int CreateCount { get; set; }
    public int UpdateCount { get; set; }
    public int ErrorCount { get; set; }
    public int DuplicateCount { get; set; }

    public string? SummaryJson { get; set; }

    public ICollection<ImportRow> Rows { get; set; } = [];

    public bool CanCommit => Status == ImportBatchStatus.Preview && ErrorCount == 0;
}

public enum ImportRowStatus { Pending = 1, WillCreate = 2, WillUpdate = 3, Duplicate = 4, Error = 5, Committed = 6, Skipped = 7 }

public class ImportRow
{
    public long Id { get; set; }

    public Guid ImportBatchId { get; set; }
    public ImportBatch ImportBatch { get; set; } = null!;

    public int RowNumber { get; set; }

    /// <summary>The source row verbatim, so an error can be explained against what was uploaded.</summary>
    public string RawJson { get; set; } = "{}";

    public ImportRowStatus Status { get; set; } = ImportRowStatus.Pending;

    /// <summary>Newline-separated, human-readable. These go straight into the error export.</summary>
    public string? ErrorMessages { get; set; }

    public Guid? ResultingEntityId { get; set; }
}
