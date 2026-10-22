using System.Collections.Generic;
using System.Numerics;

using Dalamud.Interface.Fonts.Internal.Glyphs;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>A font made of other fonts, such as <see cref="MultiFontSpec.Parts"/> and fallback fonts.</summary>
/// <remarks>Each codepoint comes from the first element whose ranges contain it and whose font has a glyph for it.
/// Glyph ids are (element index &lt;&lt; 24) | the element font's glyph id. Elements that are composite fonts
/// themselves are flattened. Elements may only be added until the font is handed to ImGui.</remarks>
internal sealed partial class CompositeFont : ManagedFont
{
    private const int MaxElements = 256;

    private readonly List<Element> elements = [];

    /// <summary>Glyph size, in pixels if positive, else as a ratio of the current font's glyphs.</summary>
    private readonly float nominalSize;

    private int terminalStart = -1;
    private uint[] codepointBuffer = new uint[64];
    private uint[] glyphBuffer = new uint[64];
    private int[] whereBuffer = new int[64];
    private GlyphMetrics[] metricsBuffer = new GlyphMetrics[64];
    private Vector2[] shiftBuffer = new Vector2[64];
    private float[] sizeBuffer = new float[64];
    private uint[] shapeCodepointBuffer = new uint[64];
    private uint[] shapeGlyphBuffer = new uint[64];
    private uint[] shapeLeafBuffer = new uint[64];
    private GlyphMetrics[] shapeMetricsBuffer = new GlyphMetrics[64];
    private GlyphMetrics[] shapeAdjustedBuffer = new GlyphMetrics[64];
    private Vector2[] shapeShiftBuffer = new Vector2[64];

    /// <summary>Initializes a new instance of the <see cref="CompositeFont"/> class, with a size in pixels.</summary>
    /// <param name="name">Name for debugging.</param>
    /// <param name="sizePx">The size the font is pushed at, and the unit of <see cref="Element"/> lengths.</param>
    public CompositeFont(string name, float sizePx)
        : base(name)
    {
        if (!(sizePx > 0))
            throw new ArgumentOutOfRangeException(nameof(sizePx), sizePx, "Must be positive; use CreateRelative.");
        this.BaseSizePx = sizePx;
        this.nominalSize = sizePx;
    }

    private CompositeFont(string name, float baseSizePx, float nominalSize)
        : base(name)
    {
        this.BaseSizePx = baseSizePx;
        this.nominalSize = nominalSize;
    }

    /// <summary>Gets the size whose pixels element offsets and advances are given in.</summary>
    /// <remarks>1 for relative fonts, whose offsets and advances are in ems.</remarks>
    public float BaseSizePx { get; }

    /// <summary>Gets the size the font is pushed at, which ImGui takes as the line height.</summary>
    /// <remarks>Pixels if positive, else a ratio of the current font size (-1.5 = 150%).</remarks>
    public float PushSize => this.ToPushSize(this.nominalSize);

    /// <summary>Gets a value indicating whether the font follows the current font size.</summary>
    public bool IsRelative => this.nominalSize <= 0;

    /// <summary>Gets the ratio of the glyphs to those of the current font size, for relative fonts.</summary>
    public float RelativeSize => this.nominalSize == 0 ? 1f : -this.nominalSize;

    /// <summary>Gets the line height relative to the glyph size.</summary>
    /// <remarks>The font is pushed at (and its line height is) this many times the size its glyphs are drawn at. Only
    /// applies to the font itself, not to it as an element of another font.</remarks>
    public float LineHeightScale { get; private set; } = 1f;

    /// <summary>Gets the part of <see cref="LineHeightScale"/> that is the font's own, not the current font's.</summary>
    public float OwnLineHeightScale { get; private set; } = 1f;

    /// <summary>Gets the elements.</summary>
    public IReadOnlyList<Element> Elements => this.elements;

    /// <inheritdoc/>
    public override bool CanShape => true;

    // The index of the first element added with AddTerminal, or the element count.
    private int TerminalStart => this.terminalStart >= 0 ? this.terminalStart : this.elements.Count;

