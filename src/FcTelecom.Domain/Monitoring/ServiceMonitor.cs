using FcTelecom.Domain.Common;
using FcTelecom.Domain.Organization;
using FcTelecom.Domain.Services;

namespace FcTelecom.Domain.Monitoring;

// Naming note: the design document calls this entity "Monitor". In code it is
// "ServiceMonitor", because System.Threading.Monitor is in scope in every file and a
// domain type of the same name makes `lock`-adjacent code genuinely confusing to read.

public enum CheckType { Icmp = 1, Tcp = 2, Http = 3, Https = 4, Dns = 5 }

public enum MonitorTargetKind
{
    /// <summary>A circuit's public IP or other external endpoint, watched from Azure.</summary>
    PublicCircuitEndpoint = 1,

    /// <summary>An always-on device inside the location, watched from a self-hosted agent.</summary>
    InternalLocationTarget = 2,
}

/// <summary>
/// What the internal target actually is. Ordered by how much a failure tells you.
/// </summary>
public enum InternalTargetKind
{
    /// <summary>Branch firewall LAN or management address. The preferred target.</summary>
    FirewallLanOrManagement = 1,

    /// <summary>The location's management VLAN gateway. Equally preferred.</summary>
    ManagementVlanGateway = 2,

    /// <summary>A core or distribution switch. Acceptable.</summary>
    CoreSwitch = 3,

    /// <summary>
    /// Anything else — a server, an AP controller. Usable, but the monitor is flagged as
    /// lower confidence because availability now depends on that device's own lifecycle.
    /// </summary>
    Other = 98,

    /// <summary>
    /// A workstation or printer. <b>Explicitly not a supported default.</b> Present so that
    /// an existing bad choice can be recorded and reported as a data-quality finding rather
    /// than silently trusted.
    /// </summary>
    NotSuitable = 99,
}

public enum DnsRecordType { A = 1, Aaaa = 2, Cname = 3, Mx = 4, Txt = 5 }

/// <summary>
/// The state a monitor is in, as decided by the correlation engine.
/// </summary>
public enum MonitorState
{
    /// <summary>
    /// No usable coverage. <b>Not the same as Up.</b> Time in this state is excluded from
    /// the availability denominator rather than counted as available — which is the single
    /// most common way uptime reporting quietly inflates itself.
    /// </summary>
    Unknown = 0,

    Up = 1,

    /// <summary>Failing, but below the failure threshold or without probe quorum. No alert, no outage.</summary>
    Suspect = 2,

    Down = 3,

    /// <summary>Succeeding again but not yet for long enough to close the outage.</summary>
    Recovering = 4,
}

/// <summary>
/// A single thing being checked: one circuit's public IP, or one internal target at a site.
/// </summary>
public class ServiceMonitor : AuditableEntity
{
    /// <summary>Null for a location-level internal target that is not tied to one circuit.</summary>
    public Guid? ServiceId { get; set; }
    public TelecomService? Service { get; set; }

    public Guid LocationId { get; set; }
    public Location Location { get; set; } = null!;

    public required string Name { get; set; }
    public CheckType CheckType { get; set; } = CheckType.Icmp;

    /// <summary>IP, hostname, or URL depending on <see cref="CheckType"/>.</summary>
    public required string Target { get; set; }

    public int? Port { get; set; }
    public int? ExpectedStatusCode { get; set; }
    public string? ExpectedContent { get; set; }
    public string? DnsQueryName { get; set; }
    public DnsRecordType? DnsRecordType { get; set; }

    public int IntervalSeconds { get; set; } = 60;
    public int TimeoutMs { get; set; } = 5_000;

    /// <summary>Consecutive failures required to leave <see cref="MonitorState.Up"/>. Debounce.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Consecutive successes required to close an outage.</summary>
    public int SuccessThreshold { get; set; } = 2;

    /// <summary>
    /// How many independent probes must agree before an outage is opened.
    /// </summary>
    /// <remarks>
    /// A single perspective cannot distinguish three situations: the circuit is down, the
    /// path between the observer and the circuit is down, or the observer itself is down.
    /// Two independent perspectives distinguish all three in most cases. A monitor with
    /// only one assigned probe still opens outages, but the UI flags it as reduced
    /// confidence and the rollups are marked accordingly.
    /// </remarks>
    public int RequiredProbeQuorum { get; set; } = 2;

