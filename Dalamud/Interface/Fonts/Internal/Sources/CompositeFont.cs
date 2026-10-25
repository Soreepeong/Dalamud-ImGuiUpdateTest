using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Utility;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>A font made of other fonts, such as the font of a <see cref="Fonts.FontSpec"/> and its fallbacks.
/// </summary>
/// <remarks><ul>
/// <li>Each codepoint comes from the first element whose ranges contain it and whose font has a glyph for it.</li>
/// <li>Glyph ids are (element index &lt;&lt; 24) | the element font's glyph id. Glyph 0 and the font metrics come
/// from the first element that is not a <see cref="Element.Supplement"/>.</li>
/// <li>Elements that are composite fonts themselves are flattened.</li>
/// <li>It does not change once made.</li>
/// </ul></remarks>
internal sealed partial class CompositeFont : MultiSourceFont
{
    private const int MaxElements = 256;

    // Glyph ids hold the element index above this bit, and the glyph id of its font below.
    private const int ElementShift = 24;
    private const uint MaxLeafGlyph = 1u << ElementShift;

    private readonly Element[] elements;

    /// <summary>Glyph size, in pixels if positive, else as a ratio of the current font's glyphs.</summary>
    private readonly float nominalSize;

    // The index of the first element that is not a supplement, or 0.
    private readonly int primary;

    private uint[] codepointBuffer = new uint[64];
    private uint[] glyphBuffer = new uint[64];
    private int[] whereBuffer = new int[64];
    private GlyphMetrics[] metricsBuffer = new GlyphMetrics[64];
    private Vector2[] shiftBuffer = new Vector2[64];
    private float[] sizeBuffer = new float[64];
    private uint[] shapeGlyphBuffer = new uint[64];
    private uint[] shapeLeafBuffer = new uint[64];
    private GlyphMetrics[] shapeMetricsBuffer = new GlyphMetrics[64];
    private GlyphMetrics[] shapeAdjustedBuffer = new GlyphMetrics[64];
    private Vector2[] shapeShiftBuffer = new Vector2[64];

    /// <summary>Initializes a new instance of the <see cref="CompositeFont"/> class, with a size in pixels.</summary>
    /// <param name="name">Name for debugging.</param>
    /// <param name="sizePx">The size the font is pushed at, and the unit of <see cref="Element"/> lengths.</param>
    /// <param name="elements">The elements, in priority order. Those marked <see cref="Element.Terminal"/> go last.
    /// </param>
    /// <param name="ownLineHeight">The font's own line height, such as <see cref="Fonts.FontSpec.LineHeight"/>.
    /// </param>
    /// <param name="inheritedLineHeight">The line height of the font this one follows, which the current font size
    /// already includes.</param>
    public CompositeFont(
        string name, float sizePx, ReadOnlySpan<Element> elements, float ownLineHeight = 1f,
        float inheritedLineHeight = 1f)
        : this(name, RequirePositive(sizePx), sizePx, elements, ownLineHeight, inheritedLineHeight)
    {
    }

    private CompositeFont(
        string name, float baseSizePx, float nominalSize, ReadOnlySpan<Element> elements, float ownLineHeight,
        float inheritedLineHeight)
        : base(name)
    {
        if (!(ownLineHeight > 0))
            throw new ArgumentOutOfRangeException(nameof(ownLineHeight), ownLineHeight, "Must be positive.");
        if (!(inheritedLineHeight > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(inheritedLineHeight),
                inheritedLineHeight,
                "Must be positive.");
        }

        this.BaseSizePx = baseSizePx;
        this.nominalSize = nominalSize;
        this.OwnLineHeightScale = ownLineHeight;
        this.LineHeightScale = ownLineHeight * inheritedLineHeight;
        this.elements = this.Flatten(elements);
        this.primary = Math.Max(Array.FindIndex(this.elements, static e => !e.Supplement), 0);
    }

    // A copy with other element fonts, for CreateStyled.
    private CompositeFont(CompositeFont source, string name, Element[] elements)
        : base(name)
    {
        this.BaseSizePx = source.BaseSizePx;
        this.nominalSize = source.nominalSize;
        this.OwnLineHeightScale = source.OwnLineHeightScale;
        this.LineHeightScale = source.LineHeightScale;
        this.elements = elements;
        this.primary = source.primary;
    }

    /// <summary>Gets the size whose pixels element offsets and advances are given in.</summary>
    /// <remarks>1 for relative fonts, whose offsets and advances are in ems.</remarks>
    public float BaseSizePx { get; }