    /// <summary>Creates a font that follows the current font size.</summary>
    /// <param name="name">Name for debugging.</param>
    /// <param name="pushSize">Ratio to the current font size, as a non-positive number (-1.5 = 150%, 0 = 100%).
    /// </param>
    /// <returns>The font.</returns>
    /// <remarks>Offsets and advances of its elements are in ems.</remarks>
    public static CompositeFont CreateRelative(string name, float pushSize) =>
        new(name, 1f, Math.Min(pushSize, 0f));

    /// <summary>Converts zero-terminated glyph ranges to sorted ranges.</summary>
    /// <param name="ranges">Pairs of inclusive bounds, terminated by 0, or empty for all.</param>
    /// <returns>The ranges, or null for all.</returns>
    public static (uint Low, uint High)[]? ToRanges(ReadOnlySpan<uint> ranges)
    {
        if (ranges.IsEmpty)
            return null;
        var list = new List<(uint, uint)>();
        for (var i = 0; i + 1 < ranges.Length && ranges[i] != 0; i += 2)
            list.Add((ranges[i], ranges[i + 1]));
        list.Sort();
        return list.ToArray();
    }

    /// <summary>Sets <see cref="LineHeightScale"/>. Only until the font is handed to ImGui.</summary>
    /// <param name="own">The font's own line height, such as <see cref="Fonts.SingleFontSpec.LineHeight"/>.
    /// </param>
    /// <param name="inherited">The line height of the font this one follows, which the current font size already
    /// includes.</param>
    public void SetLineHeightScale(float own, float inherited = 1f)
    {
        if (!(own > 0) || !(inherited > 0))
            throw new ArgumentOutOfRangeException(nameof(own), own, "Must be positive.");
        this.OwnLineHeightScale = own;
        this.LineHeightScale = own * inherited;
    }

    /// <summary>Converts a size given for the glyphs to the size to push the font at.</summary>
    /// <param name="sizePx">Pixels if positive, else a ratio of the glyphs of the current font size (0 = 100%).
    /// </param>
    /// <returns>Pixels if positive, else a ratio of the current font size.</returns>
    /// <remarks>The current font size already includes the current font's line height, so a ratio only takes the
    /// font's own.</remarks>
    public float ToPushSize(float sizePx) =>
        sizePx > 0 ? sizePx * this.LineHeightScale : -(sizePx == 0 ? 1f : -sizePx) * this.OwnLineHeightScale;

    /// <summary>Adds an element with the lowest priority, before those added with <see cref="AddTerminal"/>.
    /// </summary>
    /// <param name="element">The element.</param>
    public void Add(Element element)
    {
        var (normal, terminal) = this.Flatten(element);
        this.EnsureCapacity(normal.Count + terminal.Count);
        this.elements.InsertRange(this.TerminalStart, normal);
        if (this.terminalStart >= 0)
        {
            this.terminalStart += normal.Count;
        }
        else if (terminal.Count > 0)
        {
            this.terminalStart = this.elements.Count;
            this.elements.AddRange(terminal);
        }
    }