    /// <summary>
    /// What this monitor is actually watching. Drives how a failure is interpreted.
    /// </summary>
    /// <remarks>
    /// Circuit reachability and internal location reachability answer different questions
    /// and fail for different reasons, so they are distinguished at the schema level rather
    /// than inferred. A public circuit endpoint going quiet is a transport question; an
    /// internal target going quiet while the circuits answer is a site, CPE, or VPN question.
    /// </remarks>
    public MonitorTargetKind TargetKind { get; set; } = MonitorTargetKind.PublicCircuitEndpoint;

    /// <summary>
    /// What kind of internal device this is, when <see cref="TargetKind"/> is
    /// <see cref="MonitorTargetKind.InternalLocationTarget"/>.
    /// </summary>
    /// <remarks>
    /// Recorded because the choice of internal target determines what a failure means.
    /// A firewall LAN or management address going quiet is a real signal; a workstation
    /// going quiet means somebody went home. Non-preferred targets are reported so the
    /// coverage figures can be read with the right amount of scepticism.
    /// </remarks>
    // Named ...DeviceKind rather than ...Kind so the property does not shadow its own enum
    // type inside this class — legal C#, but it turns every reference to a member of the
    // enum into a puzzle.
    public InternalTargetKind? InternalTargetDeviceKind { get; set; }

    /// <summary>
    /// True for a target behind your firewall. Internal targets are what let the engine
    /// tell a CPE failure apart from a transport failure — the carrier's edge answering
    /// while everything behind it is dark is the classic blind spot of monitoring a public IP.
    /// </summary>
    /// <remarks>
    /// A convenience projection over <see cref="TargetKind"/>, not a stored column. The
    /// entity configuration ignores it explicitly — a read/write property with no backing
    /// field would otherwise be mapped by convention and duplicate the same fact in two
    /// columns that can disagree.
    /// </remarks>
    public bool IsInternalTarget
    {
        get => TargetKind == MonitorTargetKind.InternalLocationTarget;
        set => TargetKind = value
            ? MonitorTargetKind.InternalLocationTarget
            : MonitorTargetKind.PublicCircuitEndpoint;
    }

    /// <summary>
    /// True when this monitor's internal target is not one of the preferred device kinds.
    /// Feeds the data-completeness report rather than blocking anything.
    /// </summary>
    public bool HasWeakInternalTarget =>
        TargetKind == MonitorTargetKind.InternalLocationTarget &&
        InternalTargetDeviceKind is null or Monitoring.InternalTargetKind.Other
            or Monitoring.InternalTargetKind.NotSuitable;

    public bool Enabled { get; set; } = true;

    public MonitorState CurrentState { get; set; } = MonitorState.Unknown;
    public DateTime? StateChangedUtc { get; set; }
    public DateTime? LastCheckedUtc { get; set; }

    public ICollection<MonitorProbeAssignment> ProbeAssignments { get; set; } = [];
    public ICollection<OutageEvent> Outages { get; set; } = [];

    public bool HasReducedConfidence => ProbeAssignments.Count < RequiredProbeQuorum;

    /// <summary>
    /// How long we will wait for a result before treating the monitor as uncovered.
    /// Three intervals plus the timeout tolerates two ordinary misses without declaring
    /// the coverage gone.
    /// </summary>
    public TimeSpan StalenessTolerance =>
        TimeSpan.FromSeconds(IntervalSeconds * 3) + TimeSpan.FromMilliseconds(TimeoutMs);
}

public enum ProbeKind
{
    /// <summary>Runs in Azure Functions. HTTP, TCP, DNS. Cannot send ICMP.</summary>
    AzureFunction = 1,

    /// <summary>Runs on your network. Full check capability including ICMP and internal targets.</summary>
    SelfHostedAgent = 2,

    /// <summary>Deterministic, for development and demos.</summary>
    Simulated = 3,

    /// <summary>
    /// Results ingested from another platform (The Dude syslog, a webhook). Advisory:
    /// can raise suspicion, cannot alone confirm an outage.
    /// </summary>
    ExternalIngest = 4,
}

public enum ProbeStatus { Healthy = 1, Degraded = 2, Offline = 3, Disabled = 4 }

/// <summary>
/// How a self-hosted agent is hosted. The agent is one cross-platform .NET Worker; only
/// the hosting wrapper differs.
/// </summary>
public enum AgentHostKind
{
    /// <summary>The initially documented and supported deployment.</summary>
    WindowsService = 1,

    SystemdUnit = 2,
    Container = 3,
}

/// <summary>A vantage point that executes checks and reports results.</summary>
public class Probe : AuditableEntity
{
    public required string Name { get; set; }
    public ProbeKind Kind { get; set; }

