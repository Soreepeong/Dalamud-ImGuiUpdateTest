using System.Collections.Generic;
using System.Linq;

using Dalamud.Interface.Fonts.Internal.Formats;
using Dalamud.Interface.Fonts.Internal.Glyphs;

using Serilog;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>A game font family, such as AXIS, drawn from the game's font textures.</summary>
/// <remarks>Each .fdt file is a bitmap strike. Each size is drawn from the smallest strike at least as large, scaled.
/// Glyph ids are the codepoints.</remarks>
internal sealed class GameFontFamilyFont : ManagedFont
{
    private readonly Strike[] strikes;
    private readonly Func<int, Texture> getTexture;
    private readonly Dictionary<int, Texture?> textures = [];
    private readonly GlyphImageCache cache = new();

    /// <summary>Initializes a new instance of the <see cref="GameFontFamilyFont"/> class.</summary>
    /// <param name="name">Name for debugging.</param>
    /// <param name="strikes">The .fdt files of the family and their horizontal glyph offsets.</param>
    /// <param name="getTexture">Gets a font texture by file index (font1.tex = 0).</param>
    public GameFontFamilyFont(
        string name, IEnumerable<(FdtFileView Fdt, int HorizontalOffset)> strikes, Func<int, Texture> getTexture)
        : base(name)
    {
        this.getTexture = getTexture;
        this.strikes = strikes.Select(x => new Strike(x.Fdt, x.HorizontalOffset)).OrderBy(x => x.Px).ToArray();
        if (this.strikes.Length == 0)
            throw new ArgumentException("A font family needs at least one strike.", nameof(strikes));
    }

    /// <inheritdoc/>
    public override FontMetrics GetMetrics(float size, float density)
    {
        var strike = this.StrikeFor(size * density, out var k);
        k /= density;
        var thickness = Math.Max(1f, MathF.Round(size / 14f));

        // The files have no strikethrough metrics. The strikethrough goes through the middle of lowercase letters.
        return new(
            strike.Ascent * k,
            (strike.LineHeight - strike.Ascent) * k,
            strike.LineHeight * k,
            Math.Max(1f, MathF.Round((strike.LineHeight - strike.Ascent) * k * 0.4f)),
            thickness,
            MathF.Round((size * 0.28f) + (thickness * 0.5f)),
            thickness);
    }

    /// <inheritdoc/>
    public override void GetGlyphIndices(ReadOnlySpan<uint> codepoints, Span<uint> glyphs)
    {
        for (var i = 0; i < codepoints.Length; i++)
            glyphs[i] = codepoints[i] != 0 && this.HasGlyph(codepoints[i]) ? codepoints[i] : 0;
    }

    /// <inheritdoc/>
    public override void GetGlyphMetrics(
        ReadOnlySpan<uint> glyphs, float size, float density, Span<GlyphMetrics> metrics)
    {
        var strike = this.StrikeFor(size * density, out var k);
        k /= density;
        for (var i = 0; i < glyphs.Length; i++)
        {
            if (!strike.Fdt.TryGetGlyph(glyphs[i], out var g))
            {
                metrics[i] = default;
                continue;
            }

            metrics[i] = new()
            {
                Advance = g.AdvanceWidth * k,
                Bounds = g.BoundingWidth == 0 || g.BoundingHeight == 0
                             ? default
                             : new(
                                 strike.HorizontalOffset * k,
                                 (g.CurrentOffsetY - strike.Ascent) * k,
                                 (strike.HorizontalOffset + g.BoundingWidth) * k,
                                 (g.CurrentOffsetY + g.BoundingHeight - strike.Ascent) * k),
            };
        }
    }

    /// <inheritdoc/>
    public override void GetKerning(ReadOnlySpan<uint> glyphs, float size, float density, Span<float> adjustments)
    {
        var strike = this.StrikeFor(size * density, out var k);
        k /= density;
        for (var i = 0; i + 1 < glyphs.Length; i++)
            adjustments[i] = strike.Fdt.GetKerning(glyphs[i], glyphs[i + 1]) * k;
    }

