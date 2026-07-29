using System.Globalization;
using System.Text;

namespace NeoReports.Destinations.Local;

/// <summary>
/// Expands path/key templates with <c>{name}</c>, <c>{ext}</c>, <c>{date}</c> and parameter tokens.
/// Supports an optional date format: <c>{date:yyyy-MM-dd}</c>. Unknown tokens are left untouched.
/// Shared by the Local and S3 destinations so token rules stay consistent.
/// </summary>
public static class PathTemplate
{
    /// <summary>
    /// Expands a template string. Recognized tokens:
    /// <list type="bullet">
    /// <item><c>{name}</c> — the report name</item>
    /// <item><c>{ext}</c> — the file extension (without dot)</item>
    /// <item><c>{date}</c> or <c>{date:format}</c> — <paramref name="timestamp"/> formatted (default <c>yyyy-MM-dd</c>)</item>
    /// <item><c>{paramName}</c> — a value from <paramref name="parameters"/></item>
    /// </list>
    /// </summary>
    /// <param name="template">The template to expand.</param>
    /// <param name="reportName">Value for <c>{name}</c>.</param>
    /// <param name="extension">Value for <c>{ext}</c> (without leading dot).</param>
    /// <param name="timestamp">Value used for <c>{date}</c> tokens.</param>
    /// <param name="parameters">Optional run-time parameters for <c>{paramName}</c> tokens.</param>
    /// <param name="sanitizeSubstitution">
    /// Optional guard applied to each <b>substituted value</b> — the <c>{name}</c>, <c>{ext}</c> and
    /// <c>{paramName}</c> values, which may be partly caller-controlled — receiving
    /// <c>(tokenName, value)</c> and returning the value to emit (or throwing to reject it). It is
    /// deliberately <b>not</b> applied to <c>{date}</c> (an author-controlled format string whose
    /// output may legitimately contain path separators for date-partitioned directories) nor to the
    /// template's own literal text. The Local destination uses this to stop a run-time parameter from
    /// injecting a directory separator or <c>..</c> and escaping the target directory; the S3
    /// destination passes none, since <c>/</c> is a legitimate key-hierarchy separator there.
    /// </param>
    public static string Expand(
        string template,
        string reportName,
        string extension,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, object?>? parameters = null,
        Func<string, string, string>? sanitizeSubstitution = null)
    {
        ArgumentNullException.ThrowIfNull(template);

        var result = new StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c != '{')
            {
                result.Append(c);
                i++;
                continue;
            }

            var end = template.IndexOf('}', i + 1);
            if (end < 0)
            {
                // Unterminated token: emit the rest verbatim.
                result.Append(template, i, template.Length - i);
                break;
            }

            var token = template.Substring(i + 1, end - i - 1);
            result.Append(ResolveToken(token, reportName, extension, timestamp, parameters, sanitizeSubstitution));
            i = end + 1;
        }

        return result.ToString();
    }

    private static string ResolveToken(
        string token,
        string reportName,
        string extension,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, object?>? parameters,
        Func<string, string, string>? sanitizeSubstitution)
    {
        var colon = token.IndexOf(':');
        var key = colon >= 0 ? token[..colon] : token;
        var format = colon >= 0 ? token[(colon + 1)..] : null;

        string Sanitize(string tokenName, string value) =>
            sanitizeSubstitution is null ? value : sanitizeSubstitution(tokenName, value);

        switch (key)
        {
            case "name":
                return Sanitize("name", reportName);
            case "ext":
                return Sanitize("ext", extension);
            case "date":
                // Not sanitized: the format is author-controlled and its output may legitimately
                // contain separators (e.g. {date:yyyy/MM/dd} for date-partitioned directories).
                return timestamp.ToString(
                    string.IsNullOrEmpty(format) ? "yyyy-MM-dd" : format,
                    CultureInfo.InvariantCulture);
            default:
                if (parameters is not null && parameters.TryGetValue(key, out var value) && value is not null)
                {
                    var text = value is IFormattable formattable && !string.IsNullOrEmpty(format)
                        ? formattable.ToString(format, CultureInfo.InvariantCulture)
                        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    return Sanitize(key, text);
                }

                // Unknown token: leave it untouched so misconfiguration is visible.
                return string.Concat("{", token, "}");
        }
    }
}
