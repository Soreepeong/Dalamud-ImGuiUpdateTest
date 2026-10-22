using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts;

namespace Dalamud.Plugin.Services;

/// <summary>Provides font features with automatic font lifecycle management.</summary>
/// <remarks>
/// <para>Use <see cref="GetFontAsync"/> to load fonts ahead of use to prevent the main thread from blocking.</para>
/// <para>Keep the <see cref="ImFontRef"/> of a font, not its <see cref="ImFontPtr"/>. <see cref="ImFontPtr"/> may
/// be invalidated if nothing points to the corresponding <see cref="ImFontRef"/>.</para>
/// </remarks>
/// <example><code>
/// using (fontService.Push(FontSpec.Mono))
///     ImGui.Text("Monospaced"u8);
///
/// // Loaded ahead, then used by pointer.
/// var title = await fontService.GetFontAsync(FontSpec.From(GameFontFamilyAndSize.TrumpGothic23));
/// ImGui.GetWindowDrawList().AddText(title, 32, pos, color, "Title"u8);
/// </code></example>
public interface IFontService : IDalamudService
{
    /// <summary>Raised on the main thread when the default font has changed.</summary>
    /// <remarks>Relevant for any fonts that has default font in its <see cref="IFontSpec"/> chain.</remarks>
    event Action? DefaultFontChanged;

    /// <summary>Gets the default font.</summary>
    IFontSpec DefaultFont { get; }

    /// <summary>Gets the FontAwesome icon font.</summary>
    IFontSpec IconFont { get; }

    /// <summary>Gets the FontAwesome icon font, with each icon fit into a square of the font size.</summary>
    IFontSpec IconFontFixedWidth { get; }

    /// <summary>Gets the monospaced font.</summary>
    IFontSpec MonoFont { get; }

    /// <summary>Pushes the font of a spec into the ImGui font stack, loading it if needed (blocks until loaded). If it
    /// could not be loaded, the current font is used, and the error is logged.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="sizePx">Pixels before global scaling if positive, a ratio of the current font size if negative
    /// (-1.5 = 150%) or 0 (100%), or the spec's size if <see langword="null"/>, taking
    /// <see cref="SingleFontSpec.LineHeight"/> into consideration.</param>
    /// <returns>A disposable object that pops the font.</returns>
    /// <exception cref="InvalidOperationException">If called outside the main thread.</exception>
    ImPopFontDisposable Push(IFontSpec spec, float? sizePx = null);

    /// <summary>Gets the font of a spec, blocking to load as needed.</summary>
    /// <param name="spec">The spec.</param>
    /// <returns>The font either loaded or failed, in which case <see cref="ImFontRef.Exception"/> is set.</returns>
    ImFontRef GetFont(IFontSpec spec);

    /// <summary>Gets the font of a spec, loading it in the background if needed.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="retry">Whether to try loading again if an earlier attempt failed.</param>
    /// <param name="cancellationToken">Cancels the wait. Does not cancel the load itself.</param>
    /// <returns>The font either loaded or failed. Use <see cref="ImFontRefExtensions.ThrowIfFailed"/> to make it throw
    /// on failure.</returns>
    Task<ImFontRef> GetFontAsync(IFontSpec spec, bool retry = false, CancellationToken cancellationToken = default);

    /// <summary>Gets the font behind an ImGui font, such as <see cref="ImGui.GetFont"/>.</summary>
    /// <param name="font">The ImGui font.</param>
    /// <returns>The font, or <see langword="null"/> if <paramref name="font"/> was not made from a spec.</returns>
    ImFontRef? GetFont(ImFontPtr font);

    /// <summary>Pushes a callback that decides how text with a markup is drawn.</summary>
    /// <param name="callback">The callback.</param>
    /// <returns>A disposable object that pops the callback.</returns>
    /// <remarks>Relevant if <see cref="ImTextStyle.Markup"/> is not <see cref="ImTextMarkup.Plain"/>.</remarks>
    ImPopFontDisposable PushTextCallback(ITextMarkupCallback callback);

    /// <summary>Makes layouts made with a callback stale, so that its answers are asked again.</summary>
    /// <param name="callback">The callback.</param>
    void InvalidateTextCallback(ITextMarkupCallback callback);

    /// <summary>Registers a text decoration, to use as <see cref="ImTextStyle.ExtraDecoration"/> with
    /// <see cref="TextDecorationRegistration.Id"/>.</summary>
    /// <param name="drawer">Draws the decoration, on the main thread.</param>
    /// <param name="flags">The layers the decoration is drawn in.</param>
    /// <param name="bands">The number of times the decoration is drawn in each layer. Each time is for all the spans of
    /// a text before the next, as <see cref="TextDecorationSpan.Band"/>, so that what is drawn later covers what is
    /// drawn earlier everywhere in the text.</param>
    /// <returns>The registration, which unregisters the decoration when disposed or when the plugin unloads.</returns>
    /// <remarks>Decorations are not drawn for vertical text. Keep what the drawer needs in
    /// <see cref="ImTextDecoration.Context"/> and <see cref="ImTextDecoration.Data"/>.</remarks>
    /// <example><code>
    /// var squiggle = fontService.RegisterTextDecoration(
    ///     (in TextDecorationSpan span) =&gt; { /* span.AddPolyline(...) */ },
    ///     TextDecorationFlags.BelowText);
    /// using (ImGui.PushDecoration(squiggle.Id, 0xFF0000FF))
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
        TextDecorationDrawer drawer, TextDecorationFlags flags = default, int bands = 1);
}
