using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Utility;
using Dalamud.Utility.Unicode;

using Lumina.Text;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>Layout construction.</summary>
internal sealed partial class TextLayout
{
    private const uint SoftHyphen = 0xAD;
    private const uint ObjectReplacement = 0xFFFC;

    // Layouts given back are kept for making others, up to this many, of up to this many codepoints, so that text that
    // changes allocates little.
    private const int MaxPooled = 256;
    private const int MaxPooledCodepoints = 4096;

    private static readonly Stack<TextLayout> Pool = new();

    private BuildScratch? scratch;

    /// <summary>Initializes a new instance of the <see cref="TextLayout"/> class, not taken from the pool.</summary>
    /// <inheritdoc cref="Rent"/>
    public TextLayout(
        ManagedFont font, in TextLayoutDesc desc, in ImTextStyle defaultStyle, ReadOnlySpan<byte> text,
        int codeUnitSize, ReadOnlySpan<TextRun> runs, ReadOnlySpan<InlineObject> objects,
        ReadOnlySpan<int> sourceOffsets = default)
        : this() =>
        this.Init(font, desc, defaultStyle, text, codeUnitSize, runs, objects, sourceOffsets);

    private TextLayout()
    {
    }

    private BuildScratch Scratch => this.scratch ?? throw new InvalidOperationException("Not building.");

    /// <summary>Makes a layout, reusing one given back if there is one.</summary>
    /// <param name="font">The default font.</param>
    /// <param name="desc">Layout parameters.</param>
    /// <param name="defaultStyle">The style of text outside runs, and of runs without one.</param>
    /// <param name="text">The text, in bytes.</param>
    /// <param name="codeUnitSize">1 for UTF-8, 2 for UTF-16, 4 for UTF-32 (little endian).</param>
    /// <param name="runs">Attributed ranges.</param>
    /// <param name="objects">Inline objects, one per U+FFFC in the text.</param>
    /// <param name="sourceOffsets">The offset in another text of each byte of <paramref name="text"/> and of its
    /// end, ascending, if the layout reports offsets into that text. Runs are given in offsets into
    /// <paramref name="text"/>.</param>
    /// <returns>The layout.</returns>
    /// <remarks>Every offset of the layout is a byte offset into <paramref name="text"/>, or into the source text.
    /// </remarks>
    /// <seealso cref="Recycle"/>
    public static TextLayout Rent(
        ManagedFont font, in TextLayoutDesc desc, in ImTextStyle defaultStyle, ReadOnlySpan<byte> text,
        int codeUnitSize, ReadOnlySpan<TextRun> runs, ReadOnlySpan<InlineObject> objects,
        ReadOnlySpan<int> sourceOffsets = default)
    {
        TextLayout? layout;
        lock (Pool)
            Pool.TryPop(out layout);
        layout ??= new();
        layout.Init(font, desc, defaultStyle, text, codeUnitSize, runs, objects, sourceOffsets);
        return layout;
    }

    /// <summary>Gives the layout back, to make another from, as nothing uses it anymore.</summary>
    public void Recycle()
    {
        if (this.cps.Length > MaxPooledCodepoints)
            return;
        this.Reset();
        lock (Pool)
        {
            if (Pool.Count < MaxPooled)
                Pool.Push(this);
        }
    }

    private static bool IsHardBreak(uint c, bool mask) =>
        c is '\n' or '\r' or ObjectReplacement || (c is '\t' or SoftHyphen && !mask) || IsLineBreak(c);

    // The start of the grapheme after the one at a codepoint, within a limit.
    private static int NextGraphemeIn(bool[] graphemeStart, int cp, int limit)
    {
        do
            cp++;
        while (cp < limit && !graphemeStart[cp]);
        return Math.Min(cp, limit);
    }

    private static bool IsLineBreak(uint c) =>
        c is 0x0B or 0x0C or 0x85 or 0x2028 or 0x2029; // BK and NL other than LF and CR

