using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Utility;

using JetBrains.Annotations;

using Serilog;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>A font for <see cref="Layout.TextLayouter"/>.</summary>
/// <remarks>ImGui only passes <see cref="ImFont"/> pointers around (<see cref="NativePointer"/> and
/// <see cref="GetVariant"/>), which the text callbacks map back to the font with <see cref="ImFont.Impl"/>. All calls
/// come from the main thread, except <see cref="AddRef()"/> and <see cref="Release()"/>.</remarks>
internal abstract unsafe class ManagedFont
{
    /// <summary>The base-2 logarithm of <see cref="SubpixelStepCount"/>.</summary>
    public const int SubpixelStepShift = 2;

    /// <summary>The steps a device pixel is divided into for placing glyphs right of whole pixels.</summary>
    /// <seealso cref="GetGlyphImages"/>
    public const int SubpixelStepCount = 1 << SubpixelStepShift;

    // The number of variants from which unused ones get new styles instead of more being made.
    private const int RecycledVariants = 256;

    // The number of variants from which ones in use get new styles too.
    private const int MaxVariants = 65536;

    private static readonly ConcurrentDictionary<nint, WeakReference<ManagedFont>> Handles = new();

    [UsedImplicitly] // for keeping references
    private static readonly HashSet<ManagedFont> Referenced = [];

    private readonly Dictionary<ImTextStyle, (nint Pointer, int LastUsedFrame)> variants = [];
    private readonly ManagedFont?[] styled = new ManagedFont?[4];
    private nint handle;
    private int refs;
    private bool variantsFull;
    private ShapedGlyphList? pairOutput;
    private List<int>? graphemesBuffer;

    /// <summary>Initializes a new instance of the <see cref="ManagedFont"/> class.</summary>
    /// <param name="name">Name for debugging.</param>
    protected ManagedFont(string name) => this.Name = name;

    /// <summary>Finalizes an instance of the <see cref="ManagedFont"/> class.</summary>
    ~ManagedFont()
    {
        foreach (var v in this.variants.Values)
            NativeMemory.Free((void*)v.Pointer);
        this.variants.Clear();

        var h = Interlocked.Exchange(ref this.handle, 0);
        if (h != 0)
        {
            Handles.TryRemove(h, out _);
            NativeMemory.Free((void*)h);
        }
    }

    /// <summary>Gets the name for debugging.</summary>
    public string Name { get; }

    /// <summary>Gets the font with the default style, as ImGui knows it, valid while this object is alive.</summary>
    /// <remarks>Does not add a reference. Use <see cref="AddRef()"/> if ImGui may use the handle after this object is
    /// no longer referenced.</remarks>
    public ImFont* NativePointer
    {
        get
        {
            if (this.handle == 0)
            {
                var h = (ImFont*)NativeMemory.AllocZeroed((nuint)sizeof(ImFont));
                h->Impl = h;
                if (Interlocked.CompareExchange(ref this.handle, (nint)h, 0) == 0)
                    Handles[(nint)h] = new(this);
                else
                    NativeMemory.Free(h);
            }

            return (ImFont*)this.handle;
        }
    }

    /// <summary>Gets a value indicating whether the font shapes text itself.</summary>
    /// <remarks>Otherwise it is shaped codepoint by codepoint and kerned with <see cref="GetKerning"/>.</remarks>
    /// <seealso cref="Shape"/>
    public virtual bool CanShape => false;

    /// <summary>Gets the font behind a handle, of any style.</summary>
    /// <param name="font">The handle.</param>
    /// <returns>The font, or null if <paramref name="font"/> is null or not a live font.</returns>
    public static ManagedFont? FromNative(ImFont* font) =>
        font is not null && Handles.TryGetValue((nint)font->Impl, out var weak) && weak.TryGetTarget(out var f)
            ? f
            : null;

    /// <summary>Adds a reference to the font behind a handle.</summary>
    /// <param name="font">The handle.</param>
    public static void AddRef(ImFont* font) => FromNative(font)?.AddRef();

    /// <summary>Releases a reference to the font behind a handle.</summary>
    /// <param name="font">The handle.</param>
    public static void Release(ImFont* font) => FromNative(font)?.Release();

