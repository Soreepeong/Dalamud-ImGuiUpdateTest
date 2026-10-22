using System;
using System.Diagnostics;
using System.Linq;
using System.Text;

using Dalamud.Interface.Fonts.Internal;
using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.Layout;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Utility.Unicode;

using Xunit;

namespace Dalamud.Test.Interface.Fonts;

/// <summary>Where the time of shaping goes, for runs of one script. Set DALAMUD_TEST_BENCH to run.</summary>
[Collection("ImGui")]
public class ShapingBenchmarks(ITestOutputHelper output)
{
    private const int RunLength = 64;
    private const int Runs = 200;
    private const float Size = 18f;

    private static readonly bool Enabled = Environment.GetEnvironmentVariable("DALAMUD_TEST_BENCH") is not null;

    [Theory]
    [InlineData("Han", 0x4E00, 0x9FFF)]
    [InlineData("Hangul", 0xAC00, 0xD7A3)]
    public void ShapingRunsOfOneScript(string name, int first, int last)
    {
        Assert.SkipUnless(Enabled, "DALAMUD_TEST_BENCH is not set");
        var fallback = new SystemFallbackFont("en-US");
        var random = new Random(1);
        var runs = Enumerable.Range(0, Runs)
                             .Select(_ => Enumerable.Range(0, RunLength).Select(_ => (uint)random.Next(first, last + 1)).ToArray())
                             .ToArray();
        var texts = runs.Select(r => Encoding.UTF8.GetBytes(string.Concat(r.Select(c => char.ConvertFromUtf32((int)c))))).ToArray();
        var list = new ShapedGlyphList();
        var glyphs = new uint[RunLength];
        var metrics = new GlyphMetrics[RunLength];

        // Warmed up, as the fallback maps each codepoint to an installed font once.
        foreach (var run in runs)
        {
            list.Clear();
            fallback.Shape(new(run, 0, run.Length, Size, 1), list);
        }

        fallback.GetGlyphIndices(runs[0], glyphs);
        var face = fallback.GetFace(glyphs[0]);
        output.WriteLine($"{name}: {face}");

        void Measure(string what, Action<int> action)
        {
            for (var i = 0; i < Runs; i++)
                action(i);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < Runs; i++)
                action(i);
            output.WriteLine($"{what,-40} {sw.Elapsed.TotalMicroseconds / (Runs * RunLength),8:0.000} us per codepoint");
        }

        Measure("TextLayout, whole", i => _ = new TextLayout(fallback, new(Size), default, texts[i], 1, [], []));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Runs; i++)
            _ = new TextLayout(fallback, new(Size), default, texts[i], 1, [], []);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"{"TextLayout, allocated",-40} {allocated / Runs,8} bytes per layout");
        var shortText = "Hello"u8.ToArray();
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Runs; i++)
            _ = new TextLayout(fallback, new(Size), default, shortText, 1, [], []);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"{"TextLayout of \"Hello\", allocated",-40} {allocated / Runs,8} bytes per layout");

        // From the pool, as the cache makes them.
        foreach (var (what, text) in (ReadOnlySpan<(string, byte[])>)[("64 codepoints", texts[0]), ("\"Hello\"", shortText)])
        {
            TextLayout.Rent(fallback, new(Size), default, text, 1, [], []).Recycle();
            allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Runs; i++)
                TextLayout.Rent(fallback, new(Size), default, text, 1, [], []).Recycle();
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            output.WriteLine($"{"Pooled TextLayout of " + what + ", allocated",-40} {allocated / Runs,8} bytes per layout");
        }

        // The phases, as the profiler has them.
        FontProfiler.MeasureAnyThread = true;
        try
        {
            FontProfiler.EndFrame();
            for (var i = 0; i < Runs; i++)
                _ = new TextLayout(fallback, new(Size), default, texts[i], 1, [], []);
            FontProfiler.EndFrame();
            foreach (var section in (FontProfiler.Section[])
                     [
                         FontProfiler.Section.LayoutAnalyze, FontProfiler.Section.LayoutShape,
                         FontProfiler.Section.LayoutShapeByFonts, FontProfiler.Section.LayoutFinish,
                     ])
            {
                var us = FontProfiler.ToMs(FontProfiler.Last(section).Ticks) * 1000 / (Runs * RunLength);
                output.WriteLine($"{"- " + section,-40} {us,8:0.000} us per codepoint");
            }
        }
        finally
        {
            FontProfiler.MeasureAnyThread = false;
        }

        Measure("UnicodeGraphemeClusterEnumerator", i =>
        {
            foreach (var b in new UnicodeGraphemeClusterEnumerator(texts[i]))
                _ = b;
        });
        Measure("UnicodeLineBreakEnumerator", i =>
        {
            foreach (var b in new UnicodeLineBreakEnumerator(texts[i]))
                _ = b;
        });
        Measure("SystemFallbackFont.Shape", i =>
        {
            list.Clear();
            fallback.Shape(new(runs[i], 0, RunLength, Size, 1), list);
        });
        Measure("DirectWriteFont.Shape of the face", i =>
        {
            list.Clear();
            face.Shape(new(runs[i], 0, RunLength, Size, 1), list);
        });
        Measure("SystemFallbackFont.GetGlyphIndices", i => fallback.GetGlyphIndices(runs[i], glyphs));
        Measure("DirectWriteFont.GetGlyphIndices", i => face.GetGlyphIndices(runs[i], glyphs));
        Measure("DirectWriteFont.GetGlyphMetrics", i =>
        {
            face.GetGlyphIndices(runs[i], glyphs);
            face.GetGlyphMetrics(glyphs, Size, 1, metrics);
        });
    }
}
