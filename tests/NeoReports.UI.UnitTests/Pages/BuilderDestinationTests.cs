using Bunit;
using NeoReports.UI.Pages;
using NeoReports.UI.Services;
using Shouldly;
using Xunit;

namespace NeoReports.UI.UnitTests.Pages;

public sealed class BuilderDestinationTests : NeoReportsTestContext
{
    [Fact]
    public void Engine_unavailable_hides_the_destination_card_entirely()
    {
        Wizard.EngineAvailable = false;

        var cut = Render<BuilderDestination>();

        cut.Markup.ShouldNotContain("Engine destination");
    }

    [Fact]
    public void Selecting_a_destination_type_updates_the_wizard_and_footer_label()
    {
        Wizard.EngineAvailable = true;
        Wizard.DestinationType = "";
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities([], [], ["local", "s3"]));

        var cut = Render<BuilderDestination>();
        cut.FindAll(".sel-card").First(c => c.TextContent.Contains("local")).Click();

        Wizard.DestinationType.ShouldBe("local");
        cut.WaitForState(() => cut.Find(".wizard-footer .mono.muted").TextContent == "local");
    }

    [Fact]
    public void Selecting_None_clears_the_destination_type()
    {
        Wizard.EngineAvailable = true;
        Wizard.DestinationType = "local";
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities([], [], ["local"]));

        var cut = Render<BuilderDestination>();
        cut.FindAll(".sel-card").First(c => c.TextContent.Contains("None")).Click();

        Wizard.DestinationType.ShouldBe("");
    }

    [Fact]
    public void Typing_a_path_updates_the_wizard_state()
    {
        Wizard.EngineAvailable = true;
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities([], [], ["local"]));

        var cut = Render<BuilderDestination>();
        cut.Find("input[placeholder='./out/{name}-{date:yyyy-MM-dd}.{ext}']").Input("./out/{name}.csv");

        Wizard.DestinationPath.ShouldBe("./out/{name}.csv");
    }

    [Fact]
    public void Editing_with_a_type_but_no_path_shows_the_re_enter_warning()
    {
        Wizard.IsEditing = true;
        Wizard.EngineAvailable = true;
        Wizard.DestinationType = "s3";
        Wizard.DestinationPath = "";
        Api.Capabilities = _ => Task.FromResult<ApiCapabilities?>(new ApiCapabilities([], [], ["s3"]));

        var cut = Render<BuilderDestination>();

        cut.Markup.ShouldContain("Re-enter the path / key template.");
    }
}