    /// <summary>Gets the kerning between the last character shaped with one font and the first shaped with another.
    /// </summary>
    /// <param name="left">The font of the first character.</param>
    /// <param name="leftSize">The size of the first character.</param>
    /// <param name="right">The font of the second character.</param>
    /// <param name="rightSize">The size of the second character.</param>
    /// <param name="first">The first character.</param>
    /// <param name="second">The second character.</param>
    /// <param name="density">Device pixels per logical pixel.</param>
    /// <returns>The adjustment in logical pixels.</returns>
    /// <remarks>The average of what the fonts that have both characters say.</remarks>
    public static float KernAcross(
        ManagedFont left, float leftSize, ManagedFont right, float rightSize, uint first, uint second, float density)
    {
        var l = left.TryGetPairKerning(first, second, leftSize, density, out var k) ? k : (float?)null;

        // ReSharper disable once CompareOfFloatsByEqualityOperator
        var r = (left != right || leftSize != rightSize) &&
                right.TryGetPairKerning(first, second, rightSize, density, out k)
                    ? k
                    : (float?)null;
        return AverageKerning(l, r);
    }

    /// <summary>Gets the kerning across fonts from what each font says.</summary>
    /// <param name="left">The kerning by the font of the first character, or null if it has no say.</param>
    /// <param name="right">The kerning by the font of the second character, or null if it has no say.</param>
    /// <returns>The average of the ones that have a say, or 0.</returns>
    public static float AverageKerning(float? left, float? right) =>
        (left, right) switch
        {
            ({ } l, { } r) => (l + r) / 2,
            ({ } l, null) => l,
            (null, { } r) => r,
            _ => 0,
        };

    /// <summary>Adds a reference, which keeps the font and its handle alive.</summary>
    public void AddRef()
    {
        if (Interlocked.Increment(ref this.refs) == 1)
        {
            lock (Referenced)
                Referenced.Add(this);
        }
    }

