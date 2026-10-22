namespace Dalamud.Interface.Fonts.Internal.Glyphs;

/// <summary>An 8-bit coverage bitmap of a glyph.</summary>
internal struct GlyphBitmap
{
    /// <summary>Width.</summary>
    public int Width;

    /// <summary>Height.</summary>
    public int Height;

    /// <summary>Left relative to the pen.</summary>
    public int OffsetX;

    /// <summary>Top relative to the baseline (y down).</summary>
    public int OffsetY;

    /// <summary>Coverage, row by row.</summary>
    /// <remarks>For subpixel glyphs, the largest of the three per pixel. For color glyphs, the alpha.</remarks>
    public byte[] Alpha;

    /// <summary>Red, green and blue coverage per pixel, row by row, or null if not a subpixel glyph.</summary>
    public byte[]? Rgb;

    /// <summary>Premultiplied red, green, blue and alpha per pixel, row by row, or null if not a color glyph.</summary>
    /// <remarks>Layers in the text color are black here, and in <see cref="Mask"/>.</remarks>
    public byte[]? Rgba;

    /// <summary>How much of the text color to add per pixel of a color glyph, row by row, or null if none.</summary>
    /// <remarks>The coverage of the layers in the text color, less what layers above them cover.</remarks>
    public byte[]? Mask;

    /// <summary>Gets the kind of the glyph.</summary>
    public readonly GlyphKind Kind =>
        this.Rgba is not null ? GlyphKind.Color : this.Rgb is not null ? GlyphKind.Subpixel : GlyphKind.Grayscale;

    /// <summary>Widens the glyph to the right, as synthetic bold.</summary>
    /// <param name="amount">Pixels.</param>
    public void Embolden(float amount)
    {
        if (amount <= 0 || this.Width == 0)
            return;

        var w = this.Width + (int)MathF.Ceiling(amount);
        this.Alpha = Embolden(this.Alpha, this.Width, w, this.Height, 1, amount);
        if (this.Rgb is not null)
            this.Rgb = Embolden(this.Rgb, this.Width, w, this.Height, 3, amount);
        if (this.Rgba is not null)
            this.Rgba = Embolden(this.Rgba, this.Width, w, this.Height, 4, amount);
        if (this.Mask is not null)
            this.Mask = Embolden(this.Mask, this.Width, w, this.Height, 1, amount);
        this.Width = w;
    }

    private static byte[] Embolden(byte[] src, int srcWidth, int dstWidth, int height, int channels, float amount)
    {
        var whole = (int)amount;
        var frac = amount - whole;
        var dst = new byte[dstWidth * height * channels];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < dstWidth; x++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var v = 0;
                    for (var k = 0; k <= whole; k++)
                        v = Math.Max(v, At(x - k));
                    if (frac > 0)
                        v = Math.Max(v, (int)(At(x - whole - 1) * frac));
                    dst[(((y * dstWidth) + x) * channels) + c] = (byte)v;

                    int At(int sx) => sx >= 0 && sx < srcWidth ? src[(((y * srcWidth) + sx) * channels) + c] : 0;
                }
            }
        }

        return dst;
    }
}