    private static void Decode(ReadOnlySpan<byte> p, int unit, List<uint> cps, List<int> offsets)
    {
        for (var off = 0; off < p.Length;)
        {
            var c = 0xFFFDu;
            var len = 1;
            if (unit == 1)
            {
                var c0 = p[off];
                var need = c0 < 0x80 ? 1 :
                           (c0 & 0xE0) == 0xC0 ? 2 :
                           (c0 & 0xF0) == 0xE0 ? 3 :
                           (c0 & 0xF8) == 0xF0 ? 4 : 0;
                if (need == 1)
                {
                    c = c0;
                }
                else if (need > 0 && off + need <= p.Length)
                {
                    var u = (uint)(c0 & (0x7F >> need));
                    var k = 1;
                    for (; k < need && (p[off + k] & 0xC0) == 0x80; k++)
                        u = (u << 6) | (uint)(p[off + k] & 0x3F);
                    if (k == need)
                    {
                        len = need;
                        uint min = need switch { 2 => 0x80, 3 => 0x800, _ => 0x10000 };
                        if (u >= min && u <= 0x10FFFF && u is not (>= 0xD800 and < 0xE000))
                            c = u;
                    }
                }
            }
            else if (unit == 2)
            {
                len = 2;
                var w0 = off + 1 < p.Length ? (uint)(p[off] | (p[off + 1] << 8)) : 0xFFFD;
                c = w0;
                if (w0 is >= 0xD800 and < 0xDC00 && off + 3 < p.Length)
                {
                    var w1 = (uint)(p[off + 2] | (p[off + 3] << 8));
                    if (w1 is >= 0xDC00 and < 0xE000)
                    {
                        c = 0x10000 + ((w0 - 0xD800) << 10) + (w1 - 0xDC00);
                        len = 4;
                    }
                }

                if (c is >= 0xD800 and < 0xE000)
                    c = 0xFFFD;
            }
            else
            {
                len = 4;
                c = off + 3 < p.Length
                        ? (uint)(p[off] | (p[off + 1] << 8) | (p[off + 2] << 16) | (p[off + 3] << 24))
                        : 0xFFFD;
                if (c > 0x10FFFF || c is >= 0xD800 and < 0xE000)
                    c = 0xFFFD;
            }

            len = Math.Min(len, p.Length - off);
            cps.Add(c);
            offsets.Add(off);
            off += len;
        }

        offsets.Add(p.Length);
    }

    private void Init(
        ManagedFont font, in TextLayoutDesc layoutDesc, in ImTextStyle style, ReadOnlySpan<byte> text,
        int codeUnitSize, ReadOnlySpan<TextRun> textRuns, ReadOnlySpan<InlineObject> inlineObjects,
        ReadOnlySpan<int> sourceOffsets)
    {
        if (codeUnitSize is not (1 or 2 or 4))
            throw new ArgumentOutOfRangeException(nameof(codeUnitSize), codeUnitSize, "Must be 1, 2 or 4.");
        this.fonts.Add(font);
        this.desc = layoutDesc;
        this.vertical = (layoutDesc.Flags & ImTextLayoutFlags.Vertical) != 0;
        this.defaultStyle = style;
        this.runs = this.runs.Grow(textRuns.Length);
        textRuns.CopyTo(this.runs);
        this.runCount = textRuns.Length;
        this.objects = this.objects.Grow(inlineObjects.Length);
        inlineObjects.CopyTo(this.objects);
        this.objectCount = inlineObjects.Length;
        this.textBytes = text.Length;
        this.scratch = BuildScratch.Rent();
        try
        {
            var start = FontProfiler.Begin(FontProfiler.Section.LayoutAnalyze);
            this.Analyze(text, codeUnitSize);
            FontProfiler.End(FontProfiler.Section.LayoutAnalyze, start, text.Length);
            if (!sourceOffsets.IsEmpty)
                this.MapToSource(sourceOffsets);
            this.Build();
        }
        finally
        {
            BuildScratch.Return(this.scratch);
            this.scratch = null;
        }
    }

    // Back to how a new layout is, keeping what was allocated.
    private void Reset()
    {
        this.fonts.Clear();
        this.lines.Clear();
        this.metricsCache.Clear();
        this.items.Clear();
        this.glyphs.Clear();
        this.decorationSpans.Clear();
        this.visibleRanges.Clear();
        this.cachedSlices.Clear();
        this.cachedAtlas = null;
        Array.Clear(this.runs, 0, this.runCount);
        Array.Clear(this.objects, 0, this.objectCount);
        this.runCount = this.objectCount = this.cpCount = 0;
        this.desc = default;
        this.defaultStyle = default;
        this.textBytes = 0;
        this.segmentCount = 0;
        this.vertical = false;
        this.largestItemSize = -1;
        this.Size = default;
        this.Ink = default;
        this.TextLength = 0;
        this.EllipsisOffset = -1;
        this.RenderHook = null;
        this.Cached = false;
    }