    /// <summary>Adds an element that ends the font, such as the default chain.</summary>
    /// <param name="element">The element.</param>
    /// <remarks>Elements added later go before it. Does nothing if the font already has an end.</remarks>
    public void AddTerminal(Element element)
    {
        if (this.terminalStart >= 0)
            return;
        var (normal, terminal) = this.Flatten(element);
        this.EnsureCapacity(normal.Count + terminal.Count);
        this.terminalStart = this.elements.Count;
        this.elements.AddRange(normal);
        this.elements.AddRange(terminal);
    }

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
        var missing = Rent(ref this.codepointBuffer, codepoints.Length);
        var where = Rent(ref this.whereBuffer, codepoints.Length);
        var leaf = Rent(ref this.glyphBuffer, codepoints.Length);
        for (var ei = 0; ei < this.elements.Count; ei++)
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
                if (leaf[i] != 0 && leaf[i] < 1u << 24)
                    glyphs[where[i]] = ((uint)ei << 24) | leaf[i];
            }
        }
    }

    /// <inheritdoc/>
    public override void GetGlyphMetrics(
        ReadOnlySpan<uint> glyphs, float size, float density, Span<GlyphMetrics> metrics) =>
        this.GlyphMetricsAt(glyphs, size / this.LineHeightScale, density, metrics);

    /// <inheritdoc/>
    public override void GetKerning(ReadOnlySpan<uint> glyphs, float size, float density, Span<float> adjustments)
    {
        size /= this.LineHeightScale;
        adjustments.Clear();
        var leaf = Rent(ref this.glyphBuffer, glyphs.Length);
        for (var begin = 0; begin < glyphs.Length;)
        {
            var end = RunEnd(glyphs, begin, 24);
            var ei = (int)(glyphs[begin] >> 24);
            if (end - begin >= 2 && ei < this.elements.Count && !this.elements[ei].FitToSquare)
            {
                var e = this.elements[ei];
                ToLeaf(glyphs[begin..end], leaf[begin..end]);
                e.Font.GetKerning(leaf[begin..end], size * e.SizeRatio, density, adjustments[begin..(end - 1)]);
            }

            begin = end;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Each grapheme goes to the element of its first codepoint, whose font shapes it. Pairs across elements
    /// are kerned with <see cref="ManagedFont.KernAcross"/>.</remarks>
    public override void Shape(ShapeRun run, ShapedGlyphList output)
    {
        if (run.End <= run.Begin)
            return;
        run = run.WithSize(run.Size / this.LineHeightScale);

        // The element of each grapheme.
        var graphemes = this.FindGraphemes(run);
        var graphemeCount = graphemes.Count;
        var firsts = Rent(ref this.shapeCodepointBuffer, graphemeCount);
        var chosen = Rent(ref this.shapeGlyphBuffer, graphemeCount);
        for (var k = 0; k < graphemeCount; k++)
            firsts[k] = run.Text[graphemes[k]];
        this.GetGlyphIndices(firsts, chosen);

        var previous = -1;
        var previousLast = 0u;
        var previousEnd = -1;
        Span<GlyphMetrics> notdef = [new() { Advance = -1 }];
        for (var k = 0; k < graphemeCount;)
        {
            var begin = graphemes[k];
            var ei = ElementOf(chosen[k]);
            var kb = k++;
            while (k < graphemeCount && ElementOf(chosen[k]) == ei)
                k++;

            var end = k < graphemeCount ? graphemes[k] : run.End;
            var first = output.Count;
            if (ei < 0)
            {
                // Missing, so glyph 0 with the advance the composite gives it.
                if (notdef[0].Advance < 0)
                    this.GlyphMetricsAt([0u], run.Size, run.Density, notdef);
                for (var g = kb; g < k; g++)
                    output.Add(0, graphemes[g], notdef[0].Advance);
            }
            else
            {
                var e = this.elements[ei];
                e.Font.Shape(run.Slice(begin, end).WithSize(run.Size * e.SizeRatio), output);
                this.AdjustShaped(e, ei, run.Size, run.Density, output.Span[first..]);
                if (previous >= 0 && previous != ei && previousEnd == first && first > 0 &&
                    !e.FitToSquare && !this.elements[previous].FitToSquare)
                {
                    var p = this.elements[previous];
                    output.Span[first - 1].Advance += KernAcross(
                        p.Font,
                        run.Size * p.SizeRatio,
                        e.Font,
                        run.Size * e.SizeRatio,
                        previousLast,
                        run.Text[begin],
                        run.Density);
                }
            }

            previous = ei;
            previousLast = run.Text[graphemes[k - 1]];
            previousEnd = output.Count;
        }
    }

    /// <inheritdoc/>
    public override void GetGlyphImages(
        ReadOnlySpan<uint> glyphs, ReadOnlySpan<byte> subpixelSteps, float size, float density, float embolden,
        bool subpixel, Span<GlyphImage> images)
    {
        size /= this.LineHeightScale;
        var devicePerBase = size / this.BaseSizePx * density;
        var leaf = Rent(ref this.glyphBuffer, glyphs.Length);
        for (var begin = 0; begin < glyphs.Length;)
        {
            var end = RunEnd(glyphs, begin, 24);
            var ei = (int)(glyphs[begin] >> 24);
            if (ei >= this.elements.Count)
            {
                images[begin..end].Fill(new() { IsFinal = true });
                begin = end;
                continue;
            }

            // The glyphs of the first element are their own leaf glyphs.
            var e = this.elements[ei];
            ReadOnlySpan<uint> runLeaf = leaf[begin..end];
            if (ei == 0)
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

            var metrics = Rent(ref this.metricsBuffer, glyphs.Length);
            var shift = Rent(ref this.shiftBuffer, glyphs.Length);
            var sizes = Rent(ref this.sizeBuffer, glyphs.Length);
            sizes[begin..end].Fill(size * e.SizeRatio);
            e.Font.GetGlyphMetrics(runLeaf, size * e.SizeRatio, density, metrics[begin..end]);
            this.Adjust(e, runLeaf, size, density, metrics[begin..end], shift[begin..end], sizes[begin..end]);

            // One call per image size, as fit-to-square glyphs may each have their own.
            for (var b = begin; b < end;)
            {
                var c = b + 1;
                while (c < end && sizes[c] == sizes[b])
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
    }

    /// <inheritdoc/>
    /// <remarks>Each element takes the bold or italic form of its own font.</remarks>
    protected override ManagedFont CreateStyled(bool bold, bool italic)
    {
        var font = new CompositeFont(this.Name, this.BaseSizePx, this.nominalSize)
        {
            LineHeightScale = this.LineHeightScale,
            OwnLineHeightScale = this.OwnLineHeightScale,
            terminalStart = this.terminalStart,
        };
        foreach (var e in this.elements)
            font.elements.Add(e with { Font = e.Font.WithStyle(bold, italic) });
        return font;
    }

    private static int ElementOf(uint glyph) => glyph == 0 ? -1 : (int)(glyph >> 24);

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
        return result.ToArray();
    }

    /// <summary>Splits an element into elements of this font, converted to this font's units.</summary>
    /// <remarks>A composite gives its own and its terminal elements. Its <see cref="LineHeightScale"/> does not carry
    /// over.</remarks>
    private (List<Element> Normal, List<Element> Terminal) Flatten(Element element)
    {
        var normal = new List<Element>();
        var terminal = new List<Element>();
        if (element.Font is not CompositeFont inner)
        {
            normal.Add(element);
            return (normal, terminal);
        }

        if (inner == this)
            throw new ArgumentException("A font cannot contain itself.", nameof(element));

        // Inner lengths are in inner.BaseSizePx units, for the inner font drawn at BaseSizePx * SizeRatio.
        var k = this.BaseSizePx * element.SizeRatio / inner.BaseSizePx;
        for (var i = 0; i < inner.elements.Count; i++)
        {
            var e = inner.elements[i];
            (i < inner.TerminalStart ? normal : terminal).Add(
                e with
                {
                    Ranges = Intersect(element.Ranges, e.Ranges),
                    SizeRatio = element.SizeRatio * e.SizeRatio,
                    Offset = element.Offset + (e.Offset * k),
                    ExtraAdvanceX = element.ExtraAdvanceX + (e.ExtraAdvanceX * k),
                    MinAdvanceX = element.MinAdvanceX > 0 ? element.MinAdvanceX : e.MinAdvanceX * k,
                    MaxAdvanceX = element.MaxAdvanceX < float.MaxValue ? element.MaxAdvanceX : e.MaxAdvanceX * k,
                    PixelSnapH = element.PixelSnapH || e.PixelSnapH,
                    Embolden = element.Embolden + (e.Embolden * k),
                    FitToSquare = element.FitToSquare || e.FitToSquare,
                });
        }

        return (normal, terminal);
    }

    // The metrics of the font at the size its glyphs are drawn at.
    private FontMetrics GlyphFontMetrics(float size, float density)
    {
        if (this.elements.Count == 0)
            return FontMetrics.Fallback(size);
        var e = this.elements[0];
        return e.Font.GetMetrics(size * e.SizeRatio, density);
    }

    // Glyph metrics at the size the glyphs are drawn at.
    private void GlyphMetricsAt(ReadOnlySpan<uint> glyphs, float size, float density, Span<GlyphMetrics> metrics)
    {
        var leaf = Rent(ref this.glyphBuffer, glyphs.Length);
        var shift = Rent(ref this.shiftBuffer, glyphs.Length);
        for (var begin = 0; begin < glyphs.Length;)
        {
            var end = RunEnd(glyphs, begin, 24);
            var ei = (int)(glyphs[begin] >> 24);
            if (ei >= this.elements.Count)
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
    }

    private void EnsureCapacity(int adding)
    {
        if (this.elements.Count + adding > MaxElements)
            throw new InvalidOperationException($"A font may have at most {MaxElements} elements.");
    }
}
