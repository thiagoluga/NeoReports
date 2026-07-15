using Bunit;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class BuilderFormatTests : NeoReportsTestContext
{
    [Fact]
    public void Engine_unavailable_shows_the_nothing_to_select_banner()
    {
        Wizard.EngineAvailable = false;

        var cut = Render<BuilderFormat>();

        cut.Markup.ShouldContain("nothing to select here yet");
        cut.FindAll(".sel-card").ShouldBeEmpty();
    }

    [Fact]
    public void No_registered_formats_shows_the_empty_state()
    {
        Wizard.EngineAvailable = true;
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities([], [], []));

        var cut = Render<BuilderFormat>();

        cut.Find(".es-title").TextContent.ShouldBe("No output formats registered");
    }

    [Fact]
    public void Clicking_a_format_toggles_it_in_and_out_of_the_wizard_selection()
    {
        Wizard.EngineAvailable = true;
        Wizard.Formats.Clear();
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities([], ["csv", "xlsx"], []));

        var cut = Render<BuilderFormat>();
        var csvCard = cut.FindAll(".sel-card").First(c => c.TextContent.Contains("CSV"));
        csvCard.Click();

        Wizard.Formats.ShouldContain("csv");

        cut.WaitForState(() => cut.FindAll(".sel-card").First(c => c.TextContent.Contains("CSV")).ClassList.Contains("selected"));
        cut.FindAll(".sel-card").First(c => c.TextContent.Contains("CSV")).Click();

        Wizard.Formats.ShouldNotContain("csv");
    }

    [Fact]
    public void Footer_count_reflects_the_number_of_selected_formats()
    {
        Wizard.EngineAvailable = true;
        Wizard.Formats.Clear();
        Wizard.Formats.Add("csv");
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities([], ["csv", "xlsx"], []));

        var cut = Render<BuilderFormat>();

        cut.Markup.ShouldContain("1 format selected");
    }

    [Fact]
    public void Unknown_format_id_falls_back_to_a_generic_card()
    {
        Wizard.EngineAvailable = true;
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities([], ["parquet"], []));

        var cut = Render<BuilderFormat>();

        cut.Markup.ShouldContain("PARQUET");
        cut.Markup.ShouldContain(".parquet");
    }
}
