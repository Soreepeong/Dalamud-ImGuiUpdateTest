using Dalamud.Plugin.Services;

namespace Dalamud.Interface.Fonts;

/// <summary>How a registered text decoration is drawn, as the layers it is drawn in.</summary>
/// <remarks>The drawer is called once per span in each layer, in the order of <see cref="TextDecorationLayer"/>. It
/// tells them apart by <see cref="TextDecorationSpan.Layer"/>.</remarks>
/// <seealso cref="IFontService.RegisterTextDecoration"/>
[Flags]
public enum TextDecorationFlags
{
    /// <summary>Drawn over the text, as <see cref="OverText"/>.</summary>
    None = 0,

    /// <summary>Drawn in <see cref="TextDecorationLayer.BelowText"/>.</summary>
    BelowText = 1 << TextDecorationLayer.BelowText,

    /// <summary>Drawn in <see cref="TextDecorationLayer.BelowEdge"/>.</summary>
    BelowEdge = 1 << TextDecorationLayer.BelowEdge,

    /// <summary>Drawn in <see cref="TextDecorationLayer.BelowFill"/>.</summary>
    BelowFill = 1 << TextDecorationLayer.BelowFill,

    /// <summary>Drawn in <see cref="TextDecorationLayer.OverText"/>.</summary>
    OverText = 1 << TextDecorationLayer.OverText,
}
