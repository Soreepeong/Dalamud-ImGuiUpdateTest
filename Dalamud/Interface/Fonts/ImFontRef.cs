using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal;

namespace Dalamud.Interface.Fonts;

/// <summary>A font made from a font spec, loaded or being loaded, with automatic lifecycle management.</summary>
/// <remarks>Holding a strong reference to this will keep the font alive. Do not hold a reference to
/// <see cref="ImFontPtr"/> across frames.</remarks>
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
    public FontSpec Spec => this.Entry.Spec;

    /// <summary>Gets a <see cref="Task"/> representing the load state of the font.</summary>
    public Task LoadTask => this.Entry.Load;

    /// <summary>Gets the font.</summary>
    internal FontResolver.Entry Entry { get; }

    public static implicit operator ImFontPtr(ImFontRef font) => font.resolver.Use(font.Entry);

    /// <summary>Pushes the font into the ImGui font stack, with its own style.</summary>
    /// <param name="sizePx">The size of the glyphs. It is in pixels before global scaling if positive, a ratio of the
    /// current font size if negative (-1.5 = 150%) or 0 (100%), or <see langword="null"/> for the spec's size. NaN
    /// and infinities count as 0, and so does <see langword="null"/> for fonts not made from a spec. All font pushes
    /// take sizes this way.</param>
    /// <returns>A disposable object that pops the font.</returns>
    /// <seealso cref="PushFamily"/>
    public ImPopFontDisposable Push(float? sizePx = null) => this.resolver.Push(this.Entry, sizePx);

    /// <summary>Pushes only the family of the font, keeping the style of the current font.</summary>
    /// <param name="sizePx"><inheritdoc cref="Push" path="/param[@name='sizePx']/text()"/></param>
    /// <returns>A disposable object that pops the font.</returns>
    /// <seealso cref="Push"/>
    public ImPopFontDisposable PushFamily(float? sizePx = null) =>
        this.resolver.Push(this.Entry, sizePx, ImGui.TextStyle);

    /// <inheritdoc/>
    public override string ToString() => $"{nameof(ImFontRef)}({this.Entry.Spec})";
}
