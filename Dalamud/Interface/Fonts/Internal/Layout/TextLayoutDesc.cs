using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>Everything that changes the result of layout.</summary>
/// <param name="Size">Pixel size.</param>
/// <param name="Density">Device pixels per logical pixel.</param>
/// <param name="LineHeight">Line advance in pixels if positive, as a multiple of the size of the largest text on the
/// line if negative, or 0 for the font's own line height.</param>
/// <param name="WrapWidth">Wrap width, or 0 or less for no wrapping.</param>
/// <param name="MaxWidth">Width to stop or truncate (<see cref="ImTextLayoutFlags.Ellipsis"/>) at, or 0 or less for
/// unlimited.</param>
/// <param name="Flags">Layout flags.</param>
/// <param name="MaskChar">Character for <see cref="ImTextLayoutFlags.PasswordMask"/>, or 0 for '*'.</param>
/// <param name="TabSize">Spaces between tab stops, counted from the start of the line, or 0 for 4.</param>
/// <remarks>Widths are along the inline axis, which is y for <see cref="ImTextLayoutFlags.Vertical"/>. The line height
/// works as CSS line-height, and may be less than the text.</remarks>
internal readonly record struct TextLayoutDesc(
    float Size,
    float Density = 1,
    float LineHeight = 0,
    float WrapWidth = 0,
    float MaxWidth = 0,
    ImTextLayoutFlags Flags = ImTextLayoutFlags.None,
    uint MaskChar = 0,
    int TabSize = 0)
{
    /// <summary>Makes the parameters ImGui asks for. The locale is not used.</summary>
    /// <param name="desc">ImGui's parameters.</param>
    /// <returns>The parameters.</returns>
    public static TextLayoutDesc FromNative(in ImTextDesc desc) =>
        new(
            desc.Size,
            desc.Density,
            desc.LineHeight,
            desc.WrapWidth,
            desc.MaxWidth >= float.MaxValue ? 0 : desc.MaxWidth,
            desc.Flags,
            desc.MaskChar);
}
