using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Sources;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>Shaped, laid out text, created by <see cref="TextLayouter"/>.</summary>
/// <remarks><ol>
/// <li>Immutable, so every query returns the same result for the layout's lifetime. Main thread only.</li>
/// <li>Text is split into grapheme clusters (UAX #29), each taking the run of its first codepoint.</li>
/// <li>Segments are stretches of one run between hard breaks, such as line breaks, tabs and inline objects. The font
/// shapes each, with the text up to the hard breaks around it as context.</li>
/// <li>Items are shaping clusters, rounded out to whole graphemes. A ligature of several graphemes is one item, and
/// carets inside it divide it evenly. Pairs across segments are kerned as by <see cref="ManagedFont.KernAcross"/>.
/// </li>
/// <li>Lines break where UAX #14 allows. Where shaping says a break is unsafe, both sides are shaped again, each
/// without the other as context.</li>
/// <li>Lines are built along an inline axis (u) and stacked along a block axis (v). These are x and y for horizontal
/// text, and y and right to left for <see cref="ImTextLayoutFlags.Vertical"/>, with every grapheme upright and one em
/// long. Queries take and return screen axes relative to the top-left of the layout box.</li>
/// </ol></remarks>
/// <seealso cref="ManagedFont.Shape"/>
internal sealed partial class TextLayout
{
    private readonly List<ManagedFont> fonts = [];
    private readonly List<Line> lines = [];
    private readonly Dictionary<(int Font, float Size), FontMetrics> metricsCache = [];

    private readonly List<Item> items = [];
    private readonly List<Glyph> glyphs = [];

    private TextLayoutDesc desc;
    private ImTextStyle defaultStyle;

    // The arrays are reused by layouts made from the pool, so they may be longer than their counts.
    private TextRun[] runs = [];
    private int runCount;
    private InlineObject[] objects = [];
    private int objectCount;
    private uint[] cps = [];
    private int[] cpOffsets = [0];
    private int cpCount;
    private bool[] graphemeStart = [];
    private Vector4[] runRects = [];
    private (int First, int Count)[] runRectRange = [];
    private ImTextLine[] lineArray = [];

    private int textBytes;
    private int segmentCount;
    private bool vertical;

    private enum ItemKind : byte
    {
        Glyph,
        Blank,
        Newline,
        Control,
        Object,
        Ellipsis,

        // A soft hyphen. It is nothing, unless a line wraps after it (TextLayout.BreakLines), where it becomes a Glyph.
        SoftHyphen,
    }

    /// <summary>Gets the layout box size from advances and line heights, in screen axes.</summary>
    public Vector2 Size { get; private set; }

    /// <summary>Gets the ink box (x0, y0, x1, y1) of what is drawn, including overhangs.</summary>
    /// <remarks>May extend past <see cref="Size"/>.</remarks>
    public Vector4 Ink { get; private set; }

    /// <summary>Gets the number of bytes laid out.</summary>
    /// <remarks>Fewer than given when <see cref="ImTextLayoutFlags.StopOnNewLine"/> or
    /// <see cref="TextLayoutDesc.MaxWidth"/> stopped early.</remarks>
    public int TextLength { get; private set; }

    /// <summary>Gets the first hidden byte with <see cref="ImTextLayoutFlags.Ellipsis"/>, or -1.</summary>
    public int EllipsisOffset { get; private set; } = -1;

    /// <summary>Gets the lines.</summary>
    /// <remarks>Pinned, and valid while the layout is alive, so ImGui reads them in place.</remarks>
    public ReadOnlySpan<ImTextLine> Lines => this.lineArray.AsSpan(0, this.lines.Count);

    /// <summary>Gets the number of runs.</summary>
    public int RunCount => this.runCount;

    /// <summary>Gets or sets what is told when the layout is drawn, which may draw some runs with another style, and
    /// draws the inline objects; or null to leave the objects to the caller.</summary>
    public ITextRenderHook? RenderHook { get; set; }

    /// <summary>Gets or sets the layout as ImGui knows it, or 0 if it has none now.</summary>
    /// <seealso cref="TextLayouter.ToNative"/>
    internal nint NativeHandle { get; set; }

    /// <summary>Gets or sets a value indicating whether <see cref="TextLayouter"/> has it cached.</summary>
    /// <remarks>Once it has not, and ImGui does not have it either, it goes back to the pool.</remarks>
    /// <seealso cref="Recycle"/>
    internal bool Cached { get; set; }

