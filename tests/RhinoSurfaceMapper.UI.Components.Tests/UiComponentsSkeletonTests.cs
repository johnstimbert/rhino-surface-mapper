using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Rendering;

namespace RhinoSurfaceMapper.UI.Components.Tests;

/// <summary>
/// Retained from the Phase 0 skeleton: verifies the bUnit test harness itself is wired
/// correctly by rendering a trivial inline markup fragment via a plain
/// <see cref="Microsoft.AspNetCore.Components.RenderFragment"/>, with no <c>.razor</c> file
/// required. The real component tree (<c>MapCanvas</c>, <c>MainLayout</c>, <c>Routes</c>)
/// arrived in Phase 3 and is covered by <c>Map/MapTransformTests</c>,
/// <c>Map/MapSceneTests</c> and <c>Map/MapSessionStoreConcurrencyTests</c> instead — this test
/// is kept purely as a harness smoke check, not component coverage.
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
