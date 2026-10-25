using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Glyphs;

using Serilog;

namespace Dalamud.Interface.Fonts.Internal.Sources;

/// <summary>A font for <see cref="Layout.TextLayouter"/>.</summary>
/// <remarks><ul>
/// <li>ImGui only passes <see cref="ImFont"/> pointers around, which the text callbacks map back to the font with
/// <see cref="ImFont.Impl"/>.</li>
/// <li>All calls come from the main thread.</li>
/// </ul></remarks>
/// <seealso cref="NativePointer"/>
/// <seealso cref="GetVariant"/>
internal abstract unsafe class Font
{
    /// <summary>The base-2 logarithm of <see cref="SubpixelStepCount"/>.</summary>
    public const int SubpixelStepShift = 2;

    /// <summary>The steps a device pixel is divided into for placing glyphs right of whole pixels.</summary>
    /// <seealso cref="GetGlyphImages"/>
    public const int SubpixelStepCount = 1 << SubpixelStepShift;

    /// <summary>The steps a device pixel is divided into for the sizes glyph images are made at.</summary>
    /// <remarks>Fonts make images at sizes rounded to a multiple of 1 / this. <see cref="CompositeFont"/> relies on it
    /// to fit glyphs.</remarks>
    public const int SizeStepsPerPixel = 4;

    // The number of variants from which unused ones get new styles instead of more being made.
    private const int RecycledVariants = 256;

    // The number of variants from which ones in use get new styles too.
    private const int MaxVariants = 65536;

    // The number of pairs whose kerning is kept, from which all are forgotten.
    private const int MaxPairKernings = 4096;

    private static readonly ConcurrentDictionary<nint, WeakReference<Font>> Handles = new();

    private readonly Dictionary<ImTextStyle, (nint Pointer, long LastUsedFrame)> variants = [];
    private readonly Font?[] styled = new Font?[4];
    private nint handle;
    private bool variantsFull;

    // Variants taken from styles not used lately, for new styles, and the frame they were last looked for in.
    private Stack<nint>? freeVariants;
    private long lastVariantSweep = -1;
    private ShapedGlyphList? pairOutput;
    private List<int>? graphemesBuffer;

    // Kerning of pairs, or null if the font does not have both characters.
    private Dictionary<(uint First, uint Second, float Size, float Density), float?>? pairKernings;

    /// <summary>Initializes a new instance of the <see cref="Font"/> class.</summary>
    /// <param name="name">Name for debugging.</param>
    protected Font(string name) => this.Name = name;

    /// <summary>Finalizes an instance of the <see cref="Font"/> class.</summary>
    ~Font()
    {
        foreach (var v in this.variants.Values)
            NativeMemory.Free((void*)v.Pointer);
        this.variants.Clear();
        while (this.freeVariants?.TryPop(out var p) is true)
            NativeMemory.Free((void*)p);

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
    public ImFont* NativePointer
    {
        get
        {
            if (this.handle == 0)
            {
                var h = (ImFont*)NativeMemory.AllocZeroed((nuint)sizeof(ImFont));
                *h = new(h, default);
                if (Interlocked.CompareExchange(ref this.handle, (nint)h, 0) == 0)
                    Handles[(nint)h] = new(this);
                else
                    NativeMemory.Free(h);
            }

            return (ImFont*)this.handle;
        }
    }

    /// <summary>Gets the font behind a handle, of any style.</summary>
    /// <param name="font">The handle.</param>
    /// <returns>The font, or null if <paramref name="font"/> is null or not a live font.</returns>
    public static Font? FromNative(ImFont* font) =>
        font is not null && Handles.TryGetValue((nint)font->Impl, out var weak) && weak.TryGetTarget(out var f)
            ? f
            : null;

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
        Font left, float leftSize, Font right, float rightSize, uint first, uint second, float density)
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

    /// <inheritdoc/>
    public override string ToString() => $"{this.GetType().Name}({this.Name})";

    /// <summary>Gets the font that does the work.</summary>
    /// <returns>This font, or the font it currently stands for.</returns>
    /// <seealso cref="SlotFont"/>
    public virtual Font Resolve() => this;

    /// <summary>Gets this font with a style, as ImGui knows it, valid until the frame being built has been drawn.
    /// </summary>
    /// <param name="style">The style.</param>
    /// <returns>The font, the same for the same style within a frame.</returns>
    /// <remarks><ul>
    /// <li>Variants are never freed before the font.</li>
    /// <li>Past <see cref="RecycledVariants"/>, variants not used in this frame or the one before are given new
    /// styles. A variant kept longer than that may then show another style, but never points to freed memory.</li>
    /// <li>At <see cref="MaxVariants"/>, the one used least recently is given the new style even if it is in use,
    /// which is logged.</li>
    /// </ul></remarks>
    public ImFont* GetVariant(in ImTextStyle style)
    {
        if (style == default)
            return this.NativePointer;

        var frame = FontClock.Frame;
        ref var v = ref CollectionsMarshal.GetValueRefOrNullRef(this.variants, style);
        if (!Unsafe.IsNullRef(ref v))
        {
            v.LastUsedFrame = frame;
            return (ImFont*)v.Pointer;
        }

        var p = this.freeVariants is { Count: > 0 } free ? (ImFont*)free.Pop()
                : this.variants.Count >= RecycledVariants ? this.TakeUnusedVariant(frame)
                : null;
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

            p = this.TakeOldestVariant();
        }

