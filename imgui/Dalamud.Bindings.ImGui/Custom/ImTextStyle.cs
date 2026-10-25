using System.Runtime.InteropServices;

namespace Dalamud.Bindings.ImGui;

/// <summary>Colors and effects of a text.</summary>
/// <remarks>Equal styles share fonts and layouts. Effects that are off are stored as their defaults, so that styles
/// that look the same are equal.</remarks>
/// <seealso cref="ImFont.Style"/>
[StructLayout(LayoutKind.Sequential)]
public record struct ImTextStyle
{
    private const byte ItalicBit = 0b1;
    private const byte BoldBit = 0b10;
    private const byte MarkupMask = 0b1100;
    private const int MarkupShift = 2;

    /// <summary>The color of the text. Its alpha is multiplied with the alpha of the color the text is drawn with.
    /// 0 means to use the color the text is drawn with.</summary>
    public ImTextColor Color;

    // Native TextEx reads the markup from this byte, at its offset in ImFont. Keep it there.
    private byte flags;

    /// <summary>Gets or sets the shadow of the glyphs and their edge.</summary>
    /// <remarks>One that is off is stored as the default.</remarks>
    /// <seealso cref="ImTextShadow.IsOn"/>
    public ImTextShadow Shadow
    {
        get;
        set => field = value.IsOn ? value : default;
    }

    /// <summary>Gets or sets the edge around the glyphs.</summary>
    /// <remarks>One that is off is stored as the default.</remarks>
    /// <seealso cref="ImTextEdge.IsOn"/>
    public ImTextEdge Edge
    {
        get;
        set => field = value.IsOn ? value : default;
    }

    /// <summary>Gets or sets the color behind the text, over the height of its line, or alpha 0 for none.</summary>
    /// <remarks>A color with alpha 0 is stored as the default.</remarks>
    public ImTextColor Background
    {
        get;
        set => field = value.Alpha != 0 ? value : default;
    }

    /// <summary>Gets or sets the lines under, over, or through the text.</summary>
    /// <remarks>Lines that are off are stored as the default, and a count of 0 as 1.</remarks>
    /// <seealso cref="ImTextLineDecoration.IsOn"/>
    public ImTextLineDecoration Lines
    {
        get;
        set => field = value.IsOn ? value with { Count = Math.Max(value.Count, (byte)1) } : default;
    }

    /// <summary>Gets or sets one more decoration, drawn after <see cref="Background"/> and <see cref="Lines"/>.
    /// </summary>
    /// <remarks>It can be a built-in one, such as a wavy underline under the solid one of <see cref="Lines"/>, or one
    /// a plugin registered. To draw several, register one decoration that draws them all. One of
    /// <see cref="ImTextDecorationId.None"/> is stored as the default.</remarks>
    public ImTextDecoration ExtraDecoration
    {
        get;
        set => field = value.IsNone ? default : value;
    }

    /// <summary>Gets or sets a value indicating whether to use bold fonts, faux or not.</summary>
    public bool Bold
    {
        readonly get => (this.flags & BoldBit) != 0;
        set => this.flags = (byte)(value ? this.flags | BoldBit : this.flags & ~BoldBit);
    }

    /// <summary>Gets or sets a value indicating whether to use italic fonts, faux or not.</summary>
    public bool Italic
    {
        readonly get => (this.flags & ItalicBit) != 0;
        set => this.flags = (byte)(value ? this.flags | ItalicBit : this.flags & ~ItalicBit);
    }

    /// <summary>Gets or sets how the text is read.</summary>
    public ImTextMarkup Markup
    {
        readonly get => (ImTextMarkup)((this.flags & MarkupMask) >> MarkupShift);
        set => this.flags = (byte)((this.flags & ~MarkupMask) | (((int)value << MarkupShift) & MarkupMask));
    }

    /// <summary>Returns a copy without the decorations of a kind.</summary>
    /// <param name="id">The kind. <see cref="ImTextDecorationId.Background"/> and
    /// <see cref="ImTextDecorationId.Line"/> also clear <see cref="Background"/> and <see cref="Lines"/>.</param>
    /// <returns>The copy.</returns>
    public readonly ImTextStyle WithoutDecoration(ImTextDecorationId id)
    {
        var style = this;
        if (id == ImTextDecorationId.Background)
            style.Background = default;
        else if (id == ImTextDecorationId.Line)
            style.Lines = default;
        if (style.ExtraDecoration.Id == id)
            style.ExtraDecoration = default;
        return style;
    }
}
