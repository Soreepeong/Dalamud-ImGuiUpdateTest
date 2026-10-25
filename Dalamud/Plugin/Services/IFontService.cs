using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts;
using Dalamud.Interface.Internal.Windows.Data.Widgets;

namespace Dalamud.Plugin.Services;

/// <summary>Holds the font features that start from neither a font spec nor a font.</summary>
/// <remarks><ol>
/// <li>Push <see cref="FontSpec"/>s, such as <c>FontSpec.Mono.Push()</c> and <c>spec.GetFont()</c>.</li>
/// <li>If keeping a reference to a font is desired, keep <see cref="FontSpec"/>s or <see cref="ImFontRef"/></li>
/// <li>Built-in fonts are at <see cref="FontSpec.Default"/>, <see cref="FontSpec.Icon"/>,
/// <see cref="FontSpec.IconFixedWidth"/> and <see cref="FontSpec.Mono"/>.</li>
/// </ol></remarks>
public interface IFontService : IDalamudService
{
    /// <summary>The Dalamud default font has changed.</summary>
    event Action? DefaultFontChanged;

    /// <summary>Finds the font behind an ImGui font, such as <see cref="ImGui.GetFont"/>.</summary>
    /// <param name="font">The ImGui font.</param>
    /// <returns>The font, or <see langword="null"/> if <paramref name="font"/> was not made from a spec.</returns>
    /// <remarks>It never loads a font.</remarks>
    ImFontRef? FindFont(ImFontPtr font);

    /// <summary>Pushes a callback that decides how text with a markup is drawn.</summary>
    /// <param name="callback">The callback.</param>
    /// <returns>A disposable object that pops the callback.</returns>
    /// <remarks>Callbacks are ignored for text with <see cref="ImTextMarkup.Plain"/> as its
    /// <see cref="ImTextStyle.Markup"/>.</remarks>
    ImPopFontDisposable PushTextCallback(ITextMarkupCallback callback);

    /// <summary>Makes layouts made with a callback stale, so that its answers are asked again.</summary>
    /// <param name="callback">The callback.</param>
    void InvalidateTextCallback(ITextMarkupCallback callback);

    /// <summary>Registers a text decoration, to use as <see cref="ImTextStyle.ExtraDecoration"/> with
    /// <see cref="TextDecorationRegistration.Id"/>.</summary>
    /// <param name="drawer">Draws the decoration, on the main thread.</param>
    /// <param name="flags">The layers the decoration is drawn in. <see cref="TextDecorationFlags.None"/> is taken as
    /// <see cref="TextDecorationFlags.OverText"/>.</param>
    /// <param name="bands">The number of times the decoration is drawn in each layer, from 1 to 16. Each time is for
    /// all the spans of a text before the next, as <see cref="TextDecorationSpan.Band"/>, so that what is drawn later
    /// covers what is drawn earlier everywhere in the text.</param>
    /// <returns>The registration, which unregisters the decoration when disposed or when the plugin unloads.</returns>
    /// <exception cref="ObjectDisposedException">If the plugin has unloaded.</exception>
    /// <remarks>Decorations are not drawn for vertical text. Keep what the drawer needs in
    /// <see cref="ImTextDecoration.Context"/> and <see cref="ImTextDecoration.Data"/>.</remarks>
    /// <example>
    /// See <see cref="FontSamplesImGuiWidget.DrawRainbowEdge"/> for an example.
    /// <code>
    /// var squiggle = fontService.RegisterTextDecoration(
    ///     (in TextDecorationSpan span) =&gt; { /* span.AddPolyline(...) */ },
    ///     TextDecorationFlags.BelowText);
    /// using (ImGui.PushTextDecoration(squiggle.Id, 0xFF0000FF))
    ///     ImGui.Text("Misspeled"u8);
    ///
    /// // A rainbow around the glyphs, widest band first, all under the fill of every glyph.
    /// uint[] colors = [0xFF0000FF, 0xFF00A5FF, 0xFF00FFFF, 0xFF00FF00, 0xFFFF0000, 0xFF82004B, 0xFFEE82EE];
    /// var rainbow = fontService.RegisterTextDecoration(
    ///     (in TextDecorationSpan span) =&gt; span.DrawGlyphs(colors[span.Band], colors.Length - span.Band),
    ///     TextDecorationFlags.BelowFill,
    ///     colors.Length);
    /// </code></example>
    TextDecorationRegistration RegisterTextDecoration(
        TextDecorationDrawer drawer, TextDecorationFlags flags = TextDecorationFlags.OverText, int bands = 1);
}
