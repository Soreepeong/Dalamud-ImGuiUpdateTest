using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Logging.Internal;
using Dalamud.Utility;

using TerraFX.Interop.DirectX;

namespace Dalamud.Interface.Fonts.Internal.DirectWrite;

/// <summary>A <see cref="DirectWriteFontFace"/> shaped in a locale, and maybe slanted.</summary>
/// <remarks>The face has the metrics and the glyph images. So fonts of the same face share their glyph images.
/// </remarks>
internal sealed unsafe partial class DirectWriteFont : Font
{
    private static readonly ModuleLog Log = new(typeof(DirectWriteFont));

    private readonly DirectWriteFontFace face;
    private readonly string locale;
    private readonly float slant;
    private readonly int slantId;
    private ushort[] indexBuffer = [];
    private DWRITE_GLYPH_METRICS[] metricsBuffer = [];

    /// <summary>Initializes a new instance of the <see cref="DirectWriteFont"/> class.</summary>
    /// <param name="name">Name for debugging.</param>
    /// <param name="face">The face, shared with other fonts.</param>
    /// <param name="locale">The locale to shape text in, such as "ja-JP".</param>
    /// <param name="slant">Horizontal shift per pixel above the baseline to slant the outlines by, for synthetic
    /// italic.</param>
    public DirectWriteFont(string name, DirectWriteFontFace face, string locale, float slant = 0)
        : base(name)
    {
        this.face = face;
        this.locale = locale;
        this.slant = slant;
        this.slantId = face.SlantIdOf(slant);
    }

    /// <inheritdoc/>
    public override FontMetrics GetMetrics(float size, float density) => this.face.GetMetrics(size);

    /// <inheritdoc/>
    public override void GetGlyphIndices(ReadOnlySpan<uint> codepoints, Span<uint> glyphs)
    {
        var pooledIndices = PooledSpan<ushort>.FromBuffer(ref this.indexBuffer, codepoints.Length);
        var indices = pooledIndices.Span;
        fixed (uint* pCodepoints = codepoints)
        {
            fixed (ushort* pIndices = indices)
                this.face.Native->GetGlyphIndices(pCodepoints, (uint)codepoints.Length, pIndices).ThrowOnError();
        }

        for (var i = 0; i < codepoints.Length; i++)
            glyphs[i] = indices[i];
        pooledIndices.Recycle();
    }

    /// <inheritdoc/>
    public override void GetGlyphMetrics(
        ReadOnlySpan<uint> glyphs, float size, float density, Span<GlyphMetrics> metrics)
    {
        var pooledIndices = PooledSpan<ushort>.FromBuffer(ref this.indexBuffer, glyphs.Length);
        var indices = pooledIndices.Span;
        for (var i = 0; i < glyphs.Length; i++)
            indices[i] = (ushort)glyphs[i];
        var pooledDesign = PooledSpan<DWRITE_GLYPH_METRICS>.FromBuffer(ref this.metricsBuffer, glyphs.Length);
        var design = pooledDesign.Span;
        fixed (ushort* pIndices = indices)
        {
            fixed (DWRITE_GLYPH_METRICS* pDesign = design)
            {
                this.face.Native->GetDesignGlyphMetrics(pIndices, (uint)glyphs.Length, pDesign, false)
                    .ThrowOnError();
            }
        }

        var scale = this.face.Scale(size);
        for (var i = 0; i < glyphs.Length; i++)
        {
            ref readonly var m = ref design[i];
            var advance = m.advanceWidth * scale;
            var x0 = m.leftSideBearing * scale;
            var x1 = ((int)m.advanceWidth - m.rightSideBearing) * scale;
            var y0 = (m.topSideBearing - m.verticalOriginY) * scale;
            var y1 = ((int)m.advanceHeight - m.bottomSideBearing - m.verticalOriginY) * scale;
            // Slanting moves what is below the baseline left, and what is above it right.
            metrics[i] = x1 > x0 && y1 > y0
                             ? new()
                             {
                                 Advance = advance,
                                 Bounds = new(
                                     x0 - (Math.Max(y1, 0) * this.slant),
                                     y0,
                                     x1 - (Math.Min(y0, 0) * this.slant),
                                     y1),
                             }
                             : new() { Advance = advance };
        }

        pooledIndices.Recycle();
        pooledDesign.Recycle();
    }

    /// <inheritdoc/>
    /// <remarks>The face rasterizes and caches them, with this font's slant.</remarks>
    public override void GetGlyphImages(
        ReadOnlySpan<uint> glyphs, ReadOnlySpan<byte> subpixelSteps, float size, float density, float embolden,
        bool subpixel, Span<GlyphImage> images) =>
        this.face.GetImages(glyphs, subpixelSteps, size, density, embolden, subpixel, this.slant, this.slantId, images);

    /// <inheritdoc/>
    /// <remarks>Takes the faces of the installed family of the same name if it has them, and has DirectWrite simulate
    /// the rest.</remarks>
    protected override Font CreateStyled(bool bold, bool italic)
    {
        try
        {
            var styled = this.face.GetStyled(bold, italic, out var slanted);
            return new DirectWriteFont(
                $"{this.Name}{(bold ? " bold" : null)}{(italic ? " italic" : null)}",
                styled,
                this.locale,
                slanted ? SyntheticStyleFont.Slant : this.slant);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[{Font}] Its styled face could not be made. It is emboldened or slanted", this);
            return base.CreateStyled(bold, italic);
        }
    }
}
