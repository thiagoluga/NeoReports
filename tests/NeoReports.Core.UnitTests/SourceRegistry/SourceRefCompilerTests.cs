using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NeoReports.Abstractions;
using NeoReports.Core.Configuration;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Core.SourceRegistry;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests.SourceRegistry;

/// <summary>
/// ADR D42: <c>SourceConfig.Ref</c> compile-time validation and run-time resolution (locked
/// decision 2 — properties resolve fresh per run, never baked into the compiled report).
/// </summary>
public class SourceRefCompilerTests
{
    private const string VarName = "NR_D42_COMPILER_TEST_VAR";

    private static ReportExecutionContext Exec() =>
        new(Guid.NewGuid().ToString("N"), "r", null, NullLogger.Instance, CancellationToken.None);

    private static ServiceProvider BuildServices(out FakeConfigSourceProvider provider, IReadOnlyList<object?[]>? rows = null)
    {
        var services = new ServiceCollection();
        services.AddInMemorySourceRegistry();
        provider = new FakeConfigSourceProvider(rows ?? new[] { new object?[] { 1L, "Acme" } });
        services.AddSingleton<IConfigSourceProvider>(provider);
        services.AddSingleton<IWriterFactory>(new FakeWriterFactory("fake", "fake"));
        return services.BuildServiceProvider();
    }

    private static ReportConfig ConfigWithRef(string refName, string? type = null, IReadOnlyDictionary<string, object?>? overlay = null) => new(
        Name: "r",
        Source: new SourceConfig(type, overlay, refName),
        Columns: new[] { new ColumnConfig("Id", ColumnType.Integer), new ColumnConfig("Customer", ColumnType.String) },
        Outputs: new[] { new OutputConfig("fake") });

    [Fact]
    public async Task Compiles_and_runs_when_type_is_taken_from_the_definition()
    {
        await using var services = BuildServices(out _);
        var registry = services.GetRequiredService<ISourceRegistry>();
        await registry.SaveAsync(new SourceDefinition("sales-db", "inmemory"), CancellationToken.None);

        CompiledReport report = ReportConfigCompiler.Compile(ConfigWithRef("sales-db"), services);
        report.SourceRef.ShouldBe("sales-db");

        ReportRunResult result = await ReportRunner.ExecuteAsync(report, Exec(), services, CancellationToken.None);
        result.Status.ShouldBe(ReportRunStatus.Completed);
    }

    [Fact]
    public async Task Compiles_when_declared_type_matches_the_definition()
    {
        await using var services = BuildServices(out _);
        var registry = services.GetRequiredService<ISourceRegistry>();
        await registry.SaveAsync(new SourceDefinition("sales-db", "inmemory"), CancellationToken.None);

        CompiledReport report = ReportConfigCompiler.Compile(ConfigWithRef("sales-db", "inmemory"), services);
        report.SourceRef.ShouldBe("sales-db");
    }

    [Fact]
    public async Task Rejects_a_declared_type_that_mismatches_the_definition()
    {
        await using var services = BuildServices(out _);
        var registry = services.GetRequiredService<ISourceRegistry>();
        await registry.SaveAsync(new SourceDefinition("sales-db", "sql"), CancellationToken.None);

        var ex = Should.Throw<ConfigurationException>(
            () => ReportConfigCompiler.Compile(ConfigWithRef("sales-db", "inmemory"), services));
        ex.Message.ShouldContain("sales-db");
    }

    [Fact]
    public void Rejects_an_unknown_ref()
    {
        using var services = BuildServices(out _);

        var ex = Should.Throw<ConfigurationException>(
            () => ReportConfigCompiler.Compile(ConfigWithRef("does-not-exist"), services));
        ex.Message.ShouldContain("does-not-exist");
    }

    [Fact]
    public void Rejects_a_ref_when_no_source_registry_is_configured()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigSourceProvider>(new FakeConfigSourceProvider(Array.Empty<object?[]>()));
        services.AddSingleton<IWriterFactory>(new FakeWriterFactory("fake", "fake"));
        using var provider = services.BuildServiceProvider();