    /// <summary>Releases a reference.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref this.refs) == 0)
        {
            lock (Referenced)
                Referenced.Remove(this);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{this.GetType().Name}({this.Name})";

    /// <summary>Gets the font that does the work.</summary>
    /// <returns>The font a <see cref="FontSlot"/> currently stands for, or this font.</returns>
    public virtual ManagedFont Resolve() => this;

    /// <summary>Gets this font with a style, as ImGui knows it, valid until the frame being built has been drawn.
    /// </summary>
    /// <param name="style">The style.</param>
    /// <returns>The font, the same for the same style within a frame.</returns>
    /// <remarks>Variants are never freed before the font. Past <see cref="RecycledVariants"/>, the one used least
    /// recently is given the new style, unless it was used in this frame or the one before. A variant kept longer
    /// than that may then show another style, but never points to freed memory. At <see cref="MaxVariants"/>, the one
    /// used least recently is given the new style even if it is in use, which is logged.</remarks>
    public ImFont* GetVariant(in ImTextStyle style)
    {
        if (style == default)
            return this.NativePointer;

        var frame = ImGui.GetCurrentContext().Handle is not null ? ImGui.GetFrameCount() : 0;
        ref var v = ref CollectionsMarshal.GetValueRefOrNullRef(this.variants, style);
        if (!Unsafe.IsNullRef(ref v))
        {
            v.LastUsedFrame = frame;
            return (ImFont*)v.Pointer;
        }

        var p = this.variants.Count >= RecycledVariants ? this.TakeVariant(frame, 2) : null;
        if (p is null && this.variants.Count >= MaxVariants)
        {
            if (!this.variantsFull)
            {
                this.variantsFull = true;
                Log.Warning(
                    "[{Font}] {Count} styles are in use with the font. Styles in use are being replaced",
                    this,
                    this.variants.Count);
            }

            p = this.TakeVariant(frame, int.MinValue);
        }

        if (p is null)
        {
            p = (ImFont*)NativeMemory.AllocZeroed((nuint)sizeof(ImFont));
            p->Impl = this.NativePointer;
        }

        p->Style = style;
        this.variants.Add(style, ((nint)p, frame));
        return p;
    }

    /// <summary>Gets the bold or italic form of the font.</summary>
    /// <param name="bold">Whether bold.</param>
    /// <param name="italic">Whether italic.</param>
    /// <returns>The font with the faces for them where it has them, emboldened or slanted where not.</returns>
    public ManagedFont WithStyle(bool bold, bool italic)
    {
        var i = (bold ? 1 : 0) | (italic ? 2 : 0);
        return i == 0 ? this : this.styled[i] ??= this.CreateStyled(bold, italic);
    }

    /// <summary>Gets the metrics at a size, as designed.</summary>
    /// <param name="size">Size in logical pixels.</param>
    /// <param name="density">Device pixels per logical pixel.</param>
    /// <returns>The metrics.</returns>
    /// <remarks>The line gap is not split into the ascent and the descent.</remarks>
    public abstract FontMetrics GetMetrics(float size, float density);

    /// <summary>Maps codepoints to glyphs.</summary>
    /// <remarks>0 means missing. Glyph ids must stay below 1 &lt;&lt; 24, so that the font can be part of a
    /// <see cref="CompositeFont"/>.</remarks>
    /// <param name="codepoints">The codepoints.</param>
    /// <param name="glyphs">Receives one glyph per codepoint.</param>
    public abstract void GetGlyphIndices(ReadOnlySpan<uint> codepoints, Span<uint> glyphs);

    /// <summary>Gets glyph metrics.</summary>
    /// <param name="glyphs">The glyphs.</param>
    /// <param name="size">Size in logical pixels.</param>
    /// <param name="density">Device pixels per logical pixel.</param>
    /// <param name="metrics">Receives one entry per glyph.</param>
    public abstract void GetGlyphMetrics(
        ReadOnlySpan<uint> glyphs, float size, float density, Span<GlyphMetrics> metrics);

    /// <summary>Gets pair adjustments along a glyph sequence, for fonts that do not shape (game fonts).</summary>
    /// <remarks>Fonts that shape return zeros, as their pairs are measured with <see cref="Shape"/>.</remarks>
    /// <param name="glyphs">The glyphs.</param>
    /// <param name="size">Size in logical pixels.</param>
    /// <param name="density">Device pixels per logical pixel.</param>
    /// <param name="adjustments">Receives glyphs.Length - 1 values, in logical pixels.</param>
    public virtual void GetKerning(ReadOnlySpan<uint> glyphs, float size, float density, Span<float> adjustments) =>
        adjustments.Clear();

    /// <summary>Shapes a run and appends its glyphs, left to right.</summary>
    /// <param name="run">The run.</param>
    /// <param name="output">Receives the glyphs.</param>
    /// <remarks>Clusters are indices into <see cref="ShapeRun.Text"/>, ascending and on grapheme boundaries. The
    /// default makes a glyph per codepoint (leaving out marks and joiners the font lacks) and a cluster per grapheme,
    /// kerned with <see cref="GetKerning"/>.</remarks>
    public virtual void Shape(ShapeRun run, ShapedGlyphList output)
    {
        var n = run.End - run.Begin;
        if (n <= 0)
            return;
        var graphemes = this.FindGraphemes(run);
        var glyphs = n <= 256 ? stackalloc uint[n] : new uint[n];
        this.GetGlyphIndices(run.Text[run.Begin..run.End], glyphs);
        var first = output.Count;
        var grapheme = run.Begin;
        for (int i = 0, g = 0; i < n; i++)
        {
            var index = run.Begin + i;
            if (g < graphemes.Count && graphemes[g] == index)
            {
                grapheme = index;
                g++;
            }
            else if (glyphs[i] == 0)
            {
                continue;
            }

            output.Add(glyphs[i], grapheme, 0);
        }

        var added = output.Span[first..];
        var ids = glyphs[..added.Length];
        for (var i = 0; i < added.Length; i++)
            ids[i] = added[i].Glyph;
        var metrics = added.Length <= 128 ? stackalloc GlyphMetrics[added.Length] : new GlyphMetrics[added.Length];
        this.GetGlyphMetrics(ids, run.Size, run.Density, metrics);
        for (var i = 0; i < added.Length; i++)
            added[i].Advance = metrics[i].Advance;
        if (added.Length < 2)
            return;
        var kerning = added.Length <= 256 ? stackalloc float[added.Length - 1] : new float[added.Length - 1];
        this.GetKerning(ids, run.Size, run.Density, kerning);
        for (var i = 0; i < kerning.Length; i++)
            added[i].Advance += kerning[i];
    }

    /// <summary>Gets the kerning of a pair of characters with this font alone.</summary>
    /// <param name="first">The first character.</param>
    /// <param name="second">The second character.</param>
    /// <param name="size">Size in logical pixels.</param>
    /// <param name="density">Device pixels per logical pixel.</param>
    /// <param name="kerning">The adjustment in logical pixels.</param>
    /// <returns>Whether the font has both characters, and so has a say.</returns>
    /// <remarks>Shaped as a pair if the font shapes, else from <see cref="GetKerning"/>.</remarks>
    public bool TryGetPairKerning(uint first, uint second, float size, float density, out float kerning)
    {
        kerning = 0;
        Span<uint> pair = [first, second];
        Span<uint> glyphs = stackalloc uint[2];
        this.GetGlyphIndices(pair, glyphs);
        if (glyphs[0] == 0 || glyphs[1] == 0)
            return false;

        if (!this.CanShape)
        {
            Span<float> adjustment = stackalloc float[1];
            this.GetKerning(glyphs, size, density, adjustment);
            kerning = adjustment[0];
            return true;
        }

        var output = this.pairOutput ??= new();
        output.Clear();
        this.Shape(new(pair, 0, 2, size, density), output);
        if (output.Count != 2 || output.Span[1].Cluster != 1)
            return true;
        var together = output.Span[0].Advance;
        output.Clear();
        this.Shape(new(pair, 0, 1, size, density), output);
        if (output.Count == 1)
            kerning = together - output.Span[0].Advance;
        return true;
    }

    /// <summary>Produces glyph images.</summary>
    /// <param name="glyphs">The glyphs.</param>
    /// <param name="subpixelSteps">Per glyph, how far the pen is right of a whole device pixel, in
    /// 1/<see cref="SubpixelStepCount"/> pixel steps. The font may ignore it.</param>
    /// <param name="size">Size in logical pixels.</param>
    /// <param name="density">Device pixels per logical pixel.</param>
    /// <param name="embolden">Synthetic bold, in device pixels to widen the strokes by horizontally.</param>
    /// <param name="subpixel">Whether subpixel coverage is wanted. The font may ignore it.</param>
    /// <param name="images">Receives one entry per glyph.</param>
    public abstract void GetGlyphImages(
        ReadOnlySpan<uint> glyphs, ReadOnlySpan<byte> subpixelSteps, float size, float density, float embolden,
        bool subpixel, Span<GlyphImage> images);

    /// <summary>Gets a span of at least <paramref name="count"/> elements, growing the buffer if needed.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="count">The number of elements.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The first <paramref name="count"/> elements of the buffer.</returns>
    protected static Span<T> Rent<T>(ref T[] buffer, int count)
    {
        buffer = buffer.Grow(count);
        return buffer.AsSpan(0, count);
    }

    /// <summary>Finds the end of the glyphs from the same source font.</summary>
    /// <param name="glyphs">The glyphs.</param>
    /// <param name="begin">The first glyph.</param>
    /// <param name="shift">The bit position of the source index in the glyph ids.</param>
    /// <returns>The index past the last glyph with the same source index as <paramref name="begin"/>.</returns>
    protected static int RunEnd(ReadOnlySpan<uint> glyphs, int begin, int shift)
    {
        var end = begin + 1;
        while (end < glyphs.Length && glyphs[end] >> shift == glyphs[begin] >> shift)
            end++;
        return end;
    }

    /// <summary>Makes the bold or italic form of the font, for <see cref="WithStyle"/>.</summary>
    /// <param name="bold">Whether bold.</param>
    /// <param name="italic">Whether italic.</param>
    /// <returns>The font. The default emboldens and slants this one.</returns>
    protected virtual ManagedFont CreateStyled(bool bold, bool italic) => new SyntheticStyleFont(this, bold, italic);

    /// <summary>Finds the grapheme starts of a run.</summary>
    /// <param name="run">The run.</param>
    /// <returns>The starts, in a buffer reused by the next call.</returns>
    protected List<int> FindGraphemes(ShapeRun run)
    {
        var list = this.graphemesBuffer ??= [];
        run.FindGraphemes(list);
        return list;
    }

    // Removes the variant used least recently, if it was last used at least minAge frames ago, for reuse.
    private ImFont* TakeVariant(int frame, int minAge)
    {
        var oldest = default(KeyValuePair<ImTextStyle, (nint Pointer, int LastUsedFrame)>);
        var found = false;
        foreach (var kv in this.variants)
        {
            if (frame - kv.Value.LastUsedFrame >= minAge &&
                (!found || kv.Value.LastUsedFrame < oldest.Value.LastUsedFrame))
                (oldest, found) = (kv, true);
        }

        if (!found)
            return null;
        this.variants.Remove(oldest.Key);
        return (ImFont*)oldest.Value.Pointer;
    }
}
