using System.Globalization;

namespace NeoReports.Sources.Join.Pro;

/// <summary>
/// Compares the boxed key values of positional <see cref="Abstractions.ReportRecord"/> rows for the
/// dynamic merge-join. <c>null</c> sorts first; two numbers compare numerically even when their CLR
/// types differ (e.g. <see cref="int"/> vs <see cref="long"/> from different databases); otherwise
/// same-typed <see cref="IComparable"/> values compare directly, with an invariant-string fallback.
/// </summary>
internal sealed class ReportKeyComparer : IComparer<object?>
{
    public static readonly ReportKeyComparer Instance = new();

    private ReportKeyComparer()
    {
    }

    public int Compare(object? x, object? y)
    {
        if (x is null)
            return y is null ? 0 : -1;
        if (y is null)
            return 1;

        if (IsNumeric(x) && IsNumeric(y))
        {
            return Convert.ToDecimal(x, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDecimal(y, CultureInfo.InvariantCulture));
        }

        if (x.GetType() == y.GetType() && x is IComparable comparable)
            return comparable.CompareTo(y);

        return string.CompareOrdinal(
            Convert.ToString(x, CultureInfo.InvariantCulture),
            Convert.ToString(y, CultureInfo.InvariantCulture));
    }

    private static bool IsNumeric(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
