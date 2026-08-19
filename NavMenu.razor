namespace FcTelecom.Domain.Common;

/// <summary>
/// Marks an entity that records who created and last modified it, and when.
/// Populated automatically by <c>AuditSaveChangesInterceptor</c> — never set by hand.
/// </summary>
public interface IAuditable
{
    DateTime CreatedUtc { get; set; }
    Guid? CreatedByUserId { get; set; }
    DateTime? ModifiedUtc { get; set; }
    Guid? ModifiedByUserId { get; set; }
}

/// <summary>
/// Marks an entity that is archived rather than deleted. A global query filter excludes
/// archived rows; <c>IncludeArchived()</c> opts back in.
/// </summary>
public interface ISoftDeletable
{
    bool IsArchived { get; set; }
    DateTime? ArchivedUtc { get; set; }
    Guid? ArchivedByUserId { get; set; }
}

/// <summary>
/// Marks a table that is append-only: rows are inserted and read, never updated or deleted.
/// The audit interceptor throws if it sees a modification to one of these, and the
/// application's SQL principal is not granted UPDATE or DELETE on them either.
/// Two locks, because application logic can be defeated by a bug.
/// </summary>
public interface IImmutableRecord;

/// <summary>
/// Base for entities that carry a surrogate GUID key.
/// </summary>
/// <remarks>
/// GUIDs rather than identity integers because these records are imported from CSV,
/// synchronised to IT Glue, and referenced from a probe agent that may be offline when
/// it needs to name one. A key that can be generated client-side without a round trip
/// is worth the extra 12 bytes.
/// <para>
/// The value is generated as a sequential GUID (see <see cref="SequentialGuid"/>) so
/// clustered index fragmentation stays close to what an identity column would give.
/// </para>
/// </remarks>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = SequentialGuid.Create();

    /// <summary>Optimistic concurrency token. Mapped to SQL Server <c>rowversion</c>.</summary>
    public byte[]? RowVersion { get; set; }

    private readonly List<DomainEvent> _domainEvents = [];

    /// <summary>
    /// Events raised by this entity, drained and written to the outbox by the
    /// <c>ApplicationDbContext</c> inside the same transaction as the state change.
    /// </summary>
    public IReadOnlyCollection<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void Raise(DomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>
/// Base for entities that are audited and archivable — the common case.
/// </summary>
public abstract class AuditableEntity : BaseEntity, IAuditable, ISoftDeletable
{
    public DateTime CreatedUtc { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime? ModifiedUtc { get; set; }
    public Guid? ModifiedByUserId { get; set; }

    public bool IsArchived { get; set; }
    public DateTime? ArchivedUtc { get; set; }
    public Guid? ArchivedByUserId { get; set; }
}

/// <summary>
/// Something that happened in the domain and that someone outside the domain may care about.
/// </summary>
public abstract record DomainEvent
{
    /// <summary>
    /// Stable key used to suppress duplicate notifications. Two events with the same
    /// key produce at most one outbound message, which is what makes a redeploy
    /// mid-drain, a retry storm, or a duplicated timer fire safe.
    /// </summary>
    public abstract string DedupeKey { get; }
}

/// <summary>
/// Generates GUIDs that sort in creation order under SQL Server's <c>uniqueidentifier</c>
/// comparison rules, which order the last six bytes most significantly.
/// </summary>
/// <remarks>
/// Random GUIDs as a clustered key cause page splits on every insert. This puts a
/// timestamp in the bytes SQL Server sorts on first, so inserts append rather than
/// scatter, while the leading bytes stay random enough that keys are not guessable.
/// </remarks>
public static class SequentialGuid
{
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static Guid Create() => Create(DateTime.UtcNow);

    public static Guid Create(DateTime utcNow)
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes[..10]);

        // 100-nanosecond ticks since the epoch, truncated to six bytes. Six bytes of
        // 100ns ticks covers roughly 892 years, which is comfortably longer than this
        // application will be in service.
        long ticks = (utcNow - Epoch).Ticks;
        bytes[10] = (byte)(ticks >> 40);
        bytes[11] = (byte)(ticks >> 32);
        bytes[12] = (byte)(ticks >> 24);
        bytes[13] = (byte)(ticks >> 16);
        bytes[14] = (byte)(ticks >> 8);
        bytes[15] = (byte)ticks;

        return new Guid(bytes);
    }
}
