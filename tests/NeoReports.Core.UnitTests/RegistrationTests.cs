using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Registry;
using NeoReports.Core.UnitTests.Fakes;
using Shouldly;
using Xunit;

namespace NeoReports.Core.UnitTests;

public class RegistrationTests
{
    [Fact]
    public void AddReport_registers_typed_report_and_schema()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var source = new FakeBatchSource<Venda>(new[]
        {
            new[] { new Venda(1, "A", 10m, DateTime.UnixEpoch) },
        });

        services.AddReport<Venda>("vendas-mensal", b => b
            .From(source)
            .Filter(v => v.Valor > 0)
            .Column(v => v.Id, "ID Venda")
            .Column(v => v.Cliente, "Cliente")
            .Column(v => v.Valor, "Valor", format: "C2", culture: "pt-BR")
            .Column(v => v.Data, "Data Venda", format: "yyyy-MM-dd")
            .To(new OutputSpec(new FakeWriterFactory())));

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IReportRegistry>();

        registry.Contains("vendas-mensal").ShouldBeTrue();
        registry.Names.ShouldHaveSingleItem().ShouldBe("vendas-mensal");

        var report = registry.Find("vendas-mensal");
        report.ShouldNotBeNull();
        report.Schema.Columns.Select(c => c.Name).ShouldBe(new[] { "Id", "Cliente", "Valor", "Data" });
        report.Schema.Find("Valor")!.Type.ShouldBe(ColumnType.Decimal);
        report.Schema.Find("Id")!.Type.ShouldBe(ColumnType.Integer);
        report.Schema.Find("Valor")!.DisplayName.ShouldBe("Valor");
        report.OutputCount.ShouldBe(1);

        provider.GetRequiredService<IReportRunner>().ShouldBeOfType<ReportRunner>();
    }

    [Fact]
    public void AddReport_with_duplicate_name_throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var source = new FakeBatchSource<Venda>(new[] { new[] { new Venda(1, "A", 1m, DateTime.UnixEpoch) } });

        void Register() => services.AddReport<Venda>("dup", b => b
            .From(source)
            .Column(v => v.Id, "Id")
            .To(new OutputSpec(new FakeWriterFactory())));

        Register();
        Should.Throw<ConfigurationException>(Register);
    }

    [Fact]
    public void Build_without_source_throws()
    {
        var act = () => new ReportBuilder<Venda>("x")
            .Column(v => v.Id, "Id")
            .Build();

        Should.Throw<ConfigurationException>(act).Message.ShouldContain("no source");
    }

    [Fact]
    public void Build_without_columns_throws()
    {
        var source = new FakeBatchSource<Venda>(new[] { new[] { new Venda(1, "A", 1m, DateTime.UnixEpoch) } });
        var act = () => new ReportBuilder<Venda>("x").From(source).Build();

        Should.Throw<ConfigurationException>(act).Message.ShouldContain("no columns");
    }
}
