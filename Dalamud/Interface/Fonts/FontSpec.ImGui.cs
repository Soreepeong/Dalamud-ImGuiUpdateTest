using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal;

namespace Dalamud.Interface.Fonts;

public sealed partial record FontSpec
{
    /// <summary>Pushes the font of a spec into the ImGui font stack with its own style, loading it if needed.
    /// </summary>
    /// <param name="sizePx">The size, as for <see cref="ImFontRef.Push"/>.</param>
    /// <inheritdoc cref="ImFontRef.Push" path="/returns|/exception"/>
    /// <remarks><ul>
    /// <li><inheritdoc cref="ImFontRef.Push" path="/remarks/node()"/></li>
    /// <li>Use <see cref="GetFont"/> to load ahead. Keep the spec, such as in a field, or the
    /// <see cref="ImFontRef"/> of it. A spec made anew for each push is looked up by value, which takes longer.
    /// </li>
    /// </ul></remarks>
    /// <seealso cref="PushFamily"/>
    /// <example><code>
    /// using (FontSpec.Mono.Push())
    ///     ImGui.Text("Monospaced"u8);
    /// </code></example>
    public ImPopFontDisposable Push(float? sizePx = null) =>
        Service<FontResolver>.Get().Push(this, sizePx);

    /// <summary>Pushes only the family of the spec's font, keeping the style of the current font.</summary>
    /// <param name="sizePx">The size, as for <see cref="ImFontRef.Push"/>.</param>
    /// <returns>A disposable object that pops the font.</returns>
    /// <remarks><inheritdoc cref="Push" path="/remarks/node()"/></remarks>
    /// <seealso cref="Push"/>
    public ImPopFontDisposable PushFamily(float? sizePx = null) =>
        Service<FontResolver>.Get().Push(this, sizePx, ImGui.TextStyle);

    /// <summary>Gets the font of the spec, starting to load it in the background if needed.</summary>
    /// <param name="retry">Whether to load again if an earlier attempt failed.</param>
    /// <returns>The font, at once. <see cref="ImFontRef.LoadTask"/> completes when it is loaded.</returns>
    /// <remarks>It does not wait. Keep the result to keep the font loaded, and push it with
    /// <see cref="ImFontRef.Push"/>.</remarks>
    public ImFontRef GetFont(bool retry = false) => Service<FontResolver>.Get().Get(this, retry);
}
