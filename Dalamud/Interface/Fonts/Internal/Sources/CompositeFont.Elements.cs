using System.Numerics;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>Elements, and how they change the glyphs of their fonts.</summary>
internal sealed partial class CompositeFont
{
    // Applies an element's advance rules to glyphs its font shaped, and tags the glyph ids with the element.
    // Offsets are applied to the images instead.
    private void AdjustShaped(Element e, int ei, float size, float density, Span<ShapedGlyph> glyphs)
    {
        if (e.FitToSquare || e.HasAdvanceAdjustments)
        {
            var leaf = Rent(ref this.shapeLeafBuffer, glyphs.Length);
            var nominal = Rent(ref this.shapeMetricsBuffer, glyphs.Length);
            var adjusted = Rent(ref this.shapeAdjustedBuffer, glyphs.Length);
            var shift = Rent(ref this.shapeShiftBuffer, glyphs.Length);
            for (var i = 0; i < glyphs.Length; i++)
                leaf[i] = glyphs[i].Glyph;
            e.Font.GetGlyphMetrics(leaf, size * e.SizeRatio, density, nominal);
            nominal.CopyTo(adjusted);
            this.Adjust(e, leaf, size, density, adjusted, shift, default);
            for (var i = 0; i < glyphs.Length; i++)
            {
                ref var g = ref glyphs[i];
                if (e.FitToSquare)
                {
                    g.Advance = adjusted[i].Advance;
                    g.Offset = default;
                }
                else if (g.Advance != 0)
                {
                    g.Advance += adjusted[i].Advance - nominal[i].Advance;
                }
            }
        }

        foreach (ref var g in glyphs)
            g.Glyph = g.Glyph is not 0 and < 1u << 24 ? ((uint)ei << 24) | g.Glyph : 0;
    }

    /// <summary>Applies an element's offsets and advance rules to metrics from its font.</summary>
    /// <remarks>Also computes the shift of the glyph images in logical pixels, and with <paramref name="sizes"/>, the
    /// size to draw each image at.</remarks>
    private void Adjust(
        Element e,
        ReadOnlySpan<uint> leaf,
        float size,
        float density,
        Span<GlyphMetrics> metrics,
        Span<Vector2> shift,
        Span<float> sizes)
    {
        var k = size / this.BaseSizePx; // logical pixels per base pixel
        var offset = Snap(e.Offset * k, density);
        if (e.FitToSquare)
        {
            var ascent = this.GlyphFontMetrics(size, density).Ascent;
            Span<GlyphMetrics> one = stackalloc GlyphMetrics[1];
            for (var i = 0; i < metrics.Length; i++)
            {
                var b = metrics[i].Bounds;
                var w = b.Z - b.X;
                var h = b.W - b.Y;
                var ratio = Math.Max(1f, Math.Max(w / size, h / size));
                var glyphSize = size * e.SizeRatio / ratio;
                if (ratio > 1f)
                {
                    // Glyph images are made at sizes in quarters of a device pixel.
                    glyphSize = MathF.Floor(glyphSize * density * 4f) / (density * 4f);
                    e.Font.GetGlyphMetrics(leaf.Slice(i, 1), glyphSize, density, one);
                    b = one[0].Bounds;
                    w = b.Z - b.X;
                    h = b.W - b.Y;
                }

                // Shifted by whole device pixels, so that the image is not resampled.
                var x0 = (size - w) / 2f;
                var y0 = ((size - h) / 2f) - ascent;
                var s = Snap(new(x0 - b.X, y0 - b.Y), density) + offset;
                shift[i] = s;
                metrics[i] = new()
                {
                    Advance = size,
                    Bounds = w > 0 && h > 0 ? b + new Vector4(s.X, s.Y, s.X, s.Y) : default,
                };
                if (!sizes.IsEmpty)
                    sizes[i] = glyphSize;
            }

            return;
        }

        for (var i = 0; i < metrics.Length; i++)
        {
            ref var m = ref metrics[i];
            var advance = m.Advance;
            if (e.PixelSnapH)
                advance = MathF.Round(advance * density) / density;
            var dx = 0f;
            if (e.MinAdvanceX > 0 || e.MaxAdvanceX < float.MaxValue)
            {
                var clamped = Math.Clamp(advance, e.MinAdvanceX * k, Math.Max(e.MinAdvanceX, e.MaxAdvanceX) * k);
                dx = (clamped - advance) / 2f;
                if (e.PixelSnapH)
                    dx = MathF.Truncate(dx * density) / density;
                advance = clamped;
            }

            advance += e.ExtraAdvanceX * k;
            var s = offset + new Vector2(dx, 0);
            shift[i] = s;
            m.Advance = advance;
            if (m.Bounds != default)
                m.Bounds += new Vector4(s.X, s.Y, s.X, s.Y);
        }
    }

    /// <summary>A font in a composite, with the rules from its <see cref="Fonts.SingleFontSpec"/>.</summary>
    /// <param name="Font">The font.</param>
    /// <param name="Ranges">Codepoint ranges taken from the font, sorted, or null for all.</param>
    /// <param name="SizeRatio">The font is drawn at the composite's size times this.</param>
    /// <param name="Offset">Glyph offset.</param>
    /// <param name="ExtraAdvanceX">Added to advances.</param>
    /// <param name="MinAdvanceX">Minimum advance, with glyphs centered.</param>
    /// <param name="MaxAdvanceX">Maximum advance.</param>
    /// <param name="PixelSnapH">Round advances to whole pixels.</param>
    /// <param name="Embolden">Synthetic bold.</param>
    /// <param name="FitToSquare">Scale glyphs down to fit a square of the font size, centered.</param>
    /// <remarks>Lengths are in pixels at the composite's <see cref="BaseSizePx"/>.</remarks>
    public sealed record Element(
        ManagedFont Font,
        (uint Low, uint High)[]? Ranges = null,
        float SizeRatio = 1f,
        Vector2 Offset = default,
        float ExtraAdvanceX = 0f,
        float MinAdvanceX = 0f,
        float MaxAdvanceX = float.MaxValue,
        bool PixelSnapH = false,
        float Embolden = 0f,
        bool FitToSquare = false)
    {
        /// <summary>Gets a value indicating whether advances change.</summary>
        public bool HasAdvanceAdjustments =>
            this.PixelSnapH || this.MinAdvanceX > 0 || this.MaxAdvanceX < float.MaxValue || this.ExtraAdvanceX != 0;

        /// <summary>Gets a value indicating whether where glyph images go or their size depends on the glyphs'
        /// metrics.</summary>
        /// <remarks>Clamped advances center the images, and fitting to a square scales them. Snapped or extra
        /// advances leave them where <see cref="Offset"/> puts them.</remarks>
        public bool PlacesImagesByMetrics =>
            this.FitToSquare || this.MinAdvanceX > 0 || this.MaxAdvanceX < float.MaxValue;

        /// <summary>Tests whether a codepoint is in <see cref="Ranges"/>.</summary>
        /// <param name="codepoint">The codepoint.</param>
        /// <returns>Whether the element takes it.</returns>
        public bool Contains(uint codepoint)
        {
            if (this.Ranges is null)
                return true;
            foreach (var (low, high) in this.Ranges)
            {
                if (codepoint < low)
                    return false;
                if (codepoint <= high)
                    return true;
            }

            return false;
        }
    }
}
