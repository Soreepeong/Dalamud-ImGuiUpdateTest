using System.Diagnostics;

using Dalamud.Logging.Internal;
using Dalamud.Utility;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>Time spent by the font engine, per frame, for finding out what costs frame time.</summary>
/// <remarks>Only the main thread is measured. Sections may nest, such as rasterizing within drawing text. Each counts
/// its own time in full, and nested calls of the same section count once. A section that throws before its end is
/// not counted for the rest of the frame.</remarks>
internal static class FontProfiler
{
    /// <summary>The number of frames kept.</summary>
    public const int FrameCount = 300;

    private const int SectionCount = (int)Section.Count;

    // Rasterizing for longer than this in a frame is logged.
    private static readonly ModuleLog Log = new(typeof(FontProfiler));
    private static readonly long SlowRasterizeTicks = Stopwatch.Frequency * 4 / 1000;

    private static readonly Stat[] Frames = new Stat[FrameCount * SectionCount];
    private static readonly long[] FrameTicks = new long[FrameCount];
    private static readonly Stat[] Current = new Stat[SectionCount];
    private static readonly int[] Depths = new int[SectionCount];
    private static long frameStart = Stopwatch.GetTimestamp();
    private static int next;
    private static int filled;

    /// <summary>What is measured.</summary>
    public enum Section
    {
        /// <summary>Drawing layouts into draw lists, including rasterizing the glyphs they need.</summary>
        Render,

        /// <summary>Making layouts that were not cached, of plain text or SeStrings.</summary>
        Layout,

        /// <summary>Within <see cref="Layout"/>, decoding text and finding graphemes and line break opportunities.
        /// </summary>
        LayoutAnalyze,

        /// <summary>Within <see cref="Layout"/>, shaping, including choosing the fonts.</summary>
        LayoutShape,

        /// <summary>Within <see cref="LayoutShape"/>, the shaping by the fonts themselves.</summary>
        LayoutShapeByFonts,

        /// <summary>Within <see cref="Layout"/>, breaking lines and placing items.</summary>
        LayoutFinish,

        /// <summary>Rasterizing glyphs that were not in the atlas, on the main thread.</summary>
        Rasterize,

        /// <summary>Glyphs sent to the thread pool to be rasterized, as the frame was out of budget. No time.</summary>
        Deferred,

        /// <summary>Emboldening and packing rasterized glyphs into the atlas.</summary>
        Pack,

        /// <summary>Uploading the changed parts of the atlas to the GPU.</summary>
        Upload,

        /// <summary>The number of sections.</summary>
        Count,
    }

    /// <summary>Gets or sets a value indicating whether threads other than the main thread are measured too.</summary>
    /// <remarks>For benchmarks. One thread at a time.</remarks>
    internal static bool MeasureAnyThread { get; set; }

    private static bool IsMeasured => MeasureAnyThread || ThreadSafety.IsMainThread;

    /// <summary>Starts a section.</summary>
    /// <param name="section">The section.</param>
    /// <returns>The time to pass to <see cref="End"/>, or 0 if the section is already open.</returns>
    public static long Begin(Section section) =>
        IsMeasured && Depths[(int)section]++ == 0 ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Ends a section.</summary>
    /// <param name="section">The section.</param>
    /// <param name="start">What <see cref="Begin"/> returned.</param>
    /// <param name="bytes">Bytes processed, if any.</param>
    /// <param name="calls">The number of calls it counts as, such as glyphs rasterized together.</param>
    public static void End(Section section, long start, long bytes = 0, long calls = 1)
    {
        if (!IsMeasured)
            return;
        Depths[(int)section]--;
        if (start == 0)
            return;
        ref var s = ref Current[(int)section];
        s.Ticks += Stopwatch.GetTimestamp() - start;
        s.Calls += calls;
        s.Bytes += bytes;
    }

    /// <summary>Counts calls of a section that takes no time of its own.</summary>
    /// <param name="section">The section.</param>
    /// <param name="calls">The number of calls.</param>
    public static void Count(Section section, long calls = 1)
    {
        if (IsMeasured)
            Current[(int)section].Calls += calls;
    }

    /// <summary>Ends the frame, as it has been rendered.</summary>
    public static void EndFrame()
    {
        var now = Stopwatch.GetTimestamp();
        FrameTicks[next] = now - frameStart;
        frameStart = now;

        var raster = Current[(int)Section.Rasterize];
        if (raster.Ticks >= SlowRasterizeTicks)
        {
            Log.Debug(
                "Rasterizing {Count} glyphs took {Ms:0.00}ms in one frame",
                raster.Calls,
                ToMs(raster.Ticks));
        }

        Current.CopyTo(Frames.AsSpan(next * SectionCount, SectionCount));
        Array.Clear(Current);

        // Every section has ended by now, unless one threw before its end.
        Array.Clear(Depths);
        next = (next + 1) % FrameCount;
        filled = Math.Min(filled + 1, FrameCount);
    }

    /// <summary>Gets a section of the frame most recently ended.</summary>
    /// <param name="section">The section.</param>
    /// <returns>The time and calls.</returns>
    public static Stat Last(Section section) =>
        filled == 0 ? default : Frames[(((next + FrameCount - 1) % FrameCount) * SectionCount) + (int)section];

    /// <summary>Gets the average and the worst of the frames kept.</summary>
    /// <param name="section">The section.</param>
    /// <returns>The average per frame, the frame with the most time in the section, and how long that frame took.
    /// </returns>
    public static (Stat Average, Stat Worst, long WorstFrameTicks) Summarize(Section section)
    {
        var sum = default(Stat);
        var worst = default(Stat);
        var worstFrame = 0L;
        for (var i = 0; i < filled; i++)
        {
            var s = Frames[(i * SectionCount) + (int)section];
            sum.Ticks += s.Ticks;
            sum.Calls += s.Calls;
            sum.Bytes += s.Bytes;
            if (s.Ticks > worst.Ticks)
                (worst, worstFrame) = (s, FrameTicks[i]);
        }

        if (filled > 0)
            sum = new() { Ticks = sum.Ticks / filled, Calls = sum.Calls / filled, Bytes = sum.Bytes / filled };
        return (sum, worst, worstFrame);
    }

    /// <summary>Gets the average frame time of the frames kept.</summary>
    /// <returns>The time in <see cref="Stopwatch"/> ticks.</returns>
    public static long AverageFrameTicks()
    {
        var sum = 0L;
        for (var i = 0; i < filled; i++)
            sum += FrameTicks[i];
        return filled == 0 ? 0 : sum / filled;
    }

    /// <summary>Converts ticks to milliseconds.</summary>
    /// <param name="ticks">The <see cref="Stopwatch"/> ticks.</param>
    /// <returns>The milliseconds.</returns>
    public static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Time and calls of a section in a frame.</summary>
    public struct Stat
    {
        /// <summary>The time, in <see cref="Stopwatch"/> ticks.</summary>
        public long Ticks;

        /// <summary>The number of calls, such as glyphs rasterized.</summary>
        public long Calls;

        /// <summary>Bytes processed.</summary>
        public long Bytes;
    }
}