    /// <inheritdoc/>
    public override void GetGlyphImages(
        ReadOnlySpan<uint> glyphs, ReadOnlySpan<byte> subpixelSteps, float size, float density, float embolden,
        bool subpixel, Span<GlyphImage> images)
    {
        var strikeIndex = this.StrikeIndexFor(size * density);
        var strike = this.strikes[strikeIndex];
        var k = size * density / strike.Px; // device pixels per strike pixel

        // Synthetic bold is applied at the strike's resolution.
        embolden /= k;
        for (var i = 0; i < glyphs.Length; i++)
        {
            var key = glyphs[i]
                      | ((ulong)strikeIndex << 24)
                      | (GlyphImageCache.Quantize8(embolden) << 32);
            if (!this.cache.TryGet(key, out var image))
            {
                var start = FontProfiler.Begin(FontProfiler.Section.Rasterize);
                var bitmap = this.Rasterize(strike, glyphs[i]);
                FontProfiler.End(FontProfiler.Section.Rasterize, start);
                image = this.cache.Add(key, bitmap, embolden);
            }

            // Placed at the exact pen, as the bitmap is not shifted.
            images[i] = GlyphAtlas.Shared.ToImage(image, k, new((float)subpixelSteps[i] / SubpixelStepCount, 0));
        }
    }

    private bool HasGlyph(uint codepoint)
    {
        foreach (var s in this.strikes)
        {
            if (s.Fdt.TryGetGlyph(codepoint, out _))
                return true;
        }

        return false;
    }

    private Strike StrikeFor(float deviceSize, out float scale)
    {
        var strike = this.strikes[this.StrikeIndexFor(deviceSize)];
        scale = deviceSize / strike.Px;
        return strike;
    }

    private int StrikeIndexFor(float deviceSize)
    {
        for (var i = 0; i < this.strikes.Length; i++)
        {
            if (this.strikes[i].Px >= deviceSize - 0.01f)
                return i;
        }

        return this.strikes.Length - 1;
    }

    private GlyphBitmap? Rasterize(Strike strike, uint codepoint)
    {
        if (!strike.Fdt.TryGetGlyph(codepoint, out var g) || g.BoundingWidth == 0 || g.BoundingHeight == 0)
            return null;

        if (!this.textures.TryGetValue(g.TextureFileIndex, out var tex))
        {
            try
            {
                tex = this.getTexture(g.TextureFileIndex);
            }
            catch (Exception e)
            {
                // Left null by TryGetValue, so it is remembered as missing.
                Log.Error(e, "[{Font}] Failed to load font texture {Index}", this, g.TextureFileIndex);
            }

            this.textures.Add(g.TextureFileIndex, tex);
        }

        if (tex is null)
            return null;

        var width = tex.Width;
        var height = tex.Height;
        if (g.TextureOffsetX + g.BoundingWidth > width || g.TextureOffsetY + g.BoundingHeight > height)
            return null;

        var channel = g.TextureChannelByteIndex;
        var alpha = new byte[g.BoundingWidth * g.BoundingHeight];
        var source = tex.Pixels;
        var is4444 = tex.IsB4G4R4A4;
        for (var y = 0; y < g.BoundingHeight; y++)
        {
            for (var x = 0; x < g.BoundingWidth; x++)
            {
                var pixel = ((g.TextureOffsetY + y) * width) + g.TextureOffsetX + x;
                alpha[(y * g.BoundingWidth) + x] = is4444
                                                       ? (byte)(((source[(pixel * 2) + (channel / 2)] >>
                                                                  ((channel & 1) * 4)) & 0xF) * 17)
                                                       : source[(pixel * 4) + channel];
            }
        }

        return new GlyphBitmap
        {
            Width = g.BoundingWidth,
            Height = g.BoundingHeight,
            OffsetX = strike.HorizontalOffset,
            OffsetY = g.CurrentOffsetY - strike.Ascent,
            Alpha = alpha,
        };
    }

    /// <summary>A game font texture (common/font/fontN.tex).</summary>
    /// <remarks>Every glyph lives in one channel of one texture.</remarks>
    /// <param name="Width">Width.</param>
    /// <param name="Height">Height.</param>
    /// <param name="IsB4G4R4A4">Whether the pixels are B4G4R4A4 (as shipped) rather than B8G8R8A8.</param>
    /// <param name="Pixels">The pixels, row by row.</param>
    public sealed record Texture(int Width, int Height, bool IsB4G4R4A4, byte[] Pixels);

    private sealed record Strike(FdtFileView Fdt, int HorizontalOffset)
    {
        public float Px { get; } = Fdt.GlyphTableHeader.Size * 4 / 3;

        public int LineHeight => this.Fdt.GlyphTableHeader.LineHeight;

        public int Ascent => this.Fdt.GlyphTableHeader.Ascent;
    }
}