    private float Density => this.desc.Density > 0 ? this.desc.Density : 1;

    private bool Masked => (this.desc.Flags & ImTextLayoutFlags.PasswordMask) != 0;

    /// <summary>Gets the hover boxes (x0, y0, x1, y1) of a run, one per line it spans.</summary>
    /// <remarks>Pinned, and valid while the layout is alive.</remarks>
    /// <param name="runIndex">The run.</param>
    /// <returns>The rectangles.</returns>
    public ReadOnlySpan<Vector4> GetRunRects(int runIndex) =>
        runIndex < 0 || runIndex >= this.runCount
            ? default
            : this.runRects.AsSpan(this.runRectRange[runIndex].First, this.runRectRange[runIndex].Count);

    /// <summary>Gets the rectangles (x0, y0, x1, y1) of a byte range, one per line it spans.</summary>
    /// <param name="textBegin">Start byte offset.</param>
    /// <param name="textEnd">End byte offset.</param>
    /// <param name="rects">Receives as many rectangles as fit.</param>
    /// <returns>The number of rectangles the range has.</returns>
    /// <remarks>Where the range continues onto the next line, the rectangle reaches the wrap width (or the layout box
    /// without wrapping), and the trailing blanks with <see cref="ImTextLayoutFlags.WrapKeepBlanks"/>.</remarks>
    public int GetRangeRects(int textBegin, int textEnd, Span<Vector4> rects)
    {
        var edge = this.desc.WrapWidth > 0 ? this.desc.WrapWidth : this.vertical ? this.Size.Y : this.Size.X;
        var keepBlanks = (this.desc.Flags & ImTextLayoutFlags.WrapKeepBlanks) != 0;
        var count = 0;
        for (var n = 0; n < this.lines.Count; n++)
        {
            var line = this.lines[n];
            var lineEnd = Math.Max(line.TextNext, this.LineEndOffset(line));
            if (textEnd <= line.TextBegin || textBegin >= lineEnd)
                continue;
            var u0 = this.InlinePosAt(line, Math.Max(textBegin, line.TextBegin));
            var continues = line.EndsWithNewline ? textEnd >= lineEnd : textEnd > lineEnd && n + 1 < this.lines.Count;
            var u1 = continues ? Math.Max(u0, keepBlanks ? Math.Max(edge, line.FullWidth) : edge)
                     : textEnd >= lineEnd ? line.FullWidth
                     : this.InlinePosAt(line, textEnd);
            if (count < rects.Length)
                rects[count] = this.RectToScreen(u0, line.BlockPos, u1, line.BlockPos + line.BlockSize);
            count++;
        }

        return count;
    }

    /// <summary>Gets the pen of an inline object, relative to the layout origin.</summary>
    /// <remarks>The object's bounds are relative to it. It is on the baseline for horizontal text, and puts the box in
    /// the middle of the column for vertical text.</remarks>
    /// <param name="objectIndex">The object.</param>
    /// <returns>The position, or null if the layout stopped early or the ellipsis hid the object.</returns>
    public Vector2? GetObjectPosition(int objectIndex)
    {
        foreach (ref readonly var item in CollectionsMarshal.AsSpan(this.items))
        {
            if (item.Kind == ItemKind.Object && item.Object == objectIndex)
            {
                var line = this.lines[item.Line];
                if (!this.vertical)
                    return new(item.X, line.BlockPos + line.Baseline);
                var b = this.objects[objectIndex].Bounds;
                var left = this.Size.X - line.BlockPos - line.BlockSize;
                return new(left + ((line.BlockSize - (b.Z - b.X)) * 0.5f) - b.X, item.X - b.Y);
            }
        }

        return null;
    }

    /// <summary>Gets the style a run is laid out with.</summary>
    /// <param name="runIndex">The run.</param>
    /// <returns>The style.</returns>
    public ImTextStyle GetRunStyle(int runIndex) =>
        runIndex >= 0 && this.runs[runIndex].Style is { } style ? style : this.defaultStyle;

    /// <summary>Gets the user data of a run.</summary>
    /// <param name="runIndex">The run.</param>
    /// <returns>The user data.</returns>
    public ulong GetRunUserData(int runIndex) => this.runs[runIndex].UserData;

