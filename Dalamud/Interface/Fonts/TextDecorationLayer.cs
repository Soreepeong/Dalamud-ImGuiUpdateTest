using Dalamud.Plugin.Services;

namespace Dalamud.Interface.Fonts;

/// <summary>Where among the glyphs of a text a decoration is drawn, in the order they are drawn.</summary>
/// <remarks>Each layer is drawn for the whole text before the next, so that what is drawn in a layer under the fill
/// does not cover the fill of the glyphs next to it.</remarks>
/// <seealso cref="TextDecorationSpan.Layer"/>
/// <seealso cref="IFontService.RegisterTextDecoration"/>
public enum TextDecorationLayer
{
    /// <summary>Under the text, before its shadow, as backgrounds and underlines are.</summary>
    BelowText,

    /// <summary>Over the shadow of the text, under its edge.</summary>
    BelowEdge,

    /// <summary>Over the edge of the text, under its fill.</summary>
    BelowFill,

    /// <summary>Over the text and its inline objects, as strikethroughs are.</summary>
    OverText,
}
