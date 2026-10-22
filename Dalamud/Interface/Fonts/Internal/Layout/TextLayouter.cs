using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Sources;

using Serilog;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>Lays out text for ImGui and Dalamud's own attributed text.</summary>
/// <remarks>Main thread only.</remarks>
internal static unsafe partial class TextLayouter
{
    // Frames a layout is kept after its last use. CacheFrames is for one used in a frame after the one it was made in,
    // and NewFrames for the others. Text that changes every frame makes layouts used once. Dropped soon, they do not
    // live long enough to be collected as old objects.
    private const int CacheFrames = 120;
    private const int NewFrames = 2;

    private static readonly Dictionary<byte[], Entry> Cache = new(KeyComparer.Instance);

    private static readonly Dictionary<byte[], Entry>.AlternateLookup<ReadOnlySpan<byte>> CacheLookup =
        Cache.GetAlternateLookup<ReadOnlySpan<byte>>();

    private static byte[] keyBuffer = new byte[1024];
    private static int lastSweepFrame = -1;
    private static bool errorReported;

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
    public static void Install() => ImGuiNative.SetTextCallbacks(&LayoutCallback, &MetricsCallback);

    /// <summary>Drops the cached layouts.</summary>
    public static void ClearCache()
    {
        foreach (var entry in Cache.Values)
            Evict(entry.Layout);
        Cache.Clear();
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
    /// <remarks>The layout may be shared with other callers.</remarks>
    public static TextLayout Create(
        ManagedFont font, in TextLayoutDesc desc, in ImTextStyle defaultStyle, ReadOnlySpan<byte> text,
        int codeUnitSize = 1, ReadOnlySpan<TextRun> runs = default, ReadOnlySpan<InlineObject> objects = default)
    {
        var frame = Sweep();

        // Layouts keep the fonts that slots stand for now, so the key changes when a slot's font does.
        font = font.Resolve();
        TextRun[]? resolvedRuns = null;
        for (var i = 0; i < runs.Length; i++)
        {
            if (runs[i].Font is { } runFont && runFont.Resolve() is var resolved && resolved != runFont)
            {
                resolvedRuns ??= runs.ToArray();
                resolvedRuns[i] = runs[i] with { Font = resolved };
            }
        }

        if (resolvedRuns is not null)
            runs = resolvedRuns;

        Requests++;
        RequestedBytes += text.Length;
        var key = BuildKey(font, desc, defaultStyle, text, codeUnitSize, runs, objects);
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(CacheLookup, key);
        if (!Unsafe.IsNullRef(ref entry))
        {
            entry.Use(frame);
            return entry.Layout;
        }

        // Copied, as making the layout may lay out other text in the key buffer.
        var ownKey = key.ToArray();
        Misses++;
        var start = FontProfiler.Begin(FontProfiler.Section.Layout);
        var layout = TextLayout.Rent(font, desc, defaultStyle, text, codeUnitSize, runs, objects);
        FontProfiler.End(FontProfiler.Section.Layout, start, text.Length);
        Add(ownKey, layout, frame);
        return layout;
    }

    /// <summary>Gets a cached layout of text made some other way, such as a SeString.</summary>
    /// <param name="font">The font, resolved.</param>
    /// <param name="desc">Layout parameters.</param>
    /// <param name="style">The style.</param>
    /// <param name="text">The text, as given.</param>
    /// <param name="extraKeys">Anything else the layout depends on.</param>
    /// <param name="layout">The layout, if cached.</param>
    /// <param name="key">The key to add the layout with otherwise (<see cref="AddCached"/>).</param>
    /// <returns>Whether it is cached.</returns>
    public static bool TryGetCached(
        ManagedFont font, in TextLayoutDesc desc, in ImTextStyle style, ReadOnlySpan<byte> text,
        ReadOnlySpan<long> extraKeys, [NotNullWhen(true)] out TextLayout? layout, out byte[]? key)
    {
        var frame = Sweep();
        var length = BeginKey(font, desc, style);
        Append(ref length, extraKeys.Length);
        foreach (var k in extraKeys)
            Append(ref length, k);
        var span = EndKey(length, text);
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(CacheLookup, span);
        if (!Unsafe.IsNullRef(ref entry))
        {
            entry.Use(frame);
            layout = entry.Layout;
            key = null;
            return true;
        }

        // Copied, as making the layout may lay out other text.
        layout = null;
        key = span.ToArray();
        return false;
    }

    /// <summary>Caches a layout made after <see cref="TryGetCached"/>.</summary>
    /// <param name="key">The key from <see cref="TryGetCached"/>.</param>
    /// <param name="layout">The layout.</param>
    public static void AddCached(byte[] key, TextLayout layout) => Add(key, layout, Sweep());

    // Caches a layout, holding a native reference to it, so that drawing it does not make one each time.
    private static void Add(byte[] key, TextLayout layout, int frame)
    {
        if (!Cache.TryAdd(key, new(layout, frame)))
            return;
        layout.Cached = true;
        _ = ToNative(layout);
    }

    // Stops caching a layout. It goes back to the pool once ImGui does not have it either.
    private static void Evict(TextLayout layout)
    {
        layout.Cached = false;
        _ = ReleaseNative((NativeLayout*)layout.NativeHandle);
    }

    private static ReadOnlySpan<byte> BuildKey(
        ManagedFont font, in TextLayoutDesc desc, in ImTextStyle style, ReadOnlySpan<byte> text, int codeUnitSize,
        ReadOnlySpan<TextRun> runs, ReadOnlySpan<InlineObject> objects)
    {
        var length = BeginKey(font, desc, style);
        Append(ref length, codeUnitSize);
        Append(ref length, runs.Length);
        foreach (var r in runs)
        {
            Append(ref length, r.TextBegin);
            Append(ref length, r.TextEnd);
            Append(ref length, r.Font is null ? 0 : (nint)r.Font.NativePointer);
            Append(ref length, r.Size);
            Append(ref length, r.Style.HasValue);
            AppendStyle(ref length, r.Style.GetValueOrDefault());
            Append(ref length, r.UserData);
        }

        Append(ref length, objects.Length);
        foreach (var o in objects)
            Append(ref length, o);
        return EndKey(length, text);
    }

    // The start of every key, in the key buffer. Returns its length.
    private static int BeginKey(ManagedFont font, in TextLayoutDesc desc, in ImTextStyle style)
    {
        var length = 0;
        Append(ref length, (nint)font.NativePointer);
        Append(ref length, desc);
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
    private static int Sweep()
    {
        var frame = ImGui.GetCurrentContext().Handle is not null ? ImGui.GetFrameCount() : 0;
        if (frame != lastSweepFrame)
            SweepFrame(frame);
        return frame;
    }

    // Sweep, once a frame. Apart, as enumerating the cache takes a try block, which costs every call otherwise.
    private static void SweepFrame(int frame)
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
            Array.Resize(ref keyBuffer, Math.Max(length, keyBuffer.Length * 2));
    }

    private static void Report(Exception e)
    {
        if (errorReported)
            return;
        errorReported = true;
        Log.Error(e, "Text layout failed, and further errors are not logged");
    }

    // Updated where it is in the cache.
    private struct Entry(TextLayout layout, int frame)
    {
        public TextLayout Layout { get; } = layout;

        public int CreatedFrame { get; } = frame;

        public int LastUsedFrame { get; private set; } = frame;

        // Whether it was used in a frame after the one it was made in. Measuring and drawing text in one frame is not
        // reuse.
        public bool Reused { get; private set; }

        public void Use(int frame)
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