    // Codepoints, graphemes (UAX #29), line break opportunities (UAX #14), and the run of each codepoint.
    private void Analyze(ReadOnlySpan<byte> text, int unit)
    {
        var s = this.Scratch;
        Decode(text, unit, s.Cps, s.Offsets);
        var n = s.Cps.Count;
        this.cpCount = n;
        this.cps = this.cps.Grow(n);
        CollectionsMarshal.AsSpan(s.Cps).CopyTo(this.cps);
        this.cpOffsets = this.cpOffsets.Grow(n + 1);
        CollectionsMarshal.AsSpan(s.Offsets).CopyTo(this.cpOffsets);
        s.Prepare(text.Length, n);

        var flags = unit switch
        {
            1 => UtfEnumeratorFlags.Utf8,
            2 => UtfEnumeratorFlags.Utf16 | UtfEnumeratorFlags.LittleEndian,
            _ => UtfEnumeratorFlags.Utf32 | UtfEnumeratorFlags.LittleEndian,
        };
        this.graphemeStart = this.graphemeStart.Grow(n);
        Array.Clear(this.graphemeStart, 0, n);
        if (n > 0)
            this.graphemeStart[0] = true;
        foreach (var boundary in new UnicodeGraphemeClusterEnumerator(text, flags))
        {
            if (boundary < text.Length && this.CpAtOffset(boundary) is var cp and >= 0)
                this.graphemeStart[cp] = true;
        }

        foreach (var item in new UnicodeLineBreakEnumerator(text, flags))
        {
            if (this.CpAtOffset(item.ByteOffset) is var cp and >= 0)
                s.BreakBefore[cp] = true;
        }

        // Each grapheme takes the run of its first codepoint (byte offsets inside a codepoint or grapheme round down).
        var runOfByte = s.RunOfByte.AsSpan(0, text.Length);
        runOfByte.Fill(-1);
        for (var r = 0; r < this.runCount; r++)
        {
            var b = Math.Clamp(this.runs[r].TextBegin, 0, text.Length);
            var e = Math.Clamp(this.runs[r].TextEnd, b, text.Length);
            runOfByte[b..e].Fill(r);
        }

        for (var cp = 0; cp < n; cp++)
            s.CpRun[cp] = this.IsGraphemeStart(cp) ? runOfByte[this.cpOffsets[cp]] : s.CpRun[cp - 1];

        // Tabs are shaped as spaces, soft hyphens as the hyphens they are where lines wrap after them, and masked
        // graphemes as the mask character.
        var mask = this.Masked;
        var maskChar = this.desc.MaskChar != 0 ? this.desc.MaskChar : '*';
        for (var cp = 0; cp < n; cp++)
        {
            var c = this.cps[cp];
            if (mask && this.IsGraphemeStart(cp) && c is not ('\n' or '\r' or ObjectReplacement) && !IsLineBreak(c))
                s.ShapeText[cp] = maskChar;
            else if (c == '\t')
                s.ShapeText[cp] = ' ';
            else if (c == SoftHyphen)
                s.ShapeText[cp] = '-';
            else
                s.ShapeText[cp] = c;
        }

        if (!mask)
            this.SubstituteMissing(s.ShapeText.AsSpan(0, n), s.CpRun);
    }

