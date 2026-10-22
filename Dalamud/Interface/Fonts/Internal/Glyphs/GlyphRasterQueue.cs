using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Logging.Internal;
using Dalamud.Utility;

namespace Dalamud.Interface.Fonts.Internal.Glyphs;

/// <summary>Decides whether a glyph missing from a font's cache is rasterized now or on the thread pool, so that new
/// text does not hold up frames.</summary>
/// <remarks><ol>
/// <li>Beyond <see cref="FrameBudget"/>, glyphs are rasterized on the thread pool, and drawn as nothing until they are
/// packed at the end of a later frame.</li>
/// <li>Main thread only, but for the rasterizing.</li>
/// </ol></remarks>
internal static class GlyphRasterQueue
{
    // Bounds the bitmaps held when text changes faster than glyphs are rasterized.
    private const int MaxPending = 4096;

    private static readonly ModuleLog Log = new(typeof(GlyphRasterQueue));
    private static readonly long PackBudgetTicks = Stopwatch.Frequency / 2000;

    // The frame each pending glyph was last asked for in.
    private static readonly Dictionary<(IConcurrentGlyphRasterizer Font, ulong Key), long> Pending = [];
    private static readonly ConcurrentQueue<(GlyphRasterRequest Request, GlyphBitmap? Bitmap)> Finished = new();
    private static TimeSpan frameBudget = TimeSpan.MaxValue;
    private static long spentTicks;
    private static long frame;
    private static int synchronousDepth;

    /// <summary>Gets or sets the time glyphs may be rasterized on the main thread for in a frame.</summary>
    /// <remarks>Unlimited by default. <see cref="FrameBudgetOverride"/> takes its place if set.</remarks>
    public static TimeSpan FrameBudget
    {
        get => FrameBudgetOverride ?? frameBudget;
        set => frameBudget = value;
    }

    /// <summary>Gets or sets the time to use instead of <see cref="FrameBudget"/> for testing, or null for none.
    /// </summary>
    public static TimeSpan? FrameBudgetOverride { get; set; }

    /// <summary>Gets the number of glyphs being rasterized on the thread pool, or waiting to be packed.</summary>
    public static int PendingCount => Pending.Count;

    /// <summary>Makes glyphs be rasterized when asked for, until disposed.</summary>
    /// <returns>A disposable that ends the scope. Its default value ends nothing.</returns>
    public static ImPopFontDisposable Synchronous()
    {
        BeginSynchronous();
        return new(completeGlyphs: true);
    }

    /// <summary>Makes glyphs be rasterized when asked for, until <see cref="EndSynchronous"/>. Nests.</summary>
    public static void BeginSynchronous() => synchronousDepth++;

    /// <summary>Ends what <see cref="BeginSynchronous"/> began.</summary>
    public static void EndSynchronous()
    {
        if (synchronousDepth > 0)
            synchronousDepth--;
    }

    /// <summary>Tests whether a glyph is to be drawn as nothing for now, as it is rasterized on the thread pool.</summary>
    /// <param name="request">What to rasterize.</param>
    /// <returns>Whether it is deferred. Otherwise, the caller rasterizes it, within <see cref="Measure"/>.</returns>
    public static bool TryDefer(in GlyphRasterRequest request)
    {
        if (synchronousDepth > 0 || !ThreadSafety.IsMainThread)
            return false;
        ref var asked = ref CollectionsMarshal.GetValueRefOrNullRef(Pending, (request.Font, request.Key));
        if (!Unsafe.IsNullRef(ref asked))
        {
            asked = frame;
            return true;
        }

        if (FrameBudget == TimeSpan.MaxValue || spentTicks < FrameBudget.TotalSeconds * Stopwatch.Frequency)
            return false;
        if (Pending.Count >= MaxPending)
            return true;

        Pending.Add((request.Font, request.Key), frame);
        FontProfiler.Count(FontProfiler.Section.Deferred);
        ThreadPool.UnsafeQueueUserWorkItem(
            static r => Finished.Enqueue((r, r.Font.RasterizeConcurrently(r))),
            request,
            false);
        return true;
    }

    /// <summary>Starts measuring a glyph rasterized on the main thread, against <see cref="FrameBudget"/>.</summary>
    /// <returns>The time to pass to <see cref="EndMeasure"/>.</returns>
    public static long Measure() => Stopwatch.GetTimestamp();

    /// <summary>Ends measuring a glyph rasterized on the main thread.</summary>
    /// <param name="start">What <see cref="Measure"/> returned.</param>
    public static void EndMeasure(long start)
    {
        if (ThreadSafety.IsMainThread)
            spentTicks += Stopwatch.GetTimestamp() - start;
    }

    /// <summary>Packs the glyphs finished on the thread pool, and starts the budget of the next frame.</summary>
    /// <remarks><ol>
    /// <li>Only glyphs asked for in this frame or the one before are packed, within a budget. The others are dropped.
    /// </li>
    /// <li>Synchronous scopes left open are ended.</li>
    /// </ol></remarks>
    public static void EndFrame()
    {
        var start = Stopwatch.GetTimestamp();
        while (Finished.TryPeek(out var f))
        {
            var key = (f.Request.Font, f.Request.Key);
            var wanted = Pending.Remove(key, out var asked) && frame - asked <= 1;
            if (wanted && Stopwatch.GetTimestamp() - start >= PackBudgetTicks)
            {
                Pending.Add(key, asked);
                break;
            }

            _ = Finished.TryDequeue(out _);
            if (wanted)
                f.Request.Font.AddRasterized(f.Request, f.Bitmap);
        }

        spentTicks = 0;
        frame++;
        if (synchronousDepth > 0)
        {
            Log.Warning(
                "{Count} scopes of complete glyphs were not ended in the frame, and have been",
                synchronousDepth);
            synchronousDepth = 0;
        }
    }
}