    /// <summary>Gets the size the font is pushed at, which ImGui takes as the line height.</summary>
    /// <remarks>Pixels if positive, else a ratio of the current font size (-1.5 = 150%).</remarks>
    public float PushSize => this.ToPushSize(this.nominalSize);

    /// <summary>Gets the ratio of the glyphs to those of the current font size, for relative fonts.</summary>
    public float RelativeSize => this.nominalSize == 0 ? 1f : -this.nominalSize;

    /// <summary>Gets the elements.</summary>
    public IReadOnlyList<Element> Elements => this.elements;

    // The line height relative to the glyph size. The font is pushed at (and its line height is) this many times the
    // size its glyphs are drawn at. Only applies to the font itself, not to it as an element of another font.
    private float LineHeightScale { get; }

    // The part of LineHeightScale that is the font's own, not the current font's.
    private float OwnLineHeightScale { get; }

    /// <summary>Creates a font that follows the current font size.</summary>
    /// <param name="name">Name for debugging.</param>
    /// <param name="pushSize">Ratio to the current font size, as a non-positive number (-1.5 = 150%, 0 = 100%).
    /// </param>
    /// <param name="elements">The elements, as for the constructor.</param>
    /// <param name="ownLineHeight">The font's own line height.</param>
    /// <param name="inheritedLineHeight">The line height of the font this one follows.</param>
    /// <returns>The font.</returns>
    /// <remarks>Offsets and advances of its elements are in ems.</remarks>
    public static CompositeFont CreateRelative(
        string name, float pushSize, ReadOnlySpan<Element> elements, float ownLineHeight = 1f,
        float inheritedLineHeight = 1f)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pushSize, 0);
        return new(name, 1f, pushSize, elements, ownLineHeight, inheritedLineHeight);
    }

    /// <summary>Converts <see cref="Fonts.FontSpec.CodepointRanges"/> to sorted ranges.</summary>
    /// <param name="ranges">Pairs of inclusive bounds, which may end with a zero.</param>
    /// <returns>The ranges. Empty for none.</returns>
    public static (uint Low, uint High)[] ToRanges(ReadOnlySpan<uint> ranges)
    {
        var list = new List<(uint, uint)>();
        for (var i = 0; i + 1 < ranges.Length && ranges[i] != 0; i += 2)
            list.Add((ranges[i], ranges[i + 1]));
        list.Sort();
        return [.. list];
    }

    /// <summary>Converts a size given for the glyphs to the size to push the font at.</summary>
    /// <param name="sizePx">Pixels if positive, else a ratio of the glyphs of the current font size (0 = 100%).
    /// </param>
    /// <returns>Pixels if positive, else a ratio of the current font size.</returns>
    /// <remarks>The current font size already includes the current font's line height, so a ratio only takes the
    /// font's own.</remarks>
    public float ToPushSize(float sizePx) =>
        sizePx > 0 ? sizePx * this.LineHeightScale : -(sizePx == 0 ? 1f : -sizePx) * this.OwnLineHeightScale;

    /// <inheritdoc/>
    /// <remarks>The ascent and the descent are those of the glyphs, and the line height is that of the font pushed at
    /// <paramref name="size"/>.</remarks>
    public override FontMetrics GetMetrics(float size, float density)
    {
        var m = this.GlyphFontMetrics(size / this.LineHeightScale, density);
        return m with { LineHeight = m.LineHeight * this.LineHeightScale };
    }

    /// <inheritdoc/>
    public override void GetGlyphIndices(ReadOnlySpan<uint> codepoints, Span<uint> glyphs)
    {
        glyphs[..codepoints.Length].Clear();
        var pooledMissing = PooledSpan<uint>.FromBuffer(ref this.codepointBuffer, codepoints.Length);
        var pooledWhere = PooledSpan<int>.FromBuffer(ref this.whereBuffer, codepoints.Length);
        var pooledLeaf = PooledSpan<uint>.FromBuffer(ref this.glyphBuffer, codepoints.Length);
        var missing = pooledMissing.Span;
        var where = pooledWhere.Span;
        var leaf = pooledLeaf.Span;
        for (var ei = 0; ei < this.elements.Length; ei++)
        {
            var e = this.elements[ei];
            var n = 0;
            for (var i = 0; i < codepoints.Length; i++)
            {
                if (glyphs[i] == 0 && e.Contains(codepoints[i]))
                {
                    missing[n] = codepoints[i];
                    where[n++] = i;
                }
            }

            if (n == 0)
                continue;
            e.Font.GetGlyphIndices(missing[..n], leaf[..n]);
            for (var i = 0; i < n; i++)
            {
                if (leaf[i] != 0 && leaf[i] < MaxLeafGlyph)
                    glyphs[where[i]] = ((uint)ei << ElementShift) | leaf[i];
            }
        }

        pooledMissing.Recycle();
        pooledWhere.Recycle();
        pooledLeaf.Recycle();
    }

    /// <inheritdoc/>
    public override void GetGlyphMetrics(
        ReadOnlySpan<uint> glyphs, float size, float density, Span<GlyphMetrics> metrics) =>
        this.GlyphMetricsAt(glyphs, size / this.LineHeightScale, density, metrics);

    /// <inheritdoc/>
    /// <remarks>Elements are the sources of <see cref="MultiSourceFont"/>.</remarks>
    public override void Shape(ShapeRun run, ShapedGlyphList output) =>
        this.ShapeBySource(run.WithSize(run.Size / this.LineHeightScale), output);

    /// <inheritdoc/>
    public override void GetGlyphImages(
        ReadOnlySpan<uint> glyphs, ReadOnlySpan<byte> subpixelSteps, float size, float density, float embolden,
        bool subpixel, Span<GlyphImage> images)
    {
        size /= this.LineHeightScale;
        var devicePerBase = (size / this.BaseSizePx) * density;
        var pooledLeaf = PooledSpan<uint>.FromBuffer(ref this.glyphBuffer, glyphs.Length);
        var leaf = pooledLeaf.Span;

        // Taken by the first run whose images are placed by metrics.
        var pooledMetrics = default(PooledSpan<GlyphMetrics>);
        var pooledShift = default(PooledSpan<Vector2>);
        var pooledSizes = default(PooledSpan<float>);
        for (var begin = 0; begin < glyphs.Length;)
        {
            var end = this.ElementRunEnd(glyphs, begin, out var ei);
            if (ei >= this.elements.Length)
            {
                images[begin..end].Fill(new() { IsFinal = true });
                begin = end;
                continue;
            }

            // The glyphs of the first element, and glyph 0, are their own leaf glyphs.
            var e = this.elements[ei];
            ReadOnlySpan<uint> runLeaf = leaf[begin..end];
            if (glyphs[begin] < MaxLeafGlyph)
                runLeaf = glyphs[begin..end];
            else
                ToLeaf(glyphs[begin..end], leaf[begin..end]);

            // Without metrics, the run's images are of one size and moved by the offset alone, if any.
            if (!e.PlacesImagesByMetrics)
            {
                e.Font.GetGlyphImages(
                    runLeaf,
                    subpixelSteps[begin..end],
                    size * e.SizeRatio,
                    density,
                    embolden + (e.Embolden * devicePerBase),
                    subpixel,
                    images[begin..end]);
                var offset = Snap(e.Offset * (size / this.BaseSizePx), density) * density;
                if (offset != Vector2.Zero)
                {
                    foreach (ref var image in images[begin..end])
                        image.Rect += new Vector4(offset.X, offset.Y, offset.X, offset.Y);
                }

                begin = end;
                continue;
            }

            if (pooledMetrics.Span.IsEmpty)
            {
                pooledMetrics = PooledSpan<GlyphMetrics>.FromBuffer(ref this.metricsBuffer, glyphs.Length);
                pooledShift = PooledSpan<Vector2>.FromBuffer(ref this.shiftBuffer, glyphs.Length);
                pooledSizes = PooledSpan<float>.FromBuffer(ref this.sizeBuffer, glyphs.Length);
            }

            var metrics = pooledMetrics.Span;
            var shift = pooledShift.Span;
            var sizes = pooledSizes.Span;
            sizes[begin..end].Fill(size * e.SizeRatio);
            e.Font.GetGlyphMetrics(runLeaf, size * e.SizeRatio, density, metrics[begin..end]);
            this.Adjust(e, runLeaf, size, density, metrics[begin..end], shift[begin..end], sizes[begin..end]);

            // One call per image size, as fit-to-square glyphs may each have their own. Sizes are compared exactly, as
            // any difference is another image size.
            for (var b = begin; b < end;)
            {
                var c = b + 1;
                while (c < end && sizes[c].Equals(sizes[b]))
                    c++;
                e.Font.GetGlyphImages(
                    runLeaf[(b - begin)..(c - begin)],
                    subpixelSteps[b..c],
                    sizes[b],
                    density,
                    embolden + (e.Embolden * devicePerBase),
                    subpixel,
                    images[b..c]);
                b = c;
            }

            for (var i = begin; i < end; i++)
            {
                var d = shift[i] * density;
                images[i].Rect += new Vector4(d.X, d.Y, d.X, d.Y);
            }

            begin = end;
        }

        pooledLeaf.Recycle();
        pooledMetrics.Recycle();
        pooledShift.Recycle();
        pooledSizes.Recycle();
    }

    /// <inheritdoc/>
    /// <remarks>Each element takes the bold or italic form of its own font.</remarks>
    protected override Font CreateStyled(bool bold, bool italic)
    {
        var styled = new Element[this.elements.Length];
        for (var i = 0; i < styled.Length; i++)
            styled[i] = this.elements[i] with { Font = this.elements[i].Font.WithStyle(bold, italic) };
        return new CompositeFont(
            this,
            $"{this.Name}{(bold ? " bold" : null)}{(italic ? " italic" : null)}",
            styled);
    }

    /// <inheritdoc/>
    protected override void PickSources(ReadOnlySpan<uint> firsts, Span<int> sources)
    {
        var pooledChosen = PooledSpan<uint>.FromBuffer(ref this.shapeGlyphBuffer, firsts.Length);
        var chosen = pooledChosen.Span;
        this.GetGlyphIndices(firsts, chosen);
        for (var k = 0; k < firsts.Length; k++)
            sources[k] = chosen[k] == 0 ? -1 : (int)(chosen[k] >> ElementShift);
        pooledChosen.Recycle();
    }

    /// <inheritdoc/>
    protected override bool HasAll(int source, ReadOnlySpan<uint> codepoints, Span<uint> glyphs)
    {
        var e = this.elements[source];
        foreach (var c in codepoints)
        {
            if (!e.Contains(c))
                return false;
        }

        e.Font.GetGlyphIndices(codepoints, glyphs);
        foreach (var g in glyphs[..codepoints.Length])
        {
            if (g is 0 or >= MaxLeafGlyph)
                return false;
        }

        return true;
    }

    /// <inheritdoc/>
    protected override int SourceCount() => this.elements.Length;

    /// <inheritdoc/>
    /// <remarks>Missing graphemes are glyph 0, with the advance the composite gives it.</remarks>
    protected override void ShapeWith(int source, ShapeRun run, ReadOnlySpan<int> graphemes, ShapedGlyphList output)
    {
        if (source >= 0)
        {
            var e = this.elements[source];
            var first = output.Count;
            e.Font.Shape(run.WithSize(run.Size * e.SizeRatio), output);
            this.AdjustShaped(e, source, run.Size, run.Density, output.Span[first..]);
            return;
        }

        Span<uint> notdef = stackalloc uint[1];
        Span<GlyphMetrics> metrics = stackalloc GlyphMetrics[1];
        this.GlyphMetricsAt(notdef, run.Size, run.Density, metrics);
        foreach (var g in graphemes)
            output.Add(0, g, metrics[0].Advance);
    }

    /// <inheritdoc/>
    /// <remarks>Glyphs fit to squares are not kerned.</remarks>
    protected override Font? KerningFontOf(int source, float size, out float sourceSize)
    {
        var e = this.elements[source];
        sourceSize = size * e.SizeRatio;
        return e.FitToSquare ? null : e.Font;
    }

    private static float RequirePositive(float sizePx) =>
        sizePx > 0
            ? sizePx
            : throw new ArgumentOutOfRangeException(nameof(sizePx), sizePx, "Must be positive. Use CreateRelative.");

    // The glyph ids of the element's font, without the element.
    private static void ToLeaf(ReadOnlySpan<uint> glyphs, Span<uint> leaf)
    {
        for (var i = 0; i < glyphs.Length; i++)
            leaf[i] = glyphs[i] & 0xFFFFFF;
    }

    private static Vector2 Snap(Vector2 logical, float density) =>
        new(MathF.Round(logical.X * density) / density, MathF.Round(logical.Y * density) / density);

    private static (uint Low, uint High)[]? Intersect((uint Low, uint High)[]? a, (uint Low, uint High)[]? b)
    {
        if (a is null)
            return b;
        if (b is null)
            return a;
        var result = new List<(uint, uint)>();
        foreach (var x in a)
        {
            foreach (var y in b)
            {
                var lo = Math.Max(x.Low, y.Low);
                var hi = Math.Min(x.High, y.High);
                if (lo <= hi)
                    result.Add((lo, hi));
            }
        }

        result.Sort();
        return [.. result];
    }

    /// <summary>Flattens the elements into elements of this font, converted to this font's units, with the
    /// terminal ones last.</summary>
    /// <param name="elems">The elements.</param>
    /// <returns>The flattened elements.</returns>
    /// <remarks><ul>
    /// <li>A composite gives its own and its terminal elements. Its <see cref="LineHeightScale"/> does not carry
    /// over.</li>
    /// <li>The terminal elements come from the first element that has any. Later ones are left out.</li>
    /// </ul></remarks>
    private Element[] Flatten(ReadOnlySpan<Element> elems)
    {
        var normal = new List<Element>();
        List<Element>? terminal = null;
        var parts = new List<Element>();
        foreach (var element in elems)
        {
            parts.Clear();
            this.Flatten(element, parts);
            if (element.Terminal)
            {
                terminal ??= [.. parts];
                continue;
            }

            normal.AddRange(parts.Where(static e => !e.Terminal));
            if (terminal is null && parts.Any(static e => e.Terminal))
                terminal = [.. parts.Where(static e => e.Terminal)];
        }

        return normal.Count + (terminal?.Count ?? 0) > MaxElements
                   ? throw new InvalidOperationException($"A font may have at most {MaxElements} elements.")
                   : [.. normal, .. terminal ?? []];
    }

    // Splits an element into elements of this font. Parts of a terminal element are terminal.
    private void Flatten(Element element, List<Element> output)
    {
        if (element.Font is not CompositeFont inner)
        {
            output.Add(element);
            return;
        }

        // Inner lengths are in inner.BaseSizePx units, for the inner font drawn at BaseSizePx * SizeRatio.
        var k = (this.BaseSizePx * element.SizeRatio) / inner.BaseSizePx;
        foreach (var e in inner.elements)
        {
            output.Add(
                e with
                {
                    Ranges = Intersect(element.Ranges, e.Ranges),
                    SizeRatio = element.SizeRatio * e.SizeRatio,
                    Offset = element.Offset + (e.Offset * k),
                    ExtraAdvanceX = element.ExtraAdvanceX + (e.ExtraAdvanceX * k),
                    MinAdvanceX = element.MinAdvanceX > 0 ? element.MinAdvanceX : e.MinAdvanceX * k,
                    MaxAdvanceX = element.MaxAdvanceX < float.MaxValue ? element.MaxAdvanceX
                        : e.MaxAdvanceX < float.MaxValue ? e.MaxAdvanceX * k
                        : float.MaxValue,
                    PixelSnapH = element.PixelSnapH || e.PixelSnapH,
                    Embolden = element.Embolden + (e.Embolden * k),
                    FitToSquare = element.FitToSquare || e.FitToSquare,
                    Supplement = element.Supplement || e.Supplement,
                    Terminal = element.Terminal || e.Terminal,
                });
        }
    }

    // The metrics of the font at the size its glyphs are drawn at.
    private FontMetrics GlyphFontMetrics(float size, float density)
    {
        if (this.elements.Length == 0)
            return FontMetrics.Fallback(size);
        var e = this.elements[this.primary];
        return e.Font.GetMetrics(size * e.SizeRatio, density);
    }

    // The end of the glyphs from one element, and that element. Glyph 0 is a run of its own, from the primary element.
    private int ElementRunEnd(ReadOnlySpan<uint> glyphs, int begin, out int ei)
    {
        var end = begin + 1;
        if (glyphs[begin] == 0)
        {
            ei = this.primary;
            while (end < glyphs.Length && glyphs[end] == 0)
                end++;
            return end;
        }

        ei = (int)(glyphs[begin] >> ElementShift);
        while (end < glyphs.Length && glyphs[end] != 0 && glyphs[end] >> ElementShift == glyphs[begin] >> ElementShift)
            end++;
        return end;
    }

    // Glyph metrics at the size the glyphs are drawn at.
    private void GlyphMetricsAt(ReadOnlySpan<uint> glyphs, float size, float density, Span<GlyphMetrics> metrics)
    {
        var pooledLeaf = PooledSpan<uint>.FromBuffer(ref this.glyphBuffer, glyphs.Length);
        var pooledShift = PooledSpan<Vector2>.FromBuffer(ref this.shiftBuffer, glyphs.Length);
        var leaf = pooledLeaf.Span;
        var shift = pooledShift.Span;
        for (var begin = 0; begin < glyphs.Length;)
        {
            var end = this.ElementRunEnd(glyphs, begin, out var ei);
            if (ei >= this.elements.Length)
            {
                metrics[begin..end].Clear();
                begin = end;
                continue;
            }

            var e = this.elements[ei];
            ToLeaf(glyphs[begin..end], leaf[begin..end]);
            e.Font.GetGlyphMetrics(leaf[begin..end], size * e.SizeRatio, density, metrics[begin..end]);
            this.Adjust(e, leaf[begin..end], size, density, metrics[begin..end], shift[begin..end], default);
            begin = end;
        }

        pooledLeaf.Recycle();
        pooledShift.Recycle();
    }
}
