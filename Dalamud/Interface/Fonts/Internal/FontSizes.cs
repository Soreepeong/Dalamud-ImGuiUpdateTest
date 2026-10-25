using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Sources;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>Converts between the sizes of fonts.</summary>
/// <remarks><ul>
/// <li>The glyph size is the size of the glyphs in pixels, before the global scale. <see cref="FontSpec.SizePx"/>
/// and the font pushes take it.</li>
/// <li>The push size is ImGui's font size before the global scale, which is the line height. It is the glyph size
/// times the line height of the font. <see cref="ImGui.PushFont(ImFontPtr, float)"/> and
/// <see cref="ImGuiStyle.FontSizeBase"/> take it.</li>
/// <li>The ImGui size is the push size times the global scale. <see cref="ImGui.GetFontSize"/> gives it, and text
/// layouts and draw lists take it.</li>
/// </ul></remarks>
internal static class FontSizes
{
    /// <summary>Gets the glyph size of a spec.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="currentSizePx">The glyph size that a relative size is a ratio of.</param>
    /// <returns>The glyph size.</returns>
    public static float ToGlyphSize(FontSpec spec, float currentSizePx) =>
        spec.SizePx > 0 ? spec.SizePx : spec.RelativeSize * currentSizePx;

    /// <summary>Gets the push size of a glyph size.</summary>
    /// <param name="target">The font made from a spec, or null for other fonts.</param>
    /// <param name="sizePx">The glyph size, as for <see cref="ImFontRef.Push"/>.</param>
    /// <returns>The push size.</returns>
    public static unsafe float ToPushSize(CompositeFont? target, float? sizePx)
    {
        // ImGui asserts on negative sizes, and glyphs can not be made at a size that is not finite.
        if (sizePx is { } s && !float.IsFinite(s))
            sizePx = 0;
        var push = target is null ? sizePx ?? 0f : sizePx is { } v ? target.ToPushSize(v) : target.PushSize;

        // A size that is not positive is a ratio of the current one.
        return push > 0 ? push : ImGui.GetCurrentContext().Handle->FontSizeBase * (push == 0 ? 1f : -push);
    }

    /// <summary>Gets the ImGui size of a glyph size.</summary>
    /// <param name="target">The font made from a spec, or null for other fonts.</param>
    /// <param name="sizePx">The glyph size, as for <see cref="ImFontRef.Push"/>.</param>
    /// <returns>The ImGui size.</returns>
    public static float ToImGuiSize(CompositeFont? target, float? sizePx) =>
        ToPushSize(target, sizePx) * ImGui.GetStyle().FontScaleMain;

    /// <summary>Gets the push size of the default font.</summary>
    /// <param name="factory">The font factory.</param>
    /// <returns>The push size.</returns>
    public static float DefaultPushSize(FontFactory factory) => factory.DefaultSizePx * factory.DefaultLineHeightScale;
}