        Should.Throw<ConfigurationException>(() => ReportConfigCompiler.Compile(ConfigWithRef("sales-db"), provider));
    }

    [Fact]
    public async Task Report_local_properties_overlay_the_definition_and_win_on_conflict()
    {
        await using var services = BuildServices(out FakeConfigSourceProvider provider);
        var registry = services.GetRequiredService<ISourceRegistry>();
        await registry.SaveAsync(new SourceDefinition("sales-db", "inmemory",
            new Dictionary<string, object?> { ["connectionString"] = "base-conn", ["shared"] = "from-definition" }), CancellationToken.None);

        var overlay = new Dictionary<string, object?> { ["sql"] = "SELECT * FROM Sales", ["shared"] = "from-report" };
        CompiledReport report = ReportConfigCompiler.Compile(ConfigWithRef("sales-db", overlay: overlay), services);
        await ReportRunner.ExecuteAsync(report, Exec(), services, CancellationToken.None);

        provider.LastConfig.ShouldNotBeNull();
        provider.LastConfig!.Properties!["connectionString"].ShouldBe("base-conn"); // definition-only key survives
        provider.LastConfig.Properties["sql"].ShouldBe("SELECT * FROM Sales"); // report-only key survives
        provider.LastConfig.Properties["shared"].ShouldBe("from-report"); // report overlay wins the conflict
    }

    [Fact]
    public async Task Definition_properties_alone_are_used_when_the_report_has_none()
    {
        await using var services = BuildServices(out FakeConfigSourceProvider provider);
        var registry = services.GetRequiredService<ISourceRegistry>();
        await registry.SaveAsync(new SourceDefinition("sales-db", "inmemory",
            new Dictionary<string, object?> { ["connectionString"] = "base-conn" }), CancellationToken.None);

        CompiledReport report = ReportConfigCompiler.Compile(ConfigWithRef("sales-db"), services);
        await ReportRunner.ExecuteAsync(report, Exec(), services, CancellationToken.None);

        provider.LastConfig!.Properties!["connectionString"].ShouldBe("base-conn");
    }

    [Fact]
    public async Task A_changed_environment_variable_is_reflected_on_the_next_run_without_recompiling()
    {
        Environment.SetEnvironmentVariable(VarName, "first-value");
        try
        {
            await using var services = BuildServices(out FakeConfigSourceProvider provider);
            var registry = services.GetRequiredService<ISourceRegistry>();
            await registry.SaveAsync(new SourceDefinition("sales-db", "inmemory",
                new Dictionary<string, object?> { ["connectionString"] = $"${{{VarName}}}" }), CancellationToken.None);

            CompiledReport report = ReportConfigCompiler.Compile(ConfigWithRef("sales-db"), services);

            await ReportRunner.ExecuteAsync(report, Exec(), services, CancellationToken.None);
            provider.LastConfig!.Properties!["connectionString"].ShouldBe("first-value");

            Environment.SetEnvironmentVariable(VarName, "second-value");
            await ReportRunner.ExecuteAsync(report, Exec(), services, CancellationToken.None);
            provider.LastConfig!.Properties!["connectionString"].ShouldBe("second-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(VarName, null);
        }
    }

    [Fact]
    public async Task Deleting_the_source_after_compile_fails_the_next_run_with_a_clear_error()
    {
        await using var services = BuildServices(out _);
        var registry = services.GetRequiredService<ISourceRegistry>();
        await registry.SaveAsync(new SourceDefinition("sales-db", "inmemory"), CancellationToken.None);

        CompiledReport report = ReportConfigCompiler.Compile(ConfigWithRef("sales-db"), services);

        await registry.DeleteAsync("sales-db", CancellationToken.None);

        ReportRunResult result = await ReportRunner.ExecuteAsync(report, Exec(), services, CancellationToken.None);
        result.Status.ShouldBe(ReportRunStatus.Failed);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("sales-db");
    }

    [Fact]
    public void Existing_inline_source_configs_compile_unchanged()
    {
        using var services = BuildServices(out _);
        var config = new ReportConfig(
            Name: "inline",
            Source: new SourceConfig("inmemory"),
            Columns: new[] { new ColumnConfig("Id", ColumnType.Integer), new ColumnConfig("Customer", ColumnType.String) },
            Outputs: new[] { new OutputConfig("fake") });

        CompiledReport report = ReportConfigCompiler.Compile(config, services);
        report.SourceRef.ShouldBeNull();
    }
}
