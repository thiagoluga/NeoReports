using Bunit;
using Microsoft.AspNetCore.Components;
using NeoReports.UI.Components.UI;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Components;

public sealed class DataGridTests : NeoReportsTestContext
{
    [Fact]
    public void Loading_renders_four_skeleton_rows_with_Columns_cells_each()
    {
        var cut = Render<DataGrid>(p => p
            .Add(x => x.Loading, true)
            .Add(x => x.Columns, 3));

        cut.FindAll("tbody tr").Count.ShouldBe(4);
        cut.FindAll("tbody tr").ShouldAllBe(row => row.QuerySelectorAll("td").Length == 3);
    }

    [Fact]
    public void Empty_renders_title_and_icon_instead_of_body()
    {
        var cut = Render<DataGrid>(p => p
            .Add(x => x.Empty, true)
            .Add(x => x.EmptyTitle, "No jobs yet")
            .Add(x => x.EmptyIcon, "clock-off")
            .Add(x => x.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<tr><td>should not render</td></tr>"))));

        cut.Markup.ShouldContain("No jobs yet");
        cut.Markup.ShouldContain("ti-clock-off");
        cut.Markup.ShouldNotContain("should not render");
    }

    [Fact]
    public void Not_loading_and_not_empty_renders_Body()
    {
        var cut = Render<DataGrid>(p => p
            .Add(x => x.Loading, false)
            .Add(x => x.Empty, false)
            .Add(x => x.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<tr><td>real row</td></tr>"))));

        cut.Markup.ShouldContain("real row");
    }
}
