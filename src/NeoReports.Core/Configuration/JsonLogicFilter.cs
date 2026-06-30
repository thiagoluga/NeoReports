using System.Collections;
using System.Globalization;
using System.Text.Json;
using NeoReports.Abstractions;

namespace NeoReports.Core.Configuration;

/// <summary>
/// Compiles a <see href="https://jsonlogic.com">JsonLogic</see> expression into a
/// <see cref="Func{ReportRecord, Boolean}"/> filter over a positional <see cref="ReportRecord"/>.
/// <c>{"var": "Name"}</c> reads a column by name; the supported operators are <c>==</c>, <c>===</c>,
/// <c>!=</c>, <c>!==</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>and</c>, <c>or</c>,
/// <c>!</c>, <c>!!</c> and <c>in</c>. Unknown operators raise a <see cref="ConfigurationException"/>.
/// </summary>
public static class JsonLogicFilter
{
    /// <summary>Compiles a JsonLogic expression into a row predicate.</summary>
    /// <param name="expression">The JsonLogic expression as a JSON document.</param>
    /// <returns>A predicate that is <c>true</c> when the (truthy) expression matches the row.</returns>
    /// <exception cref="ConfigurationException">Thrown when the expression is malformed or uses an unsupported operator.</exception>
    public static Func<ReportRecord, bool> Compile(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ConfigurationException("The filter expression is empty.");

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(expression);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException($"Invalid JsonLogic filter: {ex.Message}", ex);
        }

        var evaluate = Build(root);
        return record => IsTruthy(evaluate(record));
    }

    private static Func<ReportRecord, object?> Build(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                return BuildOperation(node);
            case JsonValueKind.Array:
                var items = node.EnumerateArray().Select(Build).ToArray();
                return record => items.Select(f => f(record)).ToList();
            case JsonValueKind.String:
                var text = node.GetString();
                return _ => text;
            case JsonValueKind.Number:
                var number = node.TryGetInt64(out var l) ? l : node.GetDouble();
                return _ => number;
            case JsonValueKind.True:
                return _ => true;
            case JsonValueKind.False:
                return _ => false;
            default:
                return _ => null;
        }
    }

    private static Func<ReportRecord, object?> BuildOperation(JsonElement node)
    {
        // A JsonLogic operation is a single-key object: { "op": args }.
        var properties = node.EnumerateObject().ToArray();
        if (properties.Length != 1)
            throw new ConfigurationException("A JsonLogic operation must be an object with exactly one operator key.");

        var op = properties[0].Name;
        var value = properties[0].Value;
        var args = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(Build).ToArray()
            : new[] { Build(value) };

        return op switch
        {
            "var" => BuildVar(value),
            "==" => Binary(args, op, (a, b) => LooseEquals(a, b)),
            "!=" => Binary(args, op, (a, b) => !LooseEquals(a, b)),
            "===" => Binary(args, op, StrictEquals),
            "!==" => Binary(args, op, (a, b) => !StrictEquals(a, b)),
            ">" => Binary(args, op, (a, b) => Compare(a, b) > 0),
            ">=" => Binary(args, op, (a, b) => Compare(a, b) >= 0),
            "<" => Binary(args, op, (a, b) => Compare(a, b) < 0),
            "<=" => Binary(args, op, (a, b) => Compare(a, b) <= 0),
            "and" => record => EvaluateAnd(args, record),
            "or" => record => EvaluateOr(args, record),
            "!" => record => !IsTruthy(First(args)(record)),
            "!!" => record => IsTruthy(First(args)(record)),
            "in" => BuildIn(args, op),
            _ => throw new ConfigurationException($"Unsupported JsonLogic operator '{op}'."),
        };
    }

    private static Func<ReportRecord, object?> BuildVar(JsonElement value)
    {
        // {"var": "Name"} or {"var": ["Name", default]}.
        string? name;
        JsonElement defaultElement = default;
        var hasDefault = false;

        if (value.ValueKind == JsonValueKind.Array)
        {
            var parts = value.EnumerateArray().ToArray();
            name = parts.Length > 0 ? parts[0].GetString() : null;
            if (parts.Length > 1)
            {
                defaultElement = parts[1].Clone();
                hasDefault = true;
            }
        }
        else
        {
            name = value.GetString();
        }

        if (string.IsNullOrEmpty(name))
            throw new ConfigurationException("A JsonLogic 'var' requires a column name.");

        var column = name;
        var fallback = hasDefault ? Build(defaultElement) : null;
        return record => record.TryGet(column, out var v) ? v : fallback?.Invoke(record);
    }

    private static Func<ReportRecord, object?> BuildIn(Func<ReportRecord, object?>[] args, string op)
    {
        Require(args, 2, op);
        var needle = args[0];
        var haystack = args[1];
        return record =>
        {
            var n = needle(record);
            var h = haystack(record);
            if (h is string s)
                return n is not null && s.Contains(Stringify(n), StringComparison.Ordinal);
            if (h is IEnumerable enumerable and not string)
                return enumerable.Cast<object?>().Any(item => LooseEquals(item, n));
            return false;
        };
    }

    private static Func<ReportRecord, object?> Binary(
        Func<ReportRecord, object?>[] args, string op, Func<object?, object?, bool> predicate)
    {
        Require(args, 2, op);
        var left = args[0];
        var right = args[1];
        return record => predicate(left(record), right(record));
    }

    private static object EvaluateAnd(Func<ReportRecord, object?>[] args, ReportRecord record)
    {
        object? last = true;
        foreach (var arg in args)
        {
            last = arg(record);
            if (!IsTruthy(last))
                return last ?? false;
        }

        return last ?? false;
    }

    private static object EvaluateOr(Func<ReportRecord, object?>[] args, ReportRecord record)
    {
        object? last = false;
        foreach (var arg in args)
        {
            last = arg(record);
            if (IsTruthy(last))
                return last ?? false;
        }

        return last ?? false;
    }

    private static Func<ReportRecord, object?> First(Func<ReportRecord, object?>[] args) =>
        args.Length > 0 ? args[0] : (_ => null);

    private static void Require(Func<ReportRecord, object?>[] args, int count, string op)
    {
        if (args.Length != count)
            throw new ConfigurationException($"The JsonLogic operator '{op}' expects {count} arguments.");
    }

    private static bool LooseEquals(object? a, object? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (TryToDouble(a, out var da) && TryToDouble(b, out var db))
            return da.Equals(db);
        return string.Equals(Stringify(a), Stringify(b), StringComparison.Ordinal);
    }

    private static bool StrictEquals(object? a, object? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (TryToDouble(a, out var da) && TryToDouble(b, out var db))
            return da.Equals(db);
        return a.GetType() == b.GetType() && a.Equals(b);
    }

    private static int Compare(object? a, object? b)
    {
        if (TryToDouble(a, out var da) && TryToDouble(b, out var db))
            return da.CompareTo(db);
        if (a is DateTime dta && b is DateTime dtb)
            return dta.CompareTo(dtb);
        return string.CompareOrdinal(Stringify(a), Stringify(b));
    }

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        IEnumerable enumerable => enumerable.Cast<object?>().Any(),
        _ => !TryToDouble(value, out var d) || d != 0,
    };

    private static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static string Stringify(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
