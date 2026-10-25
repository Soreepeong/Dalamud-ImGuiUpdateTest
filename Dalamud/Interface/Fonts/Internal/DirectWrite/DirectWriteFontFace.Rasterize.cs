using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.Sources;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Dalamud.Interface.Fonts.Internal.DirectWrite;

/// <summary>Glyph images, rasterized by DirectWrite, or by the color glyph rasterizer for color glyphs.</summary>
/// <remarks>Glyphs beyond the frame's budget, glyphs of color fonts and large glyphs are rasterized on the thread
/// pool, through the queue the face was made with.</remarks>
/// <seealso cref="CreateDeferral"/>
internal sealed unsafe partial class DirectWriteFontFace
{
    // The most slants the image keys of a face tell apart.
    private const int MaxSlants = 64;

    private readonly GlyphImageCache cache = new();

    /// <summary>Makes a queue that rasterizes glyphs of DirectWrite faces on the thread pool.</summary>
    /// <returns>The queue, for the faces of one owner to share.</returns>
    public static ConcurrentGlyphQueue<GlyphRasterRequest> CreateDeferral() =>
        new(static r => r.Face.Rasterize(r), static (r, bitmap) => r.Face.AddRasterized(r.Key, bitmap));

    /// <summary>Produces glyph images. Called on the main thread.</summary>
    /// <param name="glyphs">The glyphs.</param>
    /// <param name="subpixelSteps">Per glyph, how far the pen is right of a whole device pixel, in
    /// 1/<see cref="Font.SubpixelStepCount"/> pixel steps.</param>
    /// <param name="size">Size in logical pixels.</param>
    /// <param name="density">Device pixels per logical pixel.</param>
    /// <param name="embolden">Synthetic bold, in device pixels to widen the strokes by horizontally.</param>
    /// <param name="subpixel">Whether subpixel coverage is wanted.</param>
    /// <param name="slant">Horizontal shift per pixel above the baseline to slant the outlines by.</param>
    /// <param name="slantId">The id of <paramref name="slant"/>, from <see cref="SlantIdOf"/>.</param>
    /// <param name="images">Receives one entry per glyph.</param>
    public void GetImages(
        ReadOnlySpan<uint> glyphs, ReadOnlySpan<byte> subpixelSteps, float size, float density, float embolden,
        bool subpixel, float slant, int slantId, Span<GlyphImage> images)
    {
        this.Deferral?.DrainOncePerFrame();
        var sizeQ = (int)((size * density * Font.SizeStepsPerPixel) + 0.5f);
        subpixel &= SubpixelSettings.Enabled;

        // Glyphs of a color font may have many layers. Telling which have color takes as long as drawing them.
        var costly = (this.HasColorGlyphs && this.ColorGlyphs is not null) ||
                     sizeQ > GlyphRasterBudget.MaxInlineSize * Font.SizeStepsPerPixel;

        // The bits are glyph 0-15, size in quarter pixels 16-29 and 41-57, step 30-31, embolden 32-39, subpixel 40,
        // slant 58-63.
        var common = ((ulong)(sizeQ & 0x3FFF) << 16)
                     | (GlyphImageCache.Quantize8(embolden) << 32)
                     | (subpixel ? 1ul << 40 : 0)
                     | ((ulong)(((uint)sizeQ >> 14) & 0x1FFFF) << 41)
                     | ((ulong)slantId << 58);
        for (var i = 0; i < glyphs.Length; i++)
        {
            var subX = subpixelSteps[i] & (Font.SubpixelStepCount - 1);
            var key = glyphs[i] | ((ulong)subX << 30) | common;
            if (!this.cache.TryGet(key, out var image))
            {
                var request = new GlyphRasterRequest(
                    this,
                    key,
                    glyphs[i],
                    (float)sizeQ / Font.SizeStepsPerPixel,
                    (float)subX / Font.SubpixelStepCount,
                    subpixel,
                    embolden,
                    slant);
                if (this.Deferral?.TryDefer(this, key, request, costly) == true)
                {
                    images[i] = default;
                    continue;
                }

                var start = FontProfiler.Begin(FontProfiler.Section.Rasterize);
                var measured = GlyphRasterBudget.Measure();
                var bitmap = this.Rasterize(request);
                GlyphRasterBudget.EndMeasure(measured);
                FontProfiler.End(FontProfiler.Section.Rasterize, start);
                image = this.cache.Add(key, bitmap);
            }

            // The subpixel shift is in the bitmap, which is relative to the pen minus SubpixelX, as asked.
            images[i] = GlyphAtlas.Shared.ToImage(image, 1f, default);
            images[i].CorrectCoverage = true;
        }
    }

