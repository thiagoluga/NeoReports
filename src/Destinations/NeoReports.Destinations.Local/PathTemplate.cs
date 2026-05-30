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
    public static string Expand(
        string template,
        string reportName,
        string extension,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, object?>? parameters = null)
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
            result.Append(ResolveToken(token, reportName, extension, timestamp, parameters));
            i = end + 1;
        }

        return result.ToString();
    }

    private static string ResolveToken(
        string token,
        string reportName,
        string extension,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        var colon = token.IndexOf(':');
        var key = colon >= 0 ? token[..colon] : token;
        var format = colon >= 0 ? token[(colon + 1)..] : null;

        switch (key)
        {
            case "name":
                return reportName;
            case "ext":
                return extension;
            case "date":
                return timestamp.ToString(
                    string.IsNullOrEmpty(format) ? "yyyy-MM-dd" : format,
                    CultureInfo.InvariantCulture);
            default:
                if (parameters is not null && parameters.TryGetValue(key, out var value) && value is not null)
                {
                    return value is IFormattable formattable && !string.IsNullOrEmpty(format)
                        ? formattable.ToString(format, CultureInfo.InvariantCulture)
                        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                }

                // Unknown token: leave it untouched so misconfiguration is visible.
                return string.Concat("{", token, "}");
        }
    }
}
