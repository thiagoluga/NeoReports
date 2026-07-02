using System.Text.RegularExpressions;

namespace NeoReports.Core.Configuration;

/// <summary>
/// Validates names used for runtime-registered (dynamic) reports. A dynamic report name becomes a
/// file name on disk (<see cref="FileReportConfigStore"/>) and a URL segment (the AspNetCore
/// endpoints), so an invalid name is always rejected — never sanitized — to keep path traversal
/// structurally impossible.
/// </summary>
public static class DynamicReportName
{
    /// <summary>The validation pattern: starts with a letter, then up to 99 letters/digits/underscore/hyphen.</summary>
    public const string Pattern = "^[a-zA-Z][a-zA-Z0-9_-]{0,99}$";

    private static readonly Regex Regex = new(Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="name"/> matches <see cref="Pattern"/>.</summary>
    /// <param name="name">The candidate report name.</param>
    public static bool IsValid(string? name) => name is not null && Regex.IsMatch(name);
}