    // As the game does, characters no font has are shaped as U+3013 GETA MARK, or a hyphen if the font lacks it too, or
    // left as nothing if it lacks both. Characters that show nothing of their own, such as marks and format characters,
    // are left as they are.
    private void SubstituteMissing(Span<uint> shapeText, int[] cpRun)
    {
        Span<uint> glyphs = stackalloc uint[256];
        Span<uint> marks = stackalloc uint[2];
        for (var begin = 0; begin < shapeText.Length;)
        {
            // Codepoints of one run, and so of one font, up to the buffer.
            var run = cpRun[begin];
            var end = begin + 1;
            while (end < shapeText.Length && end - begin < glyphs.Length && cpRun[end] == run)
                end++;

            var font = this.fonts[this.FontOfRun(run).FontIndex];
            var chunk = shapeText[begin..end];
            font.GetGlyphIndices(chunk, glyphs[..chunk.Length]);
            var replacement = 0u;
            for (var i = 0; i < chunk.Length; i++)
            {
                if (glyphs[i] != 0 || !ShowsItself(chunk[i]))
                    continue;
                if (replacement == 0)
                {
                    font.GetGlyphIndices([0x3013, '-'], marks);
                    replacement = marks[0] != 0 ? 0x3013u : marks[1] != 0 ? '-' : uint.MaxValue;
                }

                if (replacement != uint.MaxValue)
                    chunk[i] = replacement;
            }

            begin = end;
        }

        static bool ShowsItself(uint c) =>
            c <= 0x10FFFF && UnicodeData.GeneralCategory[(int)c] is not (
                UnicodeGeneralCategory.Mn or UnicodeGeneralCategory.Mc or UnicodeGeneralCategory.Me or
                UnicodeGeneralCategory.Zs or UnicodeGeneralCategory.Zl or UnicodeGeneralCategory.Zp or
                UnicodeGeneralCategory.Cc or UnicodeGeneralCategory.Cf or UnicodeGeneralCategory.Cs);
    }

    // From here on, offsets are into the source text. Bytes of it that show nothing belong to what precedes them.
    private void MapToSource(ReadOnlySpan<int> sourceOffsets)
    {
        if (sourceOffsets.Length != this.textBytes + 1)
            throw new ArgumentException("Must have one offset per byte, and one for the end.", nameof(sourceOffsets));
        foreach (ref var offset in this.cpOffsets.AsSpan(0, this.cpCount + 1))
            offset = sourceOffsets[offset];
        foreach (ref var run in this.runs.AsSpan(0, this.runCount))
        {
            run = run with
            {
                TextBegin = sourceOffsets[Math.Clamp(run.TextBegin, 0, this.textBytes)],
                TextEnd = sourceOffsets[Math.Clamp(run.TextEnd, 0, this.textBytes)],
            };
        }

        this.textBytes = sourceOffsets[^1];
    }

    // The codepoint starting at a byte offset, the end (n) at the end of the text, or -1.
    private int CpAtOffset(int offset)
    {
        if (offset >= this.textBytes)
            return this.cpCount;
        var i = this.CpIndex(offset);
        return this.cpOffsets[i] == offset ? i : -1;
    }

    private void Build()
    {
        var cpCount = this.cpCount;
        var mask = this.Masked;

        // Controls and objects are items as they are, and text is shaped in segments.
        var src = this.Scratch.Src;
        var cpRun = this.Scratch.CpRun;
        var objectIndex = 0;
        var ctxBegin = 0;
        var start = FontProfiler.Begin(FontProfiler.Section.LayoutShape);
        for (var cp = 0; cp < cpCount;)
        {
            var c = this.cps[cp];
            var gEnd = this.NextGrapheme(cp, cpCount);
            if (c is '\n' or '\r' || IsLineBreak(c) || (c == ObjectReplacement && objectIndex < this.objectCount))
            {
                // CR LF is one grapheme of two items.
                for (var k = cp; k < (c == ObjectReplacement ? cp + 1 : gEnd); k++)
                {
                    var item = new Item
                    {
                        Offset = this.cpOffsets[k],
                        CpBegin = k,
                        CpEnd = c == ObjectReplacement ? gEnd : k + 1,
                        Run = cpRun[k],
                        Object = -1,
                        Segment = -1,
                        Cp = this.cps[k],
                        GlyphBegin = this.glyphs.Count,
                        GlyphEnd = this.glyphs.Count,
                    };
                    item.Length = this.cpOffsets[item.CpEnd] - item.Offset;
                    (item.FontIndex, item.Size) = this.FontOfRun(item.Run);
                    if (item.Cp == ObjectReplacement)
                    {
                        item.Kind = ItemKind.Object;
                        item.Object = objectIndex++;
                        var b = this.objects[item.Object].Bounds;
                        item.Advance = this.vertical ? b.W - b.Y : b.Z;
                    }
                    else
                    {
                        // CR before LF is part of the line break, and a lone CR ends the line itself.
                        item.Kind = item.Cp == '\r' && k + 1 < cpCount && this.cps[k + 1] == '\n'
                                        ? ItemKind.Control
                                        : ItemKind.Newline;
                    }

                    src.Add(item);
                }

                cp = gEnd;
                ctxBegin = cp;
                continue;
            }

            if (IsHardBreak(c, mask))
            {
                // A tab, a soft hyphen, or U+FFFC without an object, is shaped alone.
                this.ShapeSegment(cp, gEnd, cp, gEnd, src);
                cp = gEnd;
                ctxBegin = cp;
                continue;
            }

            var end = gEnd;
            while (end < cpCount && !IsHardBreak(this.cps[end], mask) && cpRun[end] == cpRun[cp])
                end = this.NextGrapheme(end, cpCount);
            var ctxEnd = end;
            while (ctxEnd < cpCount && !IsHardBreak(this.cps[ctxEnd], mask))
                ctxEnd = this.NextGrapheme(ctxEnd, cpCount);
            this.ShapeSegment(cp, end, ctxBegin, ctxEnd, src);
            cp = end;
        }

        var srcSpan = CollectionsMarshal.AsSpan(src);
        for (var k = 0; k + 1 < srcSpan.Length; k++)
            this.KernBoundary(ref srcSpan[k], srcSpan[k + 1]);
        FontProfiler.End(FontProfiler.Section.LayoutShape, start, cpCount);

        start = FontProfiler.Begin(FontProfiler.Section.LayoutFinish);
        var ellipsis = this.MakeEllipsis(out var ellipsisCount);
        this.BreakLines(src, ellipsis, ellipsisCount);
        this.Finish();
        FontProfiler.End(FontProfiler.Section.LayoutFinish, start, cpCount);
    }

