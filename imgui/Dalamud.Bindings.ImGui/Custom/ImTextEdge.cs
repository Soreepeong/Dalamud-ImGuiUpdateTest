using System.Runtime.InteropServices;

namespace Dalamud.Bindings.ImGui;

/// <summary>An edge around the glyphs of a text.</summary>
/// <remarks><ol>
/// <li>The widths and the softness are stored in a byte each, so that the edge takes little room in
/// <see cref="ImTextStyle"/>. Values are rounded to their steps and limited to their ranges.</li>
/// <li><see cref="Width"/> is in 1/256 em steps, up to 255/256 em.</li>
/// <li><see cref="Softness"/> is in 1/64 steps, up to 255/64, as the glyph shader takes it.</li>
/// <li><see cref="MinWidth"/> and <see cref="MaxWidth"/> are in 1/16 pixel steps, up to 255/16 pixels.</li>
/// <li>The glyph shader draws edges up to 8 texels of the glyph's bitmap wide.</li>
/// </ol></remarks>
/// <seealso cref="ImTextStyle.Edge"/>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ImTextEdge
{
    /// <summary>The default width, in ems.</summary>
    public const float DefaultWidth = 1 / 16f;

    /// <summary><see cref="Width"/> in 1/256 em steps.</summary>
    public readonly byte PackedWidth;

    /// <summary><see cref="Softness"/> in 1/64 steps, as the glyph shader takes it.</summary>
    public readonly byte PackedSoftness;

    /// <summary><see cref="MinWidth"/> in 1/16 pixel steps.</summary>
    public readonly byte PackedMinWidth;

    /// <summary><see cref="MaxWidth"/> in 1/16 pixel steps.</summary>
    public readonly byte PackedMaxWidth;

    private const float WidthStep = 1 / 256f;
    private const float SoftnessStep = 1 / 64f;
    private const float PixelStep = 1 / 16f;

    /// <summary>Initializes a new instance of the <see cref="ImTextEdge"/> struct.</summary>
    /// <param name="color">The color, or alpha 0 for no edge.</param>
    /// <param name="width">The width in ems.</param>
    /// <param name="softness">How soft the edge is, as <see cref="Softness"/>, or 0 for a hard edge.</param>
    /// <param name="minWidth">The least width in pixels, or 0 for no limit.</param>
    /// <param name="maxWidth">The most width in pixels, or 0 for no limit.</param>
    public ImTextEdge(
        ImTextColor color, float width = DefaultWidth, float softness = 0, float minWidth = 0, float maxWidth = 0)
    {
        this.Color = color;
        this.PackedWidth = Quantize(width, WidthStep);
        this.PackedSoftness = Quantize(softness, SoftnessStep);
        this.PackedMinWidth = Quantize(minWidth, PixelStep);
        this.PackedMaxWidth = Quantize(maxWidth, PixelStep);
    }

    /// <summary>Gets the color, or alpha 0 for no edge.</summary>
    public ImTextColor Color { get; init; }

    /// <summary>Gets the width in ems.</summary>
    public float Width
    {
        get => this.PackedWidth * WidthStep;
        init => this.PackedWidth = Quantize(value, WidthStep);
    }

    /// <summary>Gets how soft the edge is, or 0 for a hard edge.</summary>
    /// <remarks><ol>
    /// <li>Up to 1, it is the fraction of the width the edge fades over, ending at the width.</li>
    /// <li>Above 1, the edge fades from the glyph out to the width times it, within what the shader draws.</li>
    /// </ol></remarks>
    public float Softness
    {
        get => this.PackedSoftness * SoftnessStep;
        init => this.PackedSoftness = Quantize(value, SoftnessStep);
    }

    /// <summary>Gets the least width in pixels, or 0 for no limit.</summary>
    public float MinWidth
    {
        get => this.PackedMinWidth * PixelStep;
        init => this.PackedMinWidth = Quantize(value, PixelStep);
    }

    /// <summary>Gets the most width in pixels, or 0 for no limit.</summary>
    /// <remarks>It applies after <see cref="MinWidth"/>.</remarks>
    public float MaxWidth
    {
        get => this.PackedMaxWidth * PixelStep;
        init => this.PackedMaxWidth = Quantize(value, PixelStep);
    }

    /// <summary>Gets a value indicating whether there is an edge.</summary>
    public bool IsOn => this.Color.Alpha != 0 && (this.PackedWidth != 0 || this.PackedMinWidth != 0);

    /// <summary>Gets the width in pixels of the edge of text at a size.</summary>
    /// <param name="fontSize">The font size in pixels.</param>
    /// <returns>The width, limited by <see cref="MinWidth"/> and <see cref="MaxWidth"/>.</returns>
    public float WidthAt(float fontSize)
    {
        var w = Math.Max(this.Width * fontSize, this.MinWidth);
        return this.PackedMaxWidth != 0 ? Math.Min(w, this.MaxWidth) : w;
    }

    private static byte Quantize(float value, float step) =>
        (byte)Math.Clamp(MathF.Round(value / step), 0, byte.MaxValue);
}
