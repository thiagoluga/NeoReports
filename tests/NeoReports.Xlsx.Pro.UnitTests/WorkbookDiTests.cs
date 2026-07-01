using Microsoft.Extensions.DependencyInjection;
using NeoReports.Core.Sections;
using Shouldly;
using Xunit;

namespace NeoReports.Xlsx.Pro.UnitTests;

/// <summary>
/// B1.6: <c>AddXlsxWorkbook()</c> registers the workbook writer as an <see cref="ISectionedWriterFactory"/>
/// (format "xlsx-workbook") so config-driven reports can target a workbook via an output with sections.
/// </summary>
public class WorkbookDiTests
{
    [Fact]
    public void AddXlsxWorkbook_registers_a_resolvable_sectioned_factory()
    {
        var services = new ServiceCollection();
        services.AddXlsxWorkbook(o => o.AutoFilter());
        using var provider = services.BuildServiceProvider();

        ISectionedWriterFactory factory = provider.GetServices<ISectionedWriterFactory>().Single();
        factory.Format.ShouldBe("xlsx-workbook");
        factory.Create(new Dictionary<string, object?>(), provider).ShouldBeOfType<XlsxWorkbookWriter>();
    }
}
