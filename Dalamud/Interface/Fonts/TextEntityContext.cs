using System.Numerics;

using Dalamud.Bindings.ImGui;

using ManagedFont = Dalamud.Interface.Fonts.Internal.Sources.Font;

namespace Dalamud.Interface.Fonts;

/// <summary>Where an entity of styled text is.</summary>
public readonly ref struct TextEntityContext
{
    // If set, Font is its variant for the style, made on first read.
    private readonly ManagedFont? baseFont;
    private readonly ImTextStyle style;

    /// <summary>Initializes a new instance of the <see cref="TextEntityContext"/> struct.</summary>
    /// <param name="markup">The markup of the text.</param>
    /// <param name="source">The text as read.</param>
    /// <param name="offset">The byte offset of the entity in <paramref name="source"/>.</param>
    /// <param name="font">The font, with the style at the offset.</param>
    /// <param name="fontSize">The font size.</param>
    /// <param name="data">The data of the entity.</param>
    /// <param name="size">The size of the entity in pixels.</param>
    /// <param name="color">The color the text is drawn with.</param>
    internal TextEntityContext(
        ImTextMarkup markup, ReadOnlySpan<byte> source, int offset, ImFontPtr font, float fontSize, ulong data,
        Vector2 size, uint color)
    {
        this.Markup = markup;
        this.Source = source;
        this.Offset = offset;
        this.Font = font;
        this.FontSize = fontSize;
        this.Data = data;
        this.Size = size;
        this.Color = color;
    }

    /// <summary>Initializes a new instance of the <see cref="TextEntityContext"/> struct.</summary>
    /// <param name="markup">The markup of the text.</param>
    /// <param name="source">The text as read.</param>
    /// <param name="offset">The byte offset of the entity in <paramref name="source"/>.</param>
    /// <param name="font">The font.</param>
    /// <param name="style">The style at the offset.</param>
    /// <param name="fontSize">The font size.</param>
    /// <param name="data">The data of the entity.</param>
    /// <param name="size">The size of the entity in pixels.</param>
    /// <param name="color">The color the text is drawn with.</param>
    internal TextEntityContext(
        ImTextMarkup markup, ReadOnlySpan<byte> source, int offset, ManagedFont font, in ImTextStyle style,
        float fontSize, ulong data, Vector2 size, uint color)
    {
        this.Markup = markup;
        this.Source = source;
        this.Offset = offset;
        this.baseFont = font;
        this.style = style;
        this.FontSize = fontSize;
        this.Data = data;
        this.Size = size;
        this.Color = color;
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
    public unsafe ImFontPtr Font => this.baseFont is null ? field : new(this.baseFont.GetVariant(this.style));

    /// <summary>Gets the font size in pixels.</summary>
    public float FontSize { get; }

    /// <summary>Gets the <see cref="TextEntity.Data"/> of the entity being drawn.</summary>
    /// <remarks>It is 0 in <see cref="ITextMarkupCallback.TryGetEntity"/>, as there is no entity yet.</remarks>
    public ulong Data { get; }

    /// <summary>Gets the size in pixels of the entity being drawn.</summary>
    /// <remarks>A height relative to the line is in pixels here. It is zero in
    /// <see cref="ITextMarkupCallback.TryGetEntity"/>, as there is no entity yet.</remarks>
    /// <seealso cref="TextEntity.Size"/>
    public Vector2 Size { get; }

    /// <summary>Gets the color the text is drawn with.</summary>
    /// <remarks>Its alpha also applies to the colors of styles. Apply it to fade the entity with the text. It is 0 in
    /// <see cref="ITextMarkupCallback.TryGetEntity"/>, as nothing is drawn yet.</remarks>
    public uint Color { get; }
}
