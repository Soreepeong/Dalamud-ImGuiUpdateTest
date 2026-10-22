using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>Told when a layout is drawn without a transform, such as to make its links interactive, and draws its
/// inline objects, for layouts that callers do not draw themselves, such as ImGui's.</summary>
/// <seealso cref="TextLayout.RenderHook"/>
internal interface ITextRenderHook
{
    /// <summary>Called before the layout is drawn.</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="drawList">The draw list.</param>
    /// <param name="pos">Position of the layout origin.</param>
    /// <param name="userData">The user data of the runs to draw with <paramref name="style"/>, not 0.</param>
    /// <param name="style">The style. Its bold, italic and markup do not apply.</param>
    /// <returns>Whether to draw those runs with the style.</returns>
    bool BeginRender(TextLayout layout, ImDrawListPtr drawList, Vector2 pos, out ulong userData, out ImTextStyle style);

    /// <summary>Draws an inline object.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="objectIndex">The object.</param>
    /// <param name="topLeft">The top left of its box.</param>
    /// <param name="col">The text color the layout is drawn with, whose alpha applies.</param>
    void DrawObject(ImDrawListPtr drawList, int objectIndex, Vector2 topLeft, uint col);
}