    /// <summary>Rasterizes a glyph, with its synthetic bold. Called on any thread, concurrently.</summary>
    /// <param name="request">What to rasterize.</param>
    /// <returns>The bitmap, or null if the glyph has no image.</returns>
    public GlyphBitmap? Rasterize(in GlyphRasterRequest request)
    {
        if (!this.Rasterize(
                (ushort)request.Glyph,
                request.DeviceSize,
                request.SubpixelX,
                request.Subpixel,
                request.Slant,
                out var bitmap))
        {
            return null;
        }

        bitmap.Embolden(request.Embolden);
        return bitmap;
    }

    // Caches a glyph rasterized on the thread pool.
    private void AddRasterized(ulong key, GlyphBitmap? bitmap)
    {
        // Text drawn synchronously in the meantime may have rasterized it.
        if (!this.cache.TryGet(key, out _))
            _ = this.cache.Add(key, bitmap);
    }

    // Where a color glyph may draw, relative to the pen. It is the line and the advance, with a quarter em around them.
    // Color glyphs have no outline to take bounds from.
    private RECT ColorCanvas(ushort glyph, float deviceSize, float slant)
    {
        DWRITE_GLYPH_METRICS m;
        if (this.face->GetDesignGlyphMetrics(&glyph, 1, &m, false).FAILED)
            m = default;
        var scale = this.Scale(deviceSize);
        var margin = (this.unitsPerEm * scale) / 4;
        var top = (this.designAscent * scale) + margin;
        var bottom = (this.designDescent * scale) + margin;
        return new(
            -(int)MathF.Ceiling(margin + (bottom * slant)),
            -(int)MathF.Ceiling(top),
            (int)MathF.Ceiling((m.advanceWidth * scale) + margin + (top * slant)),
            (int)MathF.Ceiling(bottom));
    }

    private bool Rasterize(
        ushort glyph, float deviceSize, float subpixelX, bool subpixel, float slant, out GlyphBitmap bitmap)
    {
        bitmap = default;
        var emSize = this.EmSize(deviceSize);
        // Without a color rasterizer, or if it fails, color glyphs fall back to their outlines.
        if (this.HasColorGlyphs &&
            this.ColorGlyphs is not null &&
            this.ColorGlyphs.TryRasterize(
                this.face,
                glyph,
                emSize,
                subpixelX,
                slant,
                this.ColorCanvas(glyph, deviceSize, slant),
                out bitmap))
        {
            return true;
        }

        // Synthetic italic slants the outlines around the baseline (y is down).
        var shear = new DWRITE_MATRIX { m11 = 1, m21 = -slant, m22 = 1 };

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
        if (DirectWriteUtilities.Factory->CreateGlyphRunAnalysis(
                &run,
                slant == 0 ? null : &shear,
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

        // Subpixel glyphs take three texels per pixel. Ones too wide for that are in grayscale, so that they show.
        // DirectWrite has no subpixel image of large ones at all.
        if (subpixel && (w <= 0 || h <= 0 || !GlyphAtlas.Fits(GlyphKind.Subpixel, w, h)))
            return this.Rasterize(glyph, deviceSize, subpixelX, false, slant, out bitmap);
        if (w <= 0 || h <= 0)
            return false;

        var data = new byte[w * h * (subpixel ? 3 : 1)];
        fixed (byte* pData = data)
        {
            if (analysis.Get()->CreateAlphaTexture(type, &bounds, pData, (uint)data.Length).FAILED)
                return false;
        }

        // Subpixel coverage is red first. The shader reverses it for monitors in blue, green, red order.
        bitmap = new()
        {
            Width = w,
            Height = h,
            OffsetX = bounds.left,
            OffsetY = bounds.top,
            Kind = subpixel ? GlyphKind.Subpixel : GlyphKind.Grayscale,
            Pixels = data,
        };
        return true;
    }
}
