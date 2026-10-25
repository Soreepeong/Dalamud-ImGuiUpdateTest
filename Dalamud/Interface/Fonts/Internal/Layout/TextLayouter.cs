using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Markup;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Utility;

using Serilog;

using TerraFX.Interop.Windows;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>Lays out text for ImGui and Dalamud's own attributed text.</summary>
/// <remarks>Main thread only.</remarks>
internal static unsafe class TextLayouter
{
    // Frames a layout is kept after its last use. CacheFrames is for one used in a frame after the one it was made in,
    // and NewFrames for the others. Text that changes every frame makes layouts used once. Dropped soon, they do not
    // live long enough to be collected as old objects.
    private const int CacheFrames = 120;
    private const int NewFrames = 2;

    // Keys up to this long are built in a kept buffer, and longer ones in a rented one, so that one long text does not
    // keep memory for good.
    private const int MaxKeptKeyLength = 1 << 16;

    private static readonly Dictionary<byte[], Entry> Cache = new(KeyComparer.Instance);

    private static readonly Dictionary<byte[], Entry>.AlternateLookup<ReadOnlySpan<byte>> CacheLookup =
        Cache.GetAlternateLookup<ReadOnlySpan<byte>>();

    private static byte[] keptKeyBuffer = new byte[1024];

    // The kept buffer, or a rented one until ReleaseKey.
    private static byte[] keyBuffer = keptKeyBuffer;
    private static long lastSweepFrame = -1;

    private enum KeyKind : byte
    {
        Plain,
        Styled,
    }

    /// <summary>Gets the number of layouts asked for so far, cached or not.</summary>
    public static long Requests { get; private set; }

    /// <summary>Gets the number of layouts made so far, as they were not cached.</summary>
    public static long Misses { get; private set; }

    /// <summary>Gets the number of bytes of text asked to lay out so far, cached or not.</summary>
    public static long RequestedBytes { get; private set; }

    /// <summary>Gets the number of cached layouts.</summary>
    public static int CacheCount => Cache.Count;

    /// <summary>Gets the number of cached layouts used in a frame after the one they were made in, as of the last
    /// sweep. The others are dropped sooner.</summary>
    public static int ReusedCount { get; private set; }

    /// <summary>Sets the text callbacks of the current ImGui context.</summary>
    public static void Install()
    {
        NativeTextLayouts.Register();
        ImGuiNative.SetTextCallbacks(&LayoutCallback, &MetricsCallback);
    }

    /// <summary>Lays out text.</summary>
    /// <param name="font">The default font.</param>
    /// <param name="desc">Layout parameters.</param>
    /// <param name="defaultStyle">The style of text outside runs.</param>
    /// <param name="text">The text, in bytes.</param>
    /// <param name="codeUnitSize">1 for UTF-8, 2 for UTF-16, 4 for UTF-32 (little endian).</param>
    /// <param name="runs">Attributed ranges, in byte offsets into <paramref name="text"/>.</param>
    /// <param name="objects">Inline objects, one per U+FFFC in the text.</param>
    /// <returns>The layout.</returns>
    /// <remarks>The layout may be shared with other callers. It is valid while cached, and is recycled when the cache
    /// and ImGui have let go of it. Do not keep it across frames.</remarks>
    public static TextLayout Create(
        Font font, in TextLayoutDesc desc, in ImTextStyle defaultStyle, ReadOnlySpan<byte> text,
        int codeUnitSize = 1, ReadOnlySpan<TextRun> runs = default, ReadOnlySpan<InlineObject> objects = default)
    {
        var frame = Sweep();

        // Layouts keep the fonts that slots stand for now, so the key changes when a slot's font does.
        font = font.Resolve();
        Requests++;
        RequestedBytes += text.Length;

        // Only the first line is laid out, which ends at the first LF at the latest. InputText asks for each wrapped
        // row with the rest of the buffer.
        if ((desc.Flags & ImTextLayoutFlags.StopOnNewLine) != 0 && codeUnitSize == 1 &&
            text.IndexOf((byte)'\n') is var lf and >= 0)
        {
            text = text[..(lf + 1)];
        }

        var key = BuildKey(font, desc, defaultStyle, text, codeUnitSize, runs, objects);
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(CacheLookup, key);
        if (!Unsafe.IsNullRef(ref entry))
        {
            ReleaseKey();
            entry.Use(frame);
            return entry.Layout;
        }

        // Copied, as making the layout may lay out other text in the key buffer.
        byte[] ownKey = [.. key];
        ReleaseKey();
        Misses++;
        var start = FontProfiler.Begin(FontProfiler.Section.Layout);
        var layout = TextLayout.Rent(font, desc, defaultStyle, text, codeUnitSize, runs, objects);
        FontProfiler.End(FontProfiler.Section.Layout, start, text.Length);
        Add(ownKey, layout, frame);
        return layout;
    }