    private static bool IsBlank(uint c) => c is ' ' or '\t' or 0x3000;

    private static bool IsText(in Item item) => item.Kind is ItemKind.Glyph or ItemKind.Blank;

    private ref Item ItemAt(int index) => ref CollectionsMarshal.AsSpan(this.items)[index];

    // Inline/block (u, v) <-> screen, relative to the layout origin.
    private (float U, float V) FromScreen(Vector2 p) => this.vertical ? (p.Y, this.Size.X - p.X) : (p.X, p.Y);

    private Vector4 RectToScreen(float u0, float v0, float u1, float v1) =>
        this.vertical ? new(this.Size.X - v1, u0, this.Size.X - v0, u1) : new(u0, v0, u1, v1);

    // The font in the bold or italic form the style asks for.
    private (int FontIndex, float Size) FontOfRun(int run)
    {
        var font = run >= 0 && this.runs[run].Font is { } f ? f : this.fonts[0];
        var style = this.GetRunStyle(run);
        return (this.FontIndexOf(font.WithStyle(style.Bold, style.Italic)),
                   run >= 0 && this.runs[run].Size > 0 ? this.runs[run].Size : this.desc.Size);
    }

    private ulong UserDataOf(int run, int obj) =>
        obj >= 0 ? this.objects[obj].UserData : run >= 0 ? this.runs[run].UserData : 0;

    private FontMetrics MetricsOf(int fontIndex, float size)
    {
        if (!this.metricsCache.TryGetValue((fontIndex, size), out var m))
            this.metricsCache.Add((fontIndex, size), m = this.fonts[fontIndex].GetMetrics(size, this.Density));
        return m;
    }

    private int FontIndexOf(ManagedFont font)
    {
        var i = this.fonts.IndexOf(font);
        if (i >= 0)
            return i;
        this.fonts.Add(font);
        return this.fonts.Count - 1;
    }

    private bool IsGraphemeStart(int cp) => cp <= 0 || cp >= this.cpCount || this.graphemeStart[cp];

    private int NextGrapheme(int cp, int limit) =>
        NextGraphemeIn(this.graphemeStart, cp, Math.Min(limit, this.cpCount));

    private int GraphemeCount(in Item item)
    {
        var n = 0;
        for (var cp = item.CpBegin; cp < item.CpEnd; cp++)
            n += this.IsGraphemeStart(cp) ? 1 : 0;
        return Math.Max(n, 1);
    }

    // Index of the codepoint starting at or containing byte 'offset'.
    private int CpIndex(int offset)
    {
        var i = this.cpOffsets.AsSpan(0, this.cpCount + 1).BinarySearch(offset);
        return i >= 0 ? i : Math.Max(~i - 1, 0);
    }

    private struct Glyph
    {
        public uint Id; // 0 if nothing to draw
        public Vector2 Pos; // pen relative to the item's pen
        public Vector4 Bounds; // ink box relative to the glyph's pen on the baseline
    }

    // A shaping cluster rounded out to whole graphemes, a control, an inline object or the ellipsis.
    private struct Item
    {
        public int Offset; // bytes in the text
        public int Length;
        public int CpBegin; // codepoints
        public int CpEnd;
        public int GlyphBegin; // into glyphs
        public int GlyphEnd;
        public int Run; // or -1
        public int Object; // or -1
        public int Line;
        public int FontIndex; // into fonts
        public int Segment; // the shaping run the item came from, or -1
        public int CtxBegin; // codepoints the segment was shaped with as context
        public int CtxEnd;
        public float X; // inline position within the line
        public float Advance; // including Kern
        public float Kern; // adjustment towards the next item, which is from another segment
        public float HyphenAdvance; // of a SoftHyphen, once shown
        public float Size;
        public Vector4 Bounds; // ink box relative to the item's pen on the baseline
        public uint Cp; // the text's first codepoint
        public ItemKind Kind;
        public bool UnsafeBefore; // ending a line before this item changes the shaping
    }

    private struct Line
    {
        public int ItemBegin; // items of the line, including trailing blanks and newline
        public int ItemEnd;
        public int TextBegin;
        public int TextEnd;
        public int TextNext;
        public float Width; // up to TextEnd
        public float FullWidth; // including trailing blanks
        public float BlockPos;
        public float BlockSize;
        public float Baseline; // within the line box
        public bool EndsWithNewline;
    }
}
