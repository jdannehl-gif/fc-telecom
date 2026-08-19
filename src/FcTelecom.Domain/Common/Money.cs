using System.Globalization;

namespace FcTelecom.Domain.Common;

/// <summary>
/// An amount and the currency it is denominated in, kept together.
/// </summary>
/// <remarks>
/// Money is <c>decimal</c>, never <c>double</c>. Adding two amounts in different
/// currencies throws rather than silently producing a number that looks plausible and
/// is wrong — the failure mode we are avoiding is a spend report that quietly sums
/// USD and CAD.
/// <para>
/// Stored as two columns (<c>decimal(19,4)</c> + <c>char(3)</c>) via an EF Core owned
/// type, so it is queryable and reportable like any other column.
/// </para>
/// </remarks>
public readonly record struct Money(decimal Amount, string CurrencyCode) : IComparable<Money>
{
    public const string DefaultCurrency = "USD";

    public static Money Zero(string currencyCode = DefaultCurrency) => new(0m, currencyCode);

    public static Money Usd(decimal amount) => new(amount, DefaultCurrency);

    public bool IsZero => Amount == 0m;

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right, "add");
        return left with { Amount = left.Amount + right.Amount };
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right, "subtract");
        return left with { Amount = left.Amount - right.Amount };
    }

    public static Money operator *(Money value, decimal factor) =>
        value with { Amount = value.Amount * factor };

    public static Money operator /(Money value, decimal divisor) =>
        divisor == 0m
            ? throw new DivideByZeroException("Cannot divide a monetary amount by zero.")
            : value with { Amount = value.Amount / divisor };

    /// <summary>Rounds to whole cents using banker's rounding.</summary>
    public Money Round() => this with { Amount = Math.Round(Amount, 2, MidpointRounding.ToEven) };

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other, "compare");
        return Amount.CompareTo(other.Amount);
    }

    // A type that implements IComparable<T> but has no ordering operators is a trap: the
    // comparison a reader reaches for first is `a < b`, and without these that either fails
    // to compile or silently falls back to something else. Each one goes through CompareTo,
    // so all four inherit the mixed-currency guard rather than quietly comparing USD to CAD
    // by amount alone (CA1036).
    //
    // Equality is not defined here — `readonly record struct` already generates ==, !=,
    // Equals and GetHashCode over both Amount and CurrencyCode. Note the asymmetry that
    // follows: `usd10 == cad10` is false, while `usd10 < cad10` throws. That is deliberate.
    // Equality is a question you can always answer; ordering across currencies is not.
    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Sums a sequence, returning zero in <paramref name="fallbackCurrency"/> when empty.
    /// Throws if the sequence mixes currencies.
    /// </summary>
    public static Money Sum(IEnumerable<Money> values, string fallbackCurrency = DefaultCurrency)
    {
        ArgumentNullException.ThrowIfNull(values);

        Money? total = null;
        foreach (Money value in values)
        {
            total = total is null ? value : total.Value + value;
        }

        return total ?? Zero(fallbackCurrency);
    }

    private static void EnsureSameCurrency(Money left, Money right, string operation)
    {
        if (!string.Equals(left.CurrencyCode, right.CurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot {operation} {left.CurrencyCode} and {right.CurrencyCode}. " +
                "Convert to a common currency first — an implicit conversion here would " +
                "produce a spend figure that looks correct and is not.");
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Amount:N2} {CurrencyCode}");
}
