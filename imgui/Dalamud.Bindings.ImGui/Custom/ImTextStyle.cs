using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Dalamud.Bindings.ImGui;

/// <summary>Colors and effects of a text.</summary>
/// <remarks>Equal styles share fonts and layouts. Effects that are off are stored as their defaults, so that styles that
/// look the same are equal.</remarks>
/// <seealso cref="ImFont.Style"/>
[StructLayout(LayoutKind.Sequential)]
public record struct ImTextStyle
{
    private const byte ItalicBit = 0b1;
    private const byte BoldBit = 0b10;
    private const byte MarkupMask = 0b1100;
    private const int MarkupShift = 2;

    /// <summary>Color of the text but with alpha multiplied, or 0 to use the color the text is drawn with.</summary>
    public ImTextColor Color;

    private byte flags;

    /// <summary>Gets or sets the shadow of the glyphs and their edge.</summary>
    /// <remarks>One that is off (<see cref="ImTextShadow.IsOn"/>) is stored as the default.</remarks>
    public ImTextShadow Shadow
    {
        get;
        set => field = value.IsOn ? value : default;
    }

    /// <summary>Gets or sets the edge around the glyphs.</summary>
    /// <remarks>One that is off (<see cref="ImTextEdge.IsOn"/>) is stored as the default.</remarks>
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
    /// <remarks>Lines that are off (<see cref="ImTextLineDecoration.IsOn"/>) are stored as the default, and a count of
    /// 0 as 1.</remarks>
    public ImTextLineDecoration Lines
    {
        get;
        set => field = value.IsOn ? value with { Count = Math.Max(value.Count, (byte)1) } : default;
    }

    /// <summary>Gets or sets one more decoration, drawn after <see cref="Background"/> and <see cref="Lines"/>.</summary>
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

    public readonly ImTextStyle WithBold(bool bold = true) => this with { Bold = bold };
    public readonly ImTextStyle WithItalic(bool italic = true) => this with { Italic = italic };
    public readonly ImTextStyle WithColor(ImTextColor color) => this with { Color = color };
    public readonly ImTextStyle WithMarkup(ImTextMarkup markup) => this with { Markup = markup };
    public readonly ImTextStyle WithShadow(ImTextColor color) => this.WithShadow(color, ImTextShadow.DefaultOffset);
    public readonly ImTextStyle WithShadow(ImTextColor color, Vector2 offset) => this.WithShadow(new(color, offset));

    [OverloadResolutionPriority(1)]
    public readonly ImTextStyle WithShadow(in ImTextShadow shadow) => this with { Shadow = shadow };

    public readonly ImTextStyle WithEdge(
        ImTextColor color, float width = ImTextEdge.DefaultWidth, float softness = 0, float minWidth = 0,
        float maxWidth = 0) => this.WithEdge(new(color, width, softness, minWidth, maxWidth));

    [OverloadResolutionPriority(1)]
    public readonly ImTextStyle WithEdge(in ImTextEdge edge) => this with { Edge = edge };

    public readonly ImTextStyle WithBackground(ImTextColor color) => this with { Background = color };

    public readonly ImTextStyle WithLines(
        ImTextLinePosition positions,
        ImTextLineStyle style = ImTextLineStyle.Solid,
        ImTextColor color = default,
        float thickness = 0,
        byte count = 1) => this.WithLines(new(positions, style, color, thickness, count));

    [OverloadResolutionPriority(1)]
    public readonly ImTextStyle WithLines(in ImTextLineDecoration decoration) => this with { Lines = decoration };

    /// <summary>Gets the style but with another <see cref="ExtraDecoration"/>.</summary>
    /// <param name="decoration">The decoration.</param>
    /// <returns>The style.</returns>
    public readonly ImTextStyle WithDecoration(ImTextDecoration decoration) =>
        this with { ExtraDecoration = decoration };

    /// <summary>Gets the style without the decorations of a kind.</summary>
    /// <param name="id">The kind. <see cref="ImTextDecorationId.Background"/> and <see cref="ImTextDecorationId.Line"/>
    /// also clear <see cref="Background"/> and <see cref="Lines"/>.</param>
    /// <returns>The style.</returns>
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
