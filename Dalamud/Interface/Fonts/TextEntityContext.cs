using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts;

/// <summary>Where an entity of styled text is.</summary>
public readonly ref struct TextEntityContext
{
    /// <summary>Initializes a new instance of the <see cref="TextEntityContext"/> struct.</summary>
    /// <param name="markup">The markup of the text.</param>
    /// <param name="source">The text as read.</param>
    /// <param name="offset">The byte offset of the entity in <paramref name="source"/>.</param>
    /// <param name="font">The font, with the style at the offset.</param>
    /// <param name="fontSize">The font size.</param>
    public TextEntityContext(
        ImTextMarkup markup, ReadOnlySpan<byte> source, int offset, ImFontPtr font, float fontSize)
    {
        this.Markup = markup;
        this.Source = source;
        this.Offset = offset;
        this.Font = font;
        this.FontSize = fontSize;
    }

    /// <summary>Gets the markup of the text.</summary>
    public ImTextMarkup Markup { get; }

    /// <summary>Gets the text as read.</summary>
    /// <remarks>For <see cref="ImTextMarkup.MacroString"/>, it is the compiled SeString.</remarks>
    public ReadOnlySpan<byte> Source { get; }

    /// <summary>Gets the byte offset in <see cref="Source"/>.</summary>
    public int Offset { get; }

    /// <summary>Gets the font, with the style at <see cref="Offset"/>.</summary>
    /// <seealso cref="ImFont.Style"/>
    public ImFontPtr Font { get; }

    /// <summary>Gets the font size in pixels.</summary>
    public float FontSize { get; }
}