    /// <summary>Lays out text as its markup reads it, or as plain text if the markup has no styler.</summary>
    /// <param name="font">The font.</param>
    /// <param name="desc">Layout parameters.</param>
    /// <param name="style">The style the text starts with. Its markup decides how the text is read.</param>
    /// <param name="text">The text.</param>
    /// <returns>The layout.</returns>
    /// <remarks>Stylers lay out nothing outside the main thread. The text is then laid out as plain text.</remarks>
    public static TextLayout CreateStyled(
        Font font, in TextLayoutDesc desc, in ImTextStyle style, ReadOnlySpan<byte> text) =>
        TextStylers.Get(style.Markup)?.CreateLayout(font, desc, style, text) ?? Create(font, desc, style, text);

    /// <summary>Gets a cached layout of text made some other way, such as a SeString.</summary>
    /// <param name="font">The font, resolved.</param>
    /// <param name="desc">Layout parameters.</param>
    /// <param name="style">The style.</param>
    /// <param name="text">The text, as given.</param>
    /// <param name="extraKeys">Anything else the layout depends on.</param>
    /// <param name="layout">The layout, if cached.</param>
    /// <param name="key">The key to add the layout with otherwise.</param>
    /// <returns>Whether it is cached.</returns>
    /// <seealso cref="AddCached"/>
    public static bool TryGetCached(
        Font font, in TextLayoutDesc desc, in ImTextStyle style, ReadOnlySpan<byte> text,
        ReadOnlySpan<long> extraKeys, [NotNullWhen(true)] out TextLayout? layout, out byte[]? key)
    {
        var frame = Sweep();
        var length = BeginKey(KeyKind.Styled, font, desc, style);
        Append(ref length, extraKeys.Length);
        foreach (var k in extraKeys)
            Append(ref length, k);
        var span = EndKey(length, text);
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(CacheLookup, span);
        if (!Unsafe.IsNullRef(ref entry))
        {
            ReleaseKey();
            entry.Use(frame);
            layout = entry.Layout;
            key = null;
            return true;
        }

        // Copied, as making the layout may lay out other text.
        layout = null;
        key = [.. span];
        ReleaseKey();
        return false;
    }

