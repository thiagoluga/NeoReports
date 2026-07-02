using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>Epic D / D1: <see cref="ReportConfigEnvironment.Substitute"/> whole-value <c>${VAR}</c> resolution.</summary>
public class ReportConfigEnvironmentTests : IDisposable
{
    private const string VarName = "NR_TEST_VAR_D1";

    public ReportConfigEnvironmentTests() => Environment.SetEnvironmentVariable(VarName, "resolved-value");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VarName, null);
        GC.SuppressFinalize(this);
    }

    private static ReportConfig BaseConfig(IReadOnlyDictionary<string, object?> sourceProperties) => new(
        Name: "r",
        Source: new SourceConfig("sql", sourceProperties),
        Columns: new[] { new ColumnConfig("Id", ColumnType.Integer) },
        Outputs: new[] { new OutputConfig("csv", sourceProperties) },
        Destinations: new[] { new DestinationConfig("local", sourceProperties) });

    [Fact]
    public void Substitutes_a_whole_value_placeholder()
    {
        var config = BaseConfig(new Dictionary<string, object?> { ["connectionString"] = $"${{{VarName}}}" });

        var result = ReportConfigEnvironment.Substitute(config);

        result.Source.Properties!["connectionString"].ShouldBe("resolved-value");
        result.Outputs[0].Properties!["connectionString"].ShouldBe("resolved-value");
        result.Destinations![0].Properties!["connectionString"].ShouldBe("resolved-value");
    }

    [Fact]
    public void Missing_variable_throws_naming_it()
    {
        var config = BaseConfig(new Dictionary<string, object?> { ["connectionString"] = "${NR_TEST_VAR_MISSING}" });

        var ex = Should.Throw<ConfigurationException>(() => ReportConfigEnvironment.Substitute(config));
        ex.Message.ShouldContain("NR_TEST_VAR_MISSING");
    }

    [Fact]
    public void Non_placeholder_strings_are_untouched()
    {
        var config = BaseConfig(new Dictionary<string, object?> { ["sql"] = "SELECT 1" });

        var result = ReportConfigEnvironment.Substitute(config);

        result.Source.Properties!["sql"].ShouldBe("SELECT 1");
    }

    [Fact]
    public void Non_string_values_are_untouched()
    {
        var config = BaseConfig(new Dictionary<string, object?> { ["pageSize"] = 1000L, ["flag"] = true, ["nothing"] = null });

        var result = ReportConfigEnvironment.Substitute(config);

        result.Source.Properties!["pageSize"].ShouldBe(1000L);
        result.Source.Properties!["flag"].ShouldBe(true);
        result.Source.Properties!["nothing"].ShouldBeNull();
    }

    [Fact]
    public void Lower_case_variable_name_is_accepted()
    {
        Environment.SetEnvironmentVariable("nr_test_lower", "lower-value");
        try
        {
            var config = BaseConfig(new Dictionary<string, object?> { ["key"] = "${nr_test_lower}" });

            var result = ReportConfigEnvironment.Substitute(config);

            result.Source.Properties!["key"].ShouldBe("lower-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("nr_test_lower", null);
        }
    }

    [Fact]
    public void Embedded_placeholder_is_not_substituted()
    {
        var config = BaseConfig(new Dictionary<string, object?> { ["key"] = $"abc${{{VarName}}}def" });

        var result = ReportConfigEnvironment.Substitute(config);

        result.Source.Properties!["key"].ShouldBe($"abc${{{VarName}}}def");
    }
}
