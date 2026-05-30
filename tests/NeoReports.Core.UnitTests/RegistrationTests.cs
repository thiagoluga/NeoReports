using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NeoReports.Abstractions;
using NeoReports.Core.Building;
using NeoReports.Core.DependencyInjection;
using NeoReports.Core.Pipeline;
using NeoReports.Core.Registry;
using NeoReports.Core.UnitTests.Fakes;
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

        registry.Contains("vendas-mensal").Should().BeTrue();
        registry.Names.Should().ContainSingle().Which.Should().Be("vendas-mensal");

        var report = registry.Find("vendas-mensal");
        report.Should().NotBeNull();
        report!.Schema.Columns.Select(c => c.Name).Should().Equal("Id", "Cliente", "Valor", "Data");
        report.Schema.Find("Valor")!.Type.Should().Be(ColumnType.Decimal);
        report.Schema.Find("Id")!.Type.Should().Be(ColumnType.Integer);
        report.Schema.Find("Valor")!.DisplayName.Should().Be("Valor");
        report.OutputCount.Should().Be(1);

        provider.GetRequiredService<IReportRunner>().Should().BeOfType<ReportRunner>();
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
        var act = Register;
        act.Should().Throw<ConfigurationException>();
    }

    [Fact]
    public void Build_without_source_throws()
    {
        var act = () => new ReportBuilder<Venda>("x")
            .Column(v => v.Id, "Id")
            .Build();

        act.Should().Throw<ConfigurationException>().WithMessage("*no source*");
    }

    [Fact]
    public void Build_without_columns_throws()
    {
        var source = new FakeBatchSource<Venda>(new[] { new[] { new Venda(1, "A", 1m, DateTime.UnixEpoch) } });
        var act = () => new ReportBuilder<Venda>("x").From(source).Build();

        act.Should().Throw<ConfigurationException>().WithMessage("*no columns*");
    }
}