    // U+2026 from the default font, else three periods.
    private Item MakeEllipsis(out int count)
    {
        var font = this.fonts[0];
        Span<uint> chars = [0x2026, '.'];
        Span<uint> ids = stackalloc uint[2];
        font.GetGlyphIndices(chars, ids);
        var glyph = new Glyph { Id = ids[0] != 0 ? ids[0] : ids[1] };
        count = ids[0] != 0 ? 1 : 3;
        Span<GlyphMetrics> m = stackalloc GlyphMetrics[1];
        font.GetGlyphMetrics([glyph.Id], this.desc.Size, this.Density, m);
        glyph.Bounds = m[0].Bounds;
        var item = new Item
        {
            Kind = ItemKind.Ellipsis,
            Run = -1,
            Object = -1,
            Segment = -1,
            Size = this.desc.Size,
            Advance = this.vertical ? this.desc.Size : m[0].Advance,
            Bounds = m[0].Bounds,
            GlyphBegin = this.glyphs.Count,
            GlyphEnd = this.glyphs.Count + 1,
        };
        this.glyphs.Add(glyph);
        return item;
    }

    // Ink, line records and run rectangles.
    private void Finish()
    {
        // Line by line, and in locals, as this runs per item in unoptimized builds too.
        var layoutSize = this.Size;
        var (inkLeft, inkTop, inkRight, inkBottom) = (0f, 0f, layoutSize.X, layoutSize.Y);
        var items = CollectionsMarshal.AsSpan(this.items);
        foreach (ref readonly var line in CollectionsMarshal.AsSpan(this.lines))
        {
            var y = line.BlockPos + line.Baseline;
            foreach (ref readonly var item in items[line.ItemBegin..line.ItemEnd])
            {
                if (item.Kind is not (ItemKind.Glyph or ItemKind.Ellipsis) || item.Bounds.Z <= item.Bounds.X)
                    continue;
                Vector4 r;
                if (this.vertical)
                {
                    var ascent = MathF.Round(this.MetricsOf(item.FontIndex, item.Size).Ascent);
                    r = this.RectToScreen(
                        item.X + ascent + item.Bounds.Y,
                        line.BlockPos,
                        item.X + ascent + item.Bounds.W,
                        line.BlockPos + line.BlockSize);
                }
                else
                {
                    r = new(item.X + item.Bounds.X, y + item.Bounds.Y, item.X + item.Bounds.Z, y + item.Bounds.W);
                }

                inkLeft = Math.Min(inkLeft, r.X);
                inkTop = Math.Min(inkTop, r.Y);
                inkRight = Math.Max(inkRight, r.Z);
                inkBottom = Math.Max(inkBottom, r.W);
            }
        }

        var ink = new Vector4(inkLeft, inkTop, inkRight, inkBottom);

        var edge = this.defaultStyle.Edge;
        var shadow = this.defaultStyle.Shadow;
        var grow = (edge.IsOn ? edge.WidthAt(this.desc.Size) : 0) +
                   (shadow.IsOn
                        ? this.desc.Size * Math.Max(Math.Abs(shadow.Offset.X), Math.Abs(shadow.Offset.Y))
                        : 0);
        this.Ink = new(ink.X - grow, ink.Y - grow, ink.Z + grow, ink.W + grow);

        // Pinned, as ImGui reads them in place. Only an empty one is not.
        this.lineArray = this.lineArray.Grow(this.lines.Count, true);
        for (var n = 0; n < this.lines.Count; n++)
        {
            var line = this.lines[n];
            var r = this.RectToScreen(0, line.BlockPos, line.Width, line.BlockPos + line.BlockSize);
            var pos = new Vector2(r.X, r.Y);
            var size = new Vector2(r.Z - r.X, r.W - r.Y);
            this.lineArray[n] = new(line.TextBegin, line.TextEnd, line.TextNext, line.Baseline, pos, size);
        }

        var runRectList = this.Scratch.RunRects;
        this.runRectRange = this.runRectRange.Grow(this.runCount);
        for (var r = 0; r < this.runCount; r++)
        {
            var first = runRectList.Count;
            foreach (var line in this.lines)
            {
                float u0 = float.MaxValue, u1 = float.MinValue;
                for (var k = line.ItemBegin; k < line.ItemEnd; k++)
                {
                    ref readonly var item = ref this.ItemAt(k);
                    if (item.Run != r || item.Kind == ItemKind.Newline)
                        continue;
                    u0 = Math.Min(u0, item.X);
                    u1 = Math.Max(u1, item.X + item.Advance);
                }

                if (u0 < u1)
                    runRectList.Add(this.RectToScreen(u0, line.BlockPos, u1, line.BlockPos + line.BlockSize));
            }

            this.runRectRange[r] = (first, runRectList.Count - first);
        }

        this.runRects = this.runRects.Grow(runRectList.Count, true);
        runRectList.CopyTo(this.runRects);
    }

