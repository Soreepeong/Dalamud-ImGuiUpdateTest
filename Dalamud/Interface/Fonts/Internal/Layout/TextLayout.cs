using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Markup;
using Dalamud.Interface.Fonts.Internal.Sources;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>Shaped, laid out text, created by <see cref="TextLayouter"/>.</summary>
/// <remarks><ul>
/// <li>A layout does not change once made. Main thread only.</li>
/// <li>Text is split into grapheme clusters (UAX #29), each taking the run of its first codepoint.</li>
/// <li>Segments are stretches of one run between hard breaks, such as line breaks, tabs and inline objects. The font
/// shapes each alone, with the grapheme starts found here.</li>
/// <li>Items are shaping clusters, rounded out to whole graphemes. A ligature of several graphemes is one item, and
/// carets inside it divide it evenly. Pairs across segments are kerned as by <see cref="Font.KernAcross"/>.
/// </li>
/// <li>Lines break where UAX #14 allows. Where shaping says a break is unsafe, both sides are shaped again, each
/// without the other.</li>
/// <li>Lines are built along x and stacked along y. Queries take and return points relative to the top-left of the
/// layout box.</li>
/// </ul></remarks>
/// <seealso cref="Font.Shape"/>
internal sealed partial class TextLayout
{
    private readonly List<Font> fonts = [];
    private readonly List<Line> lines = [];
    private readonly Dictionary<int, FontMetrics> metricsCache = [];

    private readonly List<Item> items = [];
    private readonly List<Glyph> glyphs = [];

    private TextLayoutDesc desc;
    private ImTextStyle defaultStyle;

    // The arrays are reused by layouts made from the pool, so they may be longer than their counts.
    private TextRun[] runs = [];
    private InlineObject[] objects = [];
    private int objectCount;
    private uint[] runes = [];
    private int[] runeOffsets = [0];
    private int runeCount;
    private bool[] graphemeStart = [];
    private Vector4[] runRects = [];
    private (int First, int Count)[] runRectRange = [];
    private ImTextLine[] lineArray = [];

    private int textBytes;
    private int segmentCount;

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

    /// <summary>Gets the layout box size from advances, the ink past them to the right, and line heights, in screen
    /// axes.</summary>
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
    public int RunCount { get; private set; }

    /// <summary>Gets or sets the markup hook, which may draw some runs with another style, and draws the inline
    /// objects.</summary>
    /// <remarks>It is told only when the layout is drawn without a transform. With null, the objects are left to the
    /// caller.</remarks>
    public StyledTextHook? RenderHook { get; set; }

