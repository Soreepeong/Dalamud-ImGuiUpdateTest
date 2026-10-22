using Dalamud.Interface.Fonts.Internal.Glyphs;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>Glyph images, rasterized by DirectWrite, or by the color glyph rasterizer for color glyphs.</summary>
/// <remarks>Glyphs may be rasterized on the thread pool. Color glyphs are drawn one at a time there, as the color glyph
/// rasterizer is single-threaded.</remarks>
/// <seealso cref="GlyphRasterQueue"/>
internal sealed unsafe partial class DirectWriteFont : IConcurrentGlyphRasterizer
{
    private readonly GlyphImageCache cache = new();

    /// <inheritdoc/>
    public override void GetGlyphImages(
        ReadOnlySpan<uint> glyphs, ReadOnlySpan<byte> subpixelSteps, float size, float density, float embolden,
        bool subpixel, Span<GlyphImage> images)
    {
        var sizeQ = (int)((size * density * 4f) + 0.5f);
        subpixel &= SubpixelSettings.Enabled;
        for (var i = 0; i < glyphs.Length; i++)
        {
            var subX = subpixelSteps[i] & (SubpixelStepCount - 1);
            var key = glyphs[i]
                      | ((ulong)(sizeQ & 0x3FFF) << 16)
                      | ((ulong)subX << 30)
                      | (GlyphImageCache.Quantize8(embolden) << 32)
                      | (subpixel ? 1ul << 40 : 0);
            if (!this.cache.TryGet(key, out var image))
            {
                var subpixelX = (float)subX / SubpixelStepCount;
                if (GlyphRasterQueue.TryDefer(new(this, key, glyphs[i], sizeQ / 4f, subpixelX, subpixel, embolden)))
                {
                    images[i] = default;
                    continue;
                }

                var start = FontProfiler.Begin(FontProfiler.Section.Rasterize);
                var measured = GlyphRasterQueue.Measure();
                GlyphBitmap? bitmap =
                    this.Rasterize((ushort)glyphs[i], sizeQ / 4f, subpixelX, subpixel, out var b) ? b : null;
                GlyphRasterQueue.EndMeasure(measured);
                FontProfiler.End(FontProfiler.Section.Rasterize, start);
                image = this.cache.Add(key, bitmap, embolden);
            }

            // The subpixel shift is in the bitmap, which is relative to the pen minus SubpixelX, as asked.
            images[i] = GlyphAtlas.Shared.ToImage(image, 1f, default);
            images[i].CorrectCoverage = true;
        }
    }

    /// <inheritdoc/>
    GlyphBitmap? IConcurrentGlyphRasterizer.RasterizeConcurrently(in GlyphRasterRequest request) =>
        this.Rasterize((ushort)request.Glyph, request.DeviceSize, request.SubpixelX, request.Subpixel, out var b)
            ? b
            : null;

    /// <inheritdoc/>
    void IConcurrentGlyphRasterizer.AddRasterized(in GlyphRasterRequest request, GlyphBitmap? bitmap)
    {
        // Text drawn synchronously in the meantime may have rasterized it.
        if (!this.cache.TryGet(request.Key, out _))
            _ = this.cache.Add(request.Key, bitmap, request.Embolden);
    }

    // Where a color glyph may draw, relative to the pen. It is the line and the advance, with a quarter em around them.
    // Color glyphs have no outline to take bounds from.
    private RECT ColorCanvas(ushort glyph, float deviceSize)
    {
        DWRITE_GLYPH_METRICS m;
        if (this.face->GetDesignGlyphMetrics(&glyph, 1, &m, false).FAILED)
            m = default;
        var scale = this.Scale(deviceSize);
        var margin = this.unitsPerEm * scale / 4;
        var top = (this.designAscent * scale) + margin;
        var bottom = (this.designDescent * scale) + margin;
        return new(
            -(int)MathF.Ceiling(margin + (bottom * this.slant)),
            -(int)MathF.Ceiling(top),
            (int)MathF.Ceiling((m.advanceWidth * scale) + margin + (top * this.slant)),
            (int)MathF.Ceiling(bottom));
    }

    private bool Rasterize(ushort glyph, float deviceSize, float subpixelX, bool subpixel, out GlyphBitmap bitmap)
    {
        bitmap = default;
        var emSize = this.Scale(deviceSize) * this.unitsPerEm;
        // Without a color rasterizer, or if it fails, color glyphs fall back to their outlines.
        if (this.hasColorGlyphs &&
            this.colorGlyphs is not null &&
            this.colorGlyphs.TryRasterize(
                this.face,
                glyph,
                emSize,
                subpixelX,
                this.slant,
                this.ColorCanvas(glyph, deviceSize),
                out bitmap))
        {
            return true;
        }

        // Synthetic italic slants the outlines around the baseline (y is down).
        var shear = new DWRITE_MATRIX { m11 = 1, m21 = -this.slant, m22 = 1 };

        var advance = 0f;
        var run = new DWRITE_GLYPH_RUN
        {
            fontFace = this.face,
            fontEmSize = emSize,
            glyphCount = 1,
            glyphIndices = &glyph,
            glyphAdvances = &advance,
        };

        using var analysis = default(ComPtr<IDWriteGlyphRunAnalysis>);
        if (((IDWriteFactory2*)Factory)->CreateGlyphRunAnalysis(
                &run,
                this.slant == 0 ? null : &shear,
                DWRITE_RENDERING_MODE.DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,
                DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL,
                DWRITE_GRID_FIT_MODE.DWRITE_GRID_FIT_MODE_DEFAULT,
                subpixel
                    ? DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_CLEARTYPE
                    : DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE,
                subpixelX,
                0,
                analysis.GetAddressOf()).FAILED)
        {
            return false;
        }

        // With grayscale antialiasing, the "aliased" texture holds 8-bit coverage. ClearType's holds it per subpixel,
        // in red, green, blue order.
        var type = subpixel
                       ? DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1
                       : DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1;
        RECT bounds;
        if (analysis.Get()->GetAlphaTextureBounds(type, &bounds).FAILED)
            return false;
        var w = bounds.right - bounds.left;
        var h = bounds.bottom - bounds.top;
        if (w <= 0 || h <= 0)
            return false;

        var data = new byte[w * h * (subpixel ? 3 : 1)];
        fixed (byte* pData = data)
        {
            if (analysis.Get()->CreateAlphaTexture(type, &bounds, pData, (uint)data.Length).FAILED)
                return false;
        }

        bitmap = new() { Width = w, Height = h, OffsetX = bounds.left, OffsetY = bounds.top, Alpha = data };
        if (subpixel)
        {
            // Red first. The shader reverses it for monitors in blue, green, red order.
            var alpha = new byte[w * h];
            for (int i = 0, j = 0; i < alpha.Length; i++, j += 3)
                alpha[i] = Math.Max(data[j], Math.Max(data[j + 1], data[j + 2]));

            bitmap.Alpha = alpha;
            bitmap.Rgb = data;
        }

        return true;
    }
}