    /// <summary>Where a self-hosted agent physically sits. Null for cloud probes.</summary>
    public Guid? LocationId { get; set; }
    public Location? Location { get; set; }

    /// <summary>The Entra app registration object ID this agent authenticates as.</summary>
    public string? EntraAppObjectId { get; set; }

    /// <summary>
    /// The <b>name</b> of the Key Vault secret holding this agent's HMAC key — never the
    /// key itself. Each agent has its own key, so compromising one does not let an
    /// attacker forge another's results.
    /// </summary>
    public string? HmacKeyVaultSecretName { get; set; }

    public DateTime? LastHeartbeatUtc { get; set; }
    public string? AgentVersion { get; set; }
    public ProbeStatus Status { get; set; } = ProbeStatus.Healthy;

    /// <summary>
    /// What this probe shares its fate with — power feed, virtualization cluster, upstream
    /// circuit. Free text, e.g. "Dorchester DC / cluster-A / feed-1".
    /// </summary>
    /// <remarks>
    /// Two probes are only two perspectives if they can fail independently. Two agents on
    /// the same hypervisor cluster, behind the same UPS, or riding the same upstream circuit
    /// are one perspective wearing two hats — and the quorum rule will happily count them
    /// twice and declare a confident outage that is really a power event.
    /// <para>
    /// Recorded rather than enforced: the admin UI warns when every probe assigned to a
    /// monitor shares a failure domain, and the availability rollup notes it. Encoding it
    /// as a hard constraint would be wrong, because sometimes one perspective is all there is.
    /// </para>
    /// </remarks>
    public string? FailureDomain { get; set; }

    /// <summary>
    /// Deployment shape, for the onboarding runbook and version reporting.
    /// Windows Service is the initially supported method; the agent itself is a
    /// cross-platform .NET Worker, so systemd and container remain available.
    /// </summary>
    public AgentHostKind? HostKind { get; set; }

    public ICollection<MonitorProbeAssignment> MonitorAssignments { get; set; } = [];

    /// <summary>Advisory probes count toward suspicion but not toward outage quorum.</summary>
    public bool CountsTowardQuorum => Kind is not ProbeKind.ExternalIngest;

    public bool IsStale(DateTime utcNow, TimeSpan tolerance) =>
        LastHeartbeatUtc is null || utcNow - LastHeartbeatUtc.Value > tolerance;
}

public class MonitorProbeAssignment
{
    public Guid MonitorId { get; set; }
    public ServiceMonitor Monitor { get; set; } = null!;

    public Guid ProbeId { get; set; }
    public Probe Probe { get; set; } = null!;

    public bool Enabled { get; set; } = true;
}

public enum CheckOutcome
{
    Up = 1,
    Down = 2,
    Timeout = 3,

    /// <summary>The check could not be executed — DNS resolution failed, socket error.</summary>
    Error = 4,

    /// <summary>The probe explicitly reported that it could not determine a result.</summary>
    Unknown = 5,
}

/// <summary>
/// One check, by one probe, at one moment. Raw, high-volume, short-retention.
/// </summary>
/// <remarks>
/// Clustered on (MonitorId, ObservedAtUtc) so both the correlation read pattern and the
/// retention delete are range scans rather than table scans. Nothing reports directly
/// from this table — reporting reads the rollups.
/// </remarks>
public class CheckResult : IImmutableRecord
{
    public long Id { get; set; }

    public Guid MonitorId { get; set; }
    public ServiceMonitor Monitor { get; set; } = null!;

    public Guid ProbeId { get; set; }
    public Probe Probe { get; set; } = null!;

    /// <summary>
    /// When the check ran, as reported by the probe — not when it was received.
    /// An agent that buffered results through a disconnection uploads them with their
    /// original timestamps, which is what makes the availability maths correct afterwards.
    /// </summary>
    public DateTime ObservedAtUtc { get; set; }

    public DateTime ReceivedAtUtc { get; set; }
    public CheckOutcome Outcome { get; set; }
    public int? LatencyMs { get; set; }
    public decimal? PacketLossPercent { get; set; }
    public string? ErrorCode { get; set; }
    public string? Detail { get; set; }

    /// <summary>
    /// Measured clock difference between the probe and the server for this batch.
    /// Batches beyond tolerance are accepted but flagged, and the correlation engine
    /// treats their ordering as uncertain rather than silently trusting it.
    /// </summary>
    public int? ClockSkewSeconds { get; set; }

    public bool IsSuccess => Outcome == CheckOutcome.Up;

    public bool IsFailure => Outcome is CheckOutcome.Down or CheckOutcome.Timeout;
}
