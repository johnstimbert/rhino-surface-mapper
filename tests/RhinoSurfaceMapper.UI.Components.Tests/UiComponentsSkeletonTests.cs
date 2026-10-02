using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Rendering;

namespace RhinoSurfaceMapper.UI.Components.Tests;

/// <summary>
/// Placeholder test for the Phase 0 skeleton. <c>RhinoSurfaceMapper.UI.Components</c> ships
/// no Razor components yet (the BlazorWebView shell arrives in Phase 3 per the design's
/// phased-delivery table); this test honestly verifies that the bUnit test harness itself is
/// wired correctly — renders a trivial inline markup fragment via a plain
/// <see cref="Microsoft.AspNetCore.Components.RenderFragment"/>, with no <c>.razor</c> file
/// required — rather than faking coverage for a component that does not exist yet.
/// </summary>
public sealed class UiComponentsSkeletonTests : BunitContext
{
    [Fact]
    public void The_bUnit_test_harness_can_render_markup()
    {
        var component = Render(builder => builder.AddMarkupContent(0, "<p>Phase 0 skeleton</p>"));

        component.Markup.Should().Contain("Phase 0 skeleton");
    }
}