        if (p is null)
            p = (ImFont*)NativeMemory.AllocZeroed((nuint)sizeof(ImFont));
        *p = new(this.NativePointer, style);
        this.variants.Add(style, ((nint)p, frame));
        return p;
    }

    /// <summary>Gets the bold or italic form of the font.</summary>
    /// <param name="bold">Whether bold.</param>
    /// <param name="italic">Whether italic.</param>
    /// <returns>The font with the faces for them where it has them, emboldened or slanted where not.</returns>
    public Font WithStyle(bool bold, bool italic)
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

    /// <summary>Shapes a run and appends its glyphs, left to right.</summary>
    /// <param name="run">The run.</param>
    /// <param name="output">Receives the glyphs.</param>
    /// <remarks>Clusters are indices into <see cref="ShapeRun.Text"/>, ascending and on grapheme boundaries.</remarks>
    public abstract void Shape(ShapeRun run, ShapedGlyphList output);

    /// <summary>Gets the kerning of a pair of characters with this font alone.</summary>
    /// <param name="first">The first character.</param>
    /// <param name="second">The second character.</param>
    /// <param name="size">Size in logical pixels.</param>
    /// <param name="density">Device pixels per logical pixel.</param>
    /// <param name="kerning">The adjustment in logical pixels.</param>
    /// <returns>Whether the font has both characters and shapes them to one glyph each, and so has a say.</returns>
    /// <remarks>The pair is shaped. Pairs are kept, as text switches fonts at the same pairs, such as at spaces between
    /// scripts.</remarks>
    public bool TryGetPairKerning(uint first, uint second, float size, float density, out float kerning)
    {
        // The font stood for may change, so the font that does the work keeps what it says.
        var resolved = this.Resolve();
        if (!ReferenceEquals(resolved, this))
            return resolved.TryGetPairKerning(first, second, size, density, out kerning);

        var pairs = this.pairKernings ??= [];
        ref var kept = ref CollectionsMarshal.GetValueRefOrNullRef(pairs, (first, second, size, density));
        if (!Unsafe.IsNullRef(ref kept))
        {
            kerning = kept.GetValueOrDefault();
            return kept.HasValue;
        }

        var has = this.TryGetPairKerningUncached(first, second, size, density, out kerning);
        if (pairs.Count >= MaxPairKernings)
            pairs.Clear();
        pairs[(first, second, size, density)] = has ? kerning : null;
        return has;
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

    /// <summary>Makes the bold or italic form of the font, for <see cref="WithStyle"/>.</summary>
    /// <param name="bold">Whether bold.</param>
    /// <param name="italic">Whether italic.</param>
    /// <returns>The font. The default emboldens and slants this one.</returns>
    protected virtual Font CreateStyled(bool bold, bool italic) => new SyntheticStyleFont(this, bold, italic);

    /// <summary>Finds the grapheme starts of a run.</summary>
    /// <param name="run">The run.</param>
    /// <returns>The starts, in a buffer reused by the next call.</returns>
    protected List<int> FindGraphemes(ShapeRun run)
    {
        var list = this.graphemesBuffer ??= [];
        run.FindGraphemes(list);
        return list;
    }

    // TryGetPairKerning, without the pairs kept.
    private bool TryGetPairKerningUncached(uint first, uint second, float size, float density, out float kerning)
    {
        kerning = 0;
        Span<uint> pair = [first, second];
        Span<uint> glyphs = stackalloc uint[2];
        this.GetGlyphIndices(pair, glyphs);
        if (glyphs[0] == 0 || glyphs[1] == 0)
            return false;

        var output = this.pairOutput ??= new();
        output.Clear();
        this.Shape(new(pair, 0, 2, size, density), output);
        // A ligature or a cluster has no kerning between its parts. A zero here would halve the other font's say.
        if (output.Count != 2 || output.Span[1].Cluster != 1)
            return false;
        var together = output.Span[0].Advance;
        output.Clear();
        this.Shape(new(pair, 0, 1, size, density), output);
        if (output.Count != 1)
            return false;
        kerning = together - output.Span[0].Advance;
        return true;
    }

    // Unused variants are looked for once a frame at most, so that each new style does not go through all variants.
    private ImFont* TakeUnusedVariant(long frame)
    {
        if (this.lastVariantSweep == frame)
            return null;
        this.lastVariantSweep = frame;
        var free = this.freeVariants ??= new();
        foreach (var kv in this.variants)
        {
            if (frame - kv.Value.LastUsedFrame >= 2)
            {
                free.Push(kv.Value.Pointer);
                this.variants.Remove(kv.Key);
            }
        }

        return free.TryPop(out var p) ? (ImFont*)p : null;
    }

    private ImFont* TakeOldestVariant()
    {
        var oldest = default(KeyValuePair<ImTextStyle, (nint Pointer, long LastUsedFrame)>);
        var found = false;
        foreach (var kv in this.variants)
        {
            if (!found || kv.Value.LastUsedFrame < oldest.Value.LastUsedFrame)
                (oldest, found) = (kv, true);
        }

        if (!found)
            return null;
        this.variants.Remove(oldest.Key);
        return (ImFont*)oldest.Value.Pointer;
    }
}