    // Glyphs of whole graphemes of one run, shaped by the font. For password masks and tabs, one glyph per grapheme
    // from its first codepoint.
    private void ShapeGlyphs(
        int fontIndex, float size, int cpBegin, int cpEnd, int ctxBegin, int ctxEnd, bool perGrapheme)
    {
        var output = this.Scratch.ShapeOutput;
        output.Clear();
        var font = this.fonts[fontIndex];
        if (!perGrapheme)
        {
            var start = FontProfiler.Begin(FontProfiler.Section.LayoutShapeByFonts);
            font.Shape(
                new(
                    this.Scratch.ShapeText.AsSpan(ctxBegin, ctxEnd - ctxBegin),
                    cpBegin - ctxBegin,
                    cpEnd - ctxBegin,
                    size,
                    this.Density),
                output);
            FontProfiler.End(FontProfiler.Section.LayoutShapeByFonts, start, cpEnd - cpBegin);

            // Clusters as layout codepoints, within the run and ascending.
            var last = cpBegin;
            foreach (ref var g in output.Span)
            {
                g.Cluster = Math.Clamp(g.Cluster + ctxBegin, last, cpEnd - 1);
                last = g.Cluster;
            }

            return;
        }

        var count = 0;
        for (var cp = cpBegin; cp < cpEnd; cp = this.NextGrapheme(cp, cpEnd))
            count++;
        var chars = count <= 64 ? stackalloc uint[count] : new uint[count];
        var ids = count <= 64 ? stackalloc uint[count] : new uint[count];
        var k = 0;
        for (var cp = cpBegin; cp < cpEnd; cp = this.NextGrapheme(cp, cpEnd))
            chars[k++] = this.Scratch.ShapeText[cp];
        font.GetGlyphIndices(chars, ids);
        var metrics = count <= 64 ? stackalloc GlyphMetrics[count] : new GlyphMetrics[count];
        font.GetGlyphMetrics(ids, size, this.Density, metrics);
        k = 0;
        for (var cp = cpBegin; cp < cpEnd; cp = this.NextGrapheme(cp, cpEnd), k++)
            output.Add(ids[k], cp, metrics[k].Advance);
    }