    /// <summary>Gets or sets the layout as ImGui knows it, or 0 if it has none now.</summary>
    /// <seealso cref="NativeTextLayouts.ToNative"/>
    internal unsafe nint NativeHandle { get; set; }

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
        runIndex < 0 || runIndex >= this.RunCount
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
        var edge = this.desc.WrapWidth > 0 ? this.desc.WrapWidth : this.Size.X;
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
                rects[count] = new(u0, line.BlockPos, u1, line.BlockPos + line.BlockSize);
            count++;
        }

        return count;
    }

    /// <summary>Gets the user data of a run.</summary>
    /// <param name="runIndex">The run.</param>
    /// <returns>The user data, or 0 for no run.</returns>
    public ulong GetRunUserData(int runIndex) =>
        runIndex >= 0 && runIndex < this.RunCount ? this.runs[runIndex].UserData : 0;

    private static bool IsBlank(uint c) => c is ' ' or '\t' or 0x3000;

    private static bool IsText(in Item item) => item.Kind is ItemKind.Glyph or ItemKind.Blank;

    // The style a run is laid out with, or the default style for no run.
    private ImTextStyle GetRunStyle(int runIndex) =>
        runIndex >= 0 && runIndex < this.RunCount && this.runs[runIndex].Style is { } style
            ? style
            : this.defaultStyle;

    private ref Item ItemAt(int index) => ref CollectionsMarshal.AsSpan(this.items)[index];

    // The byte offset of an item in the text.
    private int OffsetOf(in Item item) => this.runeOffsets[item.CpBegin];

    // The byte offset of the end of an item.
    private int EndOf(in Item item) => this.runeOffsets[item.CpEnd];

    // Whether an item starts within a byte range.
    private bool InRange(in Item item, int begin, int end)
    {
        var offset = this.runeOffsets[item.CpBegin];
        return offset >= begin && offset < end;
    }

    // The font in the bold or italic form the style of a run asks for.
    private int FontIndexOfRun(int run)
    {
        var style = this.GetRunStyle(run);
        return this.FontIndexOf(this.fonts[0].WithStyle(style.Bold, style.Italic));
    }

    private ulong UserDataOf(int run, int obj) =>
        obj >= 0 ? this.objects[obj].UserData : run >= 0 ? this.runs[run].UserData : 0;

    // The pen of an object item on the baseline, relative to the layout origin. The object's bounds are relative to it.
    private Vector2 ObjectPen(in Item item)
    {
        ref readonly var line = ref CollectionsMarshal.AsSpan(this.lines)[item.LineIndex];
        return new(item.X, line.BlockPos + line.Baseline);
    }

    private FontMetrics MetricsOf(int fontIndex)
    {
        if (!this.metricsCache.TryGetValue(fontIndex, out var m))
            this.metricsCache.Add(fontIndex, m = this.fonts[fontIndex].GetMetrics(this.desc.Size, this.Density));
        return m;
    }

    private int FontIndexOf(Font font)
    {
        var i = this.fonts.IndexOf(font);
        if (i >= 0)
            return i;
        this.fonts.Add(font);
        return this.fonts.Count - 1;
    }

    private bool IsGraphemeStart(int cp) => cp <= 0 || cp >= this.runeCount || this.graphemeStart[cp];

    private int NextGrapheme(int cp, int limit) =>
        NextGraphemeIn(this.graphemeStart, cp, Math.Min(limit, this.runeCount));

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
        var i = this.runeOffsets.AsSpan(0, this.runeCount + 1).BinarySearch(offset);
        return i >= 0 ? i : Math.Max(~i - 1, 0);
    }

    private struct Glyph
    {
        public uint Id; // 0 if nothing to draw
        public Vector2 Pos; // pen relative to the item's pen
    }

    // A shaping cluster rounded out to whole graphemes, a control, an inline object or the ellipsis. One per grapheme
    // of most text, so it is kept small. Byte offsets and codepoints are looked up from CpBegin and CpEnd.
    private struct Item
    {
        public int CpBegin; // codepoints. An ellipsis has none, at where the text is cut.
        public int CpEnd;
        public int GlyphBegin; // into glyphs
        public int GlyphEnd;
        public int Run; // or -1
        public int Object; // or -1
        public int LineIndex;
        public int Segment; // the shaping run the item came from, or -1
        public float X; // inline position within the line
        public float Advance; // including Kern
        public float Kern; // adjustment towards the next item, which is from another segment
        public float HyphenAdvance; // of a SoftHyphen, once shown
        public Vector4 Bounds; // ink box relative to the item's pen on the baseline
        public ItemKind Kind;
        public byte FontIndex; // into fonts, which has at most the four styles of the default font
        public bool UnsafeBefore; // ending a line before this item changes the shaping
    }

    private struct Line
    {
        public int ItemBegin; // items of the line, including trailing blanks and newline
        public int ItemEnd;
        public int TextBegin;
        public int TextEnd;
        public int TextNext;
        public float Width; // up to TextEnd, as a pen position
        public float FullWidth; // including trailing blanks, as a pen position
        public float Extent; // up to TextEnd, including ink past the advances
        public float BlockPos;
        public float BlockSize;
        public float Baseline; // within the line box
        public bool EndsWithNewline;
    }
}
