using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.Configuration;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

/// <summary>
/// Dynamic-path resilience overrides (<see cref="ResilienceConfig"/>): the JSON parser reads them
/// and <see cref="ReportConfigCompiler"/> applies them via the same <c>Retry</c>/<c>OnFailure</c>
/// builder methods the fluent path uses. Omitting the section keeps the engine's defaults.
/// </summary>
public class ResilienceConfigTests
{
    private static readonly JsonReportConfigParser Parser = new();

    private static ServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(new[] { new object?[] { 1L } }))
            .AddSingleton<IWriterFactory>(new FakeWriterFactory("csv", "csv"))
            .BuildServiceProvider();

    private static string ConfigJson(string? resilienceJson = null) => $$"""
    {
      "name": "sales",
      "source": { "type": "inmemory" },
      "columns": [ { "name": "Id", "type": "Integer" } ],
      "outputs": [ { "format": "csv" } ]
      {{(resilienceJson is null ? "" : $", \"resilience\": {resilienceJson}")}}
    }
    """;

    [Fact]
    public void Parser_reads_all_resilience_fields()
    {
        var config = Parser.Parse(ConfigJson("""
        { "maxAttempts": 5, "backoff": "Exponential", "baseDelaySeconds": 2.5, "jitter": true, "onFailure": "skip-and-log" }
        """));

        config.Resilience.ShouldNotBeNull();
        config.Resilience.MaxAttempts.ShouldBe(5);
        config.Resilience.Backoff.ShouldBe("Exponential");
        config.Resilience.BaseDelaySeconds.ShouldBe(2.5);
        config.Resilience.Jitter.ShouldBe(true);
        config.Resilience.OnFailure.ShouldBe("skip-and-log");
    }

    [Fact]
    public void Omitted_resilience_keeps_engine_defaults()
    {
        var config = Parser.Parse(ConfigJson());
        config.Resilience.ShouldBeNull();

        using var services = BuildServices();
        var report = ReportConfigCompiler.Compile(config, services);

        report.Retry.Attempts.ShouldBe(1);
        report.Retry.Backoff.ShouldBe(RetryBackoff.Constant);
        report.Retry.BaseDelay.ShouldBe(TimeSpan.FromSeconds(1));
        report.Retry.UseJitter.ShouldBeFalse();
        report.FailureStrategy.Name.ShouldBe("abort");
    }

    [Fact]
    public void Compiler_applies_exponential_backoff_with_jitter_and_max_attempts()
    {
        var config = Parser.Parse(ConfigJson("""
        { "maxAttempts": 4, "backoff": "Exponential", "baseDelaySeconds": 3, "jitter": true }
        """));

        using var services = BuildServices();
        var report = ReportConfigCompiler.Compile(config, services);

        report.Retry.Attempts.ShouldBe(4);
        report.Retry.Backoff.ShouldBe(RetryBackoff.Exponential);
        report.Retry.BaseDelay.ShouldBe(TimeSpan.FromSeconds(3));
        report.Retry.UseJitter.ShouldBeTrue();
    }

    [Theory]
    [InlineData("abort", "abort")]
    [InlineData("skip-and-log", "skip-and-log")]
    [InlineData("SKIP-AND-LOG", "skip-and-log")]
    public void Compiler_applies_failure_strategy(string onFailure, string expectedName)
    {
        var config = Parser.Parse(ConfigJson($$"""{ "onFailure": "{{onFailure}}" }"""));

        using var services = BuildServices();
        var report = ReportConfigCompiler.Compile(config, services);

        report.FailureStrategy.Name.ShouldBe(expectedName);
    }

    [Fact]
    public void Compiler_rejects_unknown_backoff_value()
    {
        var config = Parser.Parse(ConfigJson("""{ "backoff": "linear" }"""));
        using var services = BuildServices();

        var ex = Should.Throw<ConfigurationException>(() => ReportConfigCompiler.Compile(config, services));
        ex.Message.ShouldContain("linear");
    }

    [Fact]
    public void Compiler_rejects_unknown_onFailure_value()
    {
        var config = Parser.Parse(ConfigJson("""{ "onFailure": "retry-forever" }"""));
        using var services = BuildServices();

        var ex = Should.Throw<ConfigurationException>(() => ReportConfigCompiler.Compile(config, services));
        ex.Message.ShouldContain("retry-forever");
    }

    // --- abortWhen (ADR D37) ---

    [Fact]
    public void Parser_reads_abortWhen()
    {
        var config = Parser.Parse(ConfigJson("""
        { "onFailure": "skip-and-log", "abortWhen": { "consecutiveFailures": 3, "totalFailures": 10, "failureRate": 0.5 } }
        """));

        config.Resilience!.AbortWhen.ShouldNotBeNull();
        config.Resilience.AbortWhen.ConsecutiveFailures.ShouldBe(3);
        config.Resilience.AbortWhen.TotalFailures.ShouldBe(10);
        config.Resilience.AbortWhen.FailureRate.ShouldBe(0.5);
    }

    [Fact]
    public void Compiler_applies_abortWhen_and_it_is_introspectable_on_the_compiled_report()
    {
        var config = Parser.Parse(ConfigJson("""
        { "onFailure": "skip-and-log", "abortWhen": { "consecutiveFailures": 3 } }
        """));

        using var services = BuildServices();
        var report = ReportConfigCompiler.Compile(config, services);

        report.FailureStrategy.Name.ShouldBe("skip-and-log");
        report.AbortThresholds.ShouldBe(new AbortThresholdConfig(ConsecutiveFailures: 3));
    }

    [Fact]
    public void Compiler_rejects_abortWhen_with_onFailure_abort()
    {
        var config = Parser.Parse(ConfigJson("""
        { "onFailure": "abort", "abortWhen": { "consecutiveFailures": 3 } }
        """));
        using var services = BuildServices();

        var ex = Should.Throw<ConfigurationException>(() => ReportConfigCompiler.Compile(config, services));
        ex.Message.ShouldContain("skip-and-log");
    }

    [Fact]
    public void Compiler_rejects_abortWhen_with_onFailure_omitted()
    {
        var config = Parser.Parse(ConfigJson("""{ "abortWhen": { "consecutiveFailures": 3 } }"""));
        using var services = BuildServices();

        var ex = Should.Throw<ConfigurationException>(() => ReportConfigCompiler.Compile(config, services));
        ex.Message.ShouldContain("skip-and-log");
    }

    [Fact]
    public void Compiler_rejects_an_empty_abortWhen()
    {
        var config = Parser.Parse(ConfigJson("""{ "onFailure": "skip-and-log", "abortWhen": {} }"""));
        using var services = BuildServices();

        Should.Throw<ConfigurationException>(() => ReportConfigCompiler.Compile(config, services));
    }

    [Fact]
    public void Omitted_abortWhen_leaves_no_escalation()
    {
        var config = Parser.Parse(ConfigJson("""{ "onFailure": "skip-and-log" }"""));
        using var services = BuildServices();

        var report = ReportConfigCompiler.Compile(config, services);
        report.AbortThresholds.ShouldBeNull();
    }
}