    // Shapes whole graphemes of one run between hard breaks into items, with [ctxBegin, ctxEnd) as context.
    private void ShapeSegment(int cpBegin, int cpEnd, int ctxBegin, int ctxEnd, List<Item> output)
    {
        var segment = this.segmentCount++;
        var shapeText = this.Scratch.ShapeText;
        var run = this.Scratch.CpRun[cpBegin];
        var (fontIndex, size) = this.FontOfRun(run);
        var tab = !this.Masked && this.cps[cpBegin] == '\t';

        this.ShapeGlyphs(fontIndex, size, cpBegin, cpEnd, ctxBegin, ctxEnd, this.Masked || tab);
        var shaped = this.Scratch.ShapeOutput.Span;
        var ids = shaped.Length <= 128 ? stackalloc uint[shaped.Length] : new uint[shaped.Length];
        var metrics = shaped.Length <= 128 ? stackalloc GlyphMetrics[shaped.Length] : new GlyphMetrics[shaped.Length];
        for (var k = 0; k < shaped.Length; k++)
            ids[k] = shaped[k].Glyph;
        if (!ids.IsEmpty)
            this.fonts[fontIndex].GetGlyphMetrics(ids, size, this.Density, metrics);

        // Arrays are read into locals, and items filled in place, as this runs per grapheme in unoptimized builds too.
        var glyphBase = this.glyphs.Count;
        CollectionsMarshal.SetCount(this.glyphs, glyphBase + shaped.Length);
        var glyphs = CollectionsMarshal.AsSpan(this.glyphs)[glyphBase..];
        var cps = this.cps;
        var cpOffsets = this.cpOffsets;
        var graphemeStart = this.graphemeStart;
        var softHyphens = !this.Masked;
        var vertical = this.vertical;
        var g = 0;
        for (var cp = cpBegin; cp < cpEnd;)
        {
            // The item ends where both a cluster and a grapheme do.
            var end = NextGraphemeIn(graphemeStart, cp, cpEnd);
            var gEnd = g;
            while (true)
            {
                while (gEnd < shaped.Length && shaped[gEnd].Cluster < end)
                    gEnd++;
                var next = gEnd < shaped.Length ? shaped[gEnd].Cluster : cpEnd;
                if (next <= end || gEnd == g)
                    break;
                end = NextGraphemeIn(graphemeStart, next - 1, cpEnd);
            }

            output.Add(default);
            ref var item = ref CollectionsMarshal.AsSpan(output)[^1];
            item.Offset = cpOffsets[cp];
            item.Length = cpOffsets[end] - item.Offset;
            item.CpBegin = cp;
            item.CpEnd = end;
            item.Run = run;
            item.Object = -1;
            item.FontIndex = fontIndex;
            item.Segment = segment;
            item.CtxBegin = ctxBegin;
            item.CtxEnd = ctxEnd;
            item.Size = size;
            item.Cp = cps[cp];
            item.Kind = IsBlank(shapeText[cp]) ? ItemKind.Blank : ItemKind.Glyph;
            item.UnsafeBefore = gEnd > g && shaped[g].UnsafeToBreak;
            item.GlyphBegin = glyphBase + g;
            item.GlyphEnd = glyphBase + gEnd;

            var pen = 0f;
            var (inkLeft, inkTop, inkRight, inkBottom) = (float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            for (var k = g; k < gEnd; k++)
            {
                ref readonly var s = ref shaped[k];
                ref var glyph = ref glyphs[k];
                var b = metrics[k].Bounds;
                var x = pen + s.Offset.X;
                glyph.Id = s.Glyph;
                glyph.Pos = new(x, s.Offset.Y);
                glyph.Bounds = b;
                pen += s.Advance;
                if (s.Glyph != 0 && b.Z > b.X && b.W > b.Y)
                {
                    inkLeft = Math.Min(inkLeft, b.X + x);
                    inkTop = Math.Min(inkTop, b.Y + s.Offset.Y);
                    inkRight = Math.Max(inkRight, b.Z + x);
                    inkBottom = Math.Max(inkBottom, b.W + s.Offset.Y);
                }
            }

            if (item.Kind == ItemKind.Glyph && inkRight > inkLeft)
                item.Bounds = new(inkLeft, inkTop, inkRight, inkBottom);

            // A tab is shaped as a space. BreakLines widens it to the next tab stop.
            item.Advance = vertical ? size * this.GraphemeCount(item) : pen;

            // A soft hyphen takes no space, unless BreakLines shows it.
            if (softHyphens && item.Cp == SoftHyphen)
            {
                item.Kind = ItemKind.SoftHyphen;
                item.HyphenAdvance = item.Advance;
                item.Advance = 0;
            }

            g = gEnd;
            cp = end;
        }

        // Glyphs past the last cluster, which shaping should not give, are dropped.
        CollectionsMarshal.SetCount(this.glyphs, glyphBase + g);
    }

    // The kerning of the pair (a, b) alone with a font, or null if the font lacks either character.
    private float? PairKern(int fontIndex, float size, uint a, uint b)
    {
        var key = (fontIndex, size, a, b);
        var pairKerns = this.Scratch.PairKerns;
        if (!pairKerns.TryGetValue(key, out var value))
        {
            value = this.fonts[fontIndex].TryGetPairKerning(a, b, size, this.Density, out var k) ? k : null;
            pairKerns.Add(key, value);
        }

        return value;
    }

    // Kerns adjacent items of different segments, as ManagedFont.KernAcross does.
    private void KernBoundary(ref Item left, in Item right)
    {
        left.Advance -= left.Kern;
        left.Kern = 0;
        if (this.vertical || !IsText(left) || !IsText(right) || left.Segment == right.Segment ||
            left.CpEnd != right.CpBegin ||
            left.GlyphBegin == left.GlyphEnd || right.GlyphBegin == right.GlyphEnd)
        {
            return;
        }

        var aCp = left.CpEnd - 1;
        while (aCp > left.CpBegin && !this.IsGraphemeStart(aCp))
            aCp--;
        if (!this.Masked && (this.cps[aCp] == '\t' || this.cps[right.CpBegin] == '\t'))
            return;
        var a = this.Scratch.ShapeText[aCp];
        var b = this.Scratch.ShapeText[right.CpBegin];
        var l = this.PairKern(left.FontIndex, left.Size, a, b);
        var r = right.FontIndex != left.FontIndex || right.Size != left.Size
                    ? this.PairKern(right.FontIndex, right.Size, a, b)
                    : null;
        left.Kern = ManagedFont.AverageKerning(l, r);
        left.Advance += left.Kern;
    }

    // What only building a layout needs, reused by the next layout built on the thread.
    private sealed class BuildScratch
    {
        // Larger ones are left to the GC, rather than kept for the thread's lifetime.
        private const int MaxKeptLength = 1 << 16;

        // Null while in use. A layout made while making another then gets scratch of its own.
        [ThreadStatic]
        private static BuildScratch? free;

        public List<uint> Cps { get; } = [];

        public List<int> Offsets { get; } = [];

        public List<Item> Src { get; } = [];

        public List<Item> Fresh { get; } = [];

        public List<Vector4> RunRects { get; } = [];

        public Dictionary<(int Font, float Size, uint A, uint B), float?> PairKerns { get; } = [];

        public ShapedGlyphList ShapeOutput { get; } = new();

        // At least as long as Prepare was given. Only that much is meaningful.
        public bool[] BreakBefore { get; private set; } = [];

        public int[] CpRun { get; private set; } = [];

        public uint[] ShapeText { get; private set; } = [];

        public int[] RunOfByte { get; private set; } = [];

        public static BuildScratch Rent()
        {
            var scratch = free ?? new();
            free = null;
            return scratch;
        }

        public static void Return(BuildScratch scratch)
        {
            if (scratch.RunOfByte.Length > MaxKeptLength || scratch.Src.Capacity > MaxKeptLength)
                return;
            scratch.Cps.Clear();
            scratch.Offsets.Clear();
            scratch.Src.Clear();
            scratch.Fresh.Clear();
            scratch.RunRects.Clear();
            scratch.PairKerns.Clear();
            scratch.ShapeOutput.Clear();
            free = scratch;
        }

        // Makes the arrays long enough for the text, with BreakBefore cleared.
        public void Prepare(int textLength, int cpCount)
        {
            this.BreakBefore = Take(this.BreakBefore, cpCount + 1);
            this.CpRun = Take(this.CpRun, cpCount);
            this.ShapeText = Take(this.ShapeText, cpCount);
            this.RunOfByte = Take(this.RunOfByte, textLength);
        }

        // An array at least as long, with as much of it cleared.
        private static T[] Take<T>(T[] array, int length)
        {
            var result = array.Grow(length);
            if (result == array)
                Array.Clear(array, 0, length);
            return result;
        }
    }
}
