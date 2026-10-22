using Dalamud.Plugin.Services;

namespace Dalamud.Interface.Fonts;

/// <summary>Draws a text decoration over a span of a line.</summary>
/// <param name="span">Where to draw, and with what.</param>
/// <remarks>Called on the main thread, while the text is drawn. Called once per run of adjacent text on a line with
/// the same decoration, context, and text color.</remarks>
/// <seealso cref="IFontService.RegisterTextDecoration"/>
public delegate void TextDecorationDrawer(in TextDecorationSpan span);
