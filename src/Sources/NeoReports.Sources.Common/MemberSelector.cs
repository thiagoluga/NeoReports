using System.Linq.Expressions;

namespace NeoReports.Sources.Common;

/// <summary>Extracts a simple member name from a keyset key selector (e.g. <c>v =&gt; v.Id</c>).</summary>
public static class MemberSelector
{
    /// <summary>Returns the accessed member's name, or throws if <paramref name="selector"/> isn't a simple member access.</summary>
    public static string GetMemberName<T, TKey>(Expression<Func<T, TKey>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            body = unary.Operand;

        if (body is MemberExpression { Member.Name: { } name })
            return name;

        throw new ArgumentException(
            "Keyset selector must be a simple member access (e.g. v => v.Id).", nameof(selector));
    }
}
