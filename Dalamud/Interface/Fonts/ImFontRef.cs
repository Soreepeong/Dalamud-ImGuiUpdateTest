using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal;

namespace Dalamud.Interface.Fonts;

/// <summary>A loaded font, made from a font spec.</summary>
/// <remarks>
/// <para>Holding it keeps the font alive. Fonts nobody holds or uses are released some time after their last use.
/// </para>
/// <para><b>Keep the <see cref="ImFontRef"/>, not the <see cref="ImFontPtr"/>.</b> Converting to
/// <see cref="ImFontPtr"/> keeps the font alive until the frame being built has been drawn, and for a short while
/// after. A pointer kept beyond that, without its <see cref="ImFontRef"/>, may point to a released font, and then to
/// another font made later.</para>
/// <para>The pointer stays the same for as long as the font is alive, even when the default font changes. Only text
/// sizes and metrics may change then.</para>
/// <para>There is one <see cref="ImFontRef"/> per font at a time. Getting the same spec again gives the same object,
/// as long as it is held.</para>
/// <para>A font that could not be loaded has <see cref="Exception"/> set, and draws and pushes as the current font.
/// </para>
/// </remarks>
public sealed class ImFontRef
{
    private readonly FontResolver resolver;

    /// <summary>Initializes a new instance of the <see cref="ImFontRef"/> class.</summary>
    /// <param name="resolver">The resolver that made it.</param>
    /// <param name="entry">The font.</param>
    internal ImFontRef(FontResolver resolver, FontResolver.Entry entry)
    {
        this.resolver = resolver;
        this.Entry = entry;
    }

    /// <summary>Gets the spec of the font. Equal specs share one font.</summary>
    public IFontSpec Spec => this.Entry.Spec;

    /// <summary>Gets why the font could not be loaded, or <see langword="null"/> if it was loaded (or is being loaded again,
    /// after <see cref="Plugin.Services.IFontService.GetFontAsync"/> with retry).</summary>
    public FontLoadException? Exception => this.Entry.Exception;

    /// <summary>Gets the font.</summary>
    internal FontResolver.Entry Entry { get; }

    /// <summary>Gets the ImGui font, keeping it alive until the frame being built has been drawn.</summary>
    /// <param name="font">The font.</param>
    /// <returns>The ImGui font.</returns>
    public static implicit operator ImFontPtr(ImFontRef font) => font.resolver.Use(font.Entry);

    /// <summary>Pushes the font into the ImGui font stack.</summary>
    /// <param name="sizePx">The size of the glyphs. It is in pixels before global scaling if positive, a ratio of the
    /// current font size if negative (-1.5 = 150%) or 0 (100%), or <see langword="null"/> for the spec's size.</param>
    /// <returns>A disposable object that pops the font.</returns>
    /// <exception cref="InvalidOperationException">If called outside the main thread.</exception>
    /// <remarks>The spec's line height applies on top of the size, so ImGui's font size may differ. The global scale
    /// applies to pixel sizes. For a size that ignores it, pass the size divided by
    /// <see cref="Utility.ImGuiHelpers.GlobalScale"/>. Fonts pushed inside with a size of 100% follow that.</remarks>
    public IDisposable Push(float? sizePx = null) => this.resolver.Push(this.Entry, sizePx);

    /// <inheritdoc/>
    public override string ToString() => $"{nameof(ImFontRef)}({this.Entry.Spec})";
}
