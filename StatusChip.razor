namespace FcTelecom.Domain.Common;

/// <summary>
/// A bandwidth rate, stored as an integer number of kilobits per second.
/// </summary>
/// <remarks>
/// One unit, everywhere, named in the property. The alternative — a number plus a
/// separate unit column, or worse a string like "1 Gbps" — guarantees that someone
/// eventually compares 1000 to 1 and concludes a gigabit circuit is slower than a
/// megabit one.
/// <para>
/// Carrier marketing uses decimal multiples (1 Gbps = 1,000,000 kbps), not binary,
/// so that is what <see cref="FromMbps"/> and <see cref="FromGbps"/> do.
/// </para>
/// </remarks>
public readonly record struct Bandwidth(int Kbps) : IComparable<Bandwidth>
{
    public static readonly Bandwidth Zero = new(0);

    public static Bandwidth FromKbps(int kbps) => new(kbps);

    public static Bandwidth FromMbps(double mbps) => new((int)Math.Round(mbps * 1_000));

    public static Bandwidth FromGbps(double gbps) => new((int)Math.Round(gbps * 1_000_000));

    public double Mbps => Kbps / 1_000d;

    public double Gbps => Kbps / 1_000_000d;

    public int CompareTo(Bandwidth other) => Kbps.CompareTo(other.Kbps);

    public static bool operator <(Bandwidth left, Bandwidth right) => left.Kbps < right.Kbps;
    public static bool operator >(Bandwidth left, Bandwidth right) => left.Kbps > right.Kbps;
    public static bool operator <=(Bandwidth left, Bandwidth right) => left.Kbps <= right.Kbps;
    public static bool operator >=(Bandwidth left, Bandwidth right) => left.Kbps >= right.Kbps;

    /// <summary>Human-readable form, choosing the unit that reads best.</summary>
    public override string ToString() => Kbps switch
    {
        0 => "—",
        >= 1_000_000 when Kbps % 1_000_000 == 0 => $"{Kbps / 1_000_000} Gbps",
        >= 1_000_000 => $"{Gbps:0.##} Gbps",
        >= 1_000 when Kbps % 1_000 == 0 => $"{Kbps / 1_000} Mbps",
        >= 1_000 => $"{Mbps:0.##} Mbps",
        _ => $"{Kbps} kbps",
    };
}