    /// <summary>Caches a layout made after <see cref="TryGetCached"/>.</summary>
    /// <param name="key">The key from <see cref="TryGetCached"/>.</param>
    /// <param name="layout">The layout.</param>
    public static void AddCached(byte[] key, TextLayout layout) => Add(key, layout, Sweep());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int LayoutCallback(
        ImFont* font, ImTextDesc* desc, byte* text, byte* textEnd, ImTextLayout** outLayout)
    {
        *outLayout = null;

        // ImGui measures the text as empty, and draws nothing.
        if (!ThreadSafety.IsMainThread)
            return RPC.RPC_E_WRONG_THREAD;

        try
        {
            if (Font.FromNative(font) is not { } managed)
                return E.E_INVALIDARG; // The font is not Dalamud's.
            var d = TextLayoutDesc.FromNative(*desc);

            var bytes = new ReadOnlySpan<byte>(text, (int)(textEnd - text));
            *outLayout = NativeTextLayouts.ToNative(CreateStyled(managed, d, font->Style, bytes));
            return 0;
        }
        catch (Exception e)
        {
            Report(e);
            return e.HResult < 0 ? e.HResult : E.E_FAIL;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MetricsCallback(
        ImFont* font, float size, float density, ImTextMetricsFlags flags, ImTextMetrics* outMetrics)
    {
        try
        {
            // Fonts are main thread only. Other threads get the fallback.
            if (ThreadSafety.IsMainThread && Font.FromNative(font) is { } managed)
            {
                var m = managed.GetMetrics(size, density);
                if ((flags & ImTextMetricsFlags.DesignMetrics) == 0)
                    m = m.SplitLineGap();
                WriteMetrics(m, outMetrics);
                return;
            }
        }
        catch (Exception e)
        {
            Report(e);
        }

        WriteMetrics(FontMetrics.Fallback(size), outMetrics);
        return;

        static void WriteMetrics(in FontMetrics m, ImTextMetrics* outMetrics) =>
            *outMetrics = new(m.Ascent, m.Descent, m.LineHeight, m.UnderlinePosition, m.UnderlineThickness);
    }

    // Caches a layout, holding a native reference to it, so that drawing it does not make one each time.
    private static void Add(byte[] key, TextLayout layout, long frame)
    {
        if (!Cache.TryAdd(key, new(layout, frame)))
            return;
        layout.Cached = true;
        _ = NativeTextLayouts.ToNative(layout);
    }

    // Stops caching a layout. It goes back to the pool once ImGui does not have it either.
    private static void Evict(TextLayout layout)
    {
        layout.Cached = false;

        // A plugin that released the cache's reference too has freed the native layout already.
        if (layout.NativeHandle != 0)
            _ = NativeTextLayouts.Release(layout);
    }

    private static ReadOnlySpan<byte> BuildKey(
        Font font, in TextLayoutDesc desc, in ImTextStyle style, ReadOnlySpan<byte> text, int codeUnitSize,
        ReadOnlySpan<TextRun> runs, ReadOnlySpan<InlineObject> objects)
    {
        var length = BeginKey(KeyKind.Plain, font, desc, style);
        Append(ref length, codeUnitSize);
        Append(ref length, runs.Length);
        foreach (var r in runs)
        {
            Append(ref length, r.TextBegin);
            Append(ref length, r.TextEnd);
            Append(ref length, r.Style.HasValue);
            AppendStyle(ref length, r.Style.GetValueOrDefault());
            Append(ref length, r.UserData);
        }

        Append(ref length, objects.Length);
        foreach (var o in objects)
            Append(ref length, o);
        return EndKey(length, text);
    }

    // The start of every key, in the key buffer. The kind tells keys of plain layouts from those of stylers, whose
    // bytes may be equal after it. Returns the length.
    private static int BeginKey(KeyKind kind, Font font, in TextLayoutDesc desc, in ImTextStyle style)
    {
        var length = 0;
        Append(ref length, kind);
        Append(ref length, (nint)font.NativePointer);
        Append(ref length, desc.Canonical);
        AppendStyle(ref length, style);
        return length;
    }

    private static ReadOnlySpan<byte> EndKey(int length, ReadOnlySpan<byte> text)
    {
        EnsureKey(length + text.Length);
        text.CopyTo(keyBuffer.AsSpan(length));
        return keyBuffer.AsSpan(0, length + text.Length);
    }

    private static void Append<T>(ref int length, in T value)
        where T : unmanaged
    {
        EnsureKey(length + sizeof(T));
        Unsafe.WriteUnaligned(ref keyBuffer[length], value);
        length += sizeof(T);
    }

    // Field by field, as the padding after the flags and inside the lines is not always zero.
    private static void AppendStyle(ref int length, in ImTextStyle style)
    {
        Append(ref length, style.Color);
        Append(ref length, (style.Bold ? 1 : 0) | (style.Italic ? 2 : 0) | ((int)style.Markup << 2));
        Append(ref length, style.Shadow);
        Append(ref length, style.Edge);
        Append(ref length, style.Background);
        Append(ref length, style.Lines.ToDecoration());
        Append(ref length, style.ExtraDecoration);
    }

    // Drops layouts not used lately, once a frame. Returns the frame.
    private static long Sweep()
    {
        var frame = FontClock.Frame;
        if (frame != lastSweepFrame)
            SweepFrame(frame);
        return frame;
    }

    // Sweep, once a frame. Its own method, so that the calls that skip it stay small.
    private static void SweepFrame(long frame)
    {
        lastSweepFrame = frame;

        // Removing while enumerating is allowed for Dictionary.
        var reused = 0;
        foreach (var (k, e) in Cache)
        {
            if (frame - e.LastUsedFrame > (e.Reused ? CacheFrames : NewFrames))
            {
                Cache.Remove(k);
                Evict(e.Layout);
            }
            else if (e.Reused)
            {
                reused++;
            }
        }

        ReusedCount = reused;
    }

    private static void EnsureKey(int length)
    {
        if (keyBuffer.Length < length)
            GrowKey(length);
    }

    private static void GrowKey(int length)
    {
        var grown = Math.Max(length, keyBuffer.Length * 2);
        if (length <= MaxKeptKeyLength)
        {
            Array.Resize(ref keptKeyBuffer, Math.Min(grown, MaxKeptKeyLength));
            keyBuffer = keptKeyBuffer;
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(grown);
        keyBuffer.CopyTo(rented, 0);
        ReleaseKey();
        keyBuffer = rented;
    }

    // Gives a rented key buffer back, once the key is looked up or copied.
    private static void ReleaseKey()
    {
        if (keyBuffer == keptKeyBuffer)
            return;
        ArrayPool<byte>.Shared.Return(keyBuffer);
        keyBuffer = keptKeyBuffer;
    }

    // Logs an error, at most once a minute for each kind of exception and the method that threw it.
    private static void Report(Exception e) => Log.Logger.Error(e, "Text layout failed");

    // Updated where it is in the cache.
    private struct Entry(TextLayout layout, long frame)
    {
        public TextLayout Layout { get; } = layout;

        public long LastUsedFrame { get; private set; } = frame;

        // Whether it was used in a frame after the one it was made in. Measuring and drawing text in one frame is not
        // reuse.
        public bool Reused { get; private set; }

        private long CreatedFrame { get; } = frame;

        public void Use(long frame)
        {
            this.LastUsedFrame = frame;
            this.Reused |= frame != this.CreatedFrame;
        }
    }

    private sealed class KeyComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => this.GetHashCode(obj.AsSpan());

        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<byte> alternate)
        {
            HashCode h = default;
            h.AddBytes(alternate);
            return h.ToHashCode();
        }

        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
    }
}
