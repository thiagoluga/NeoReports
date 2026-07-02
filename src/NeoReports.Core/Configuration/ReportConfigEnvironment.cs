using System.Text.RegularExpressions;
using NeoReports.Abstractions;

namespace NeoReports.Core.Configuration;

/// <summary>
/// Resolves <c>${VAR}</c> placeholders in a <see cref="ReportConfig"/>'s property-bag string
/// values from environment variables, so secrets (connection strings, keys) never need to be
/// written into a persisted config document (<see cref="IReportConfigStore"/> always stores the
/// original, unresolved document).
/// </summary>
public static partial class ReportConfigEnvironment
{
    // Whole-value match only — no interpolation inside larger strings, no defaults syntax, no
    // recursion. Keeping this narrow keeps ReportConfigCompiler pure and the behavior predictable.
    [GeneratedRegex(@"^\$\{([A-Za-z_][A-Za-z0-9_]*)\}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    /// <summary>
    /// Returns a copy of <paramref name="config"/> with every string property value that is
    /// exactly <c>${NAME}</c> replaced by the environment variable <c>NAME</c>.
    /// </summary>
    /// <param name="config">The configuration to substitute.</param>
    /// <exception cref="ConfigurationException">Thrown when a referenced environment variable is not set.</exception>
    public static ReportConfig Substitute(ReportConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        SourceConfig source = config.Source with { Properties = SubstituteProperties(config.Source.Properties) };

        OutputConfig[] outputs = config.Outputs
            .Select(output => output with { Properties = SubstituteProperties(output.Properties) })
            .ToArray();

        IReadOnlyList<DestinationConfig>? destinations = config.Destinations?
            .Select(destination => destination with { Properties = SubstituteProperties(destination.Properties) })
            .ToArray();

        return config with { Source = source, Outputs = outputs, Destinations = destinations };
    }

    private static IReadOnlyDictionary<string, object?>? SubstituteProperties(
        IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null || properties.Count == 0)
            return properties;

        var result = new Dictionary<string, object?>(properties.Count, StringComparer.Ordinal);
        foreach ((string key, object? value) in properties)
            result[key] = SubstituteValue(key, value);

        return result;
    }

    private static object? SubstituteValue(string propertyKey, object? value)
    {
        if (value is not string text)
            return value;

        Match match = PlaceholderPattern().Match(text);
        if (!match.Success)
            return value;

        string variableName = match.Groups[1].Value;
        string? resolved = Environment.GetEnvironmentVariable(variableName);
        if (resolved is null)
        {
            throw new ConfigurationException(
                $"Environment variable '{variableName}' referenced by property '{propertyKey}' is not set.");
        }

        return resolved;
    }
}
