using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.Layout;
using Dalamud.Interface.Fonts.Internal.Sources;

using Xunit;

namespace Dalamud.Test.Interface.Fonts;

/// <summary>Where the time of drawing a cached layout goes. Set DALAMUD_TEST_BENCH to run.</summary>
[Collection("ImGui")]
public unsafe class RenderBenchmarks(ITestOutputHelper output)
{
    private const int WarmUpFrames = 10;
    private const int WarmUpMs = 1000;
    private const int ProfileSeconds = 20;
    private const int Frames = 20;
    private const int RendersPerFrame = 10;
    private const float Size = 16f;

    private static readonly bool Enabled = Environment.GetEnvironmentVariable("DALAMUD_TEST_BENCH") is not null;
    private static readonly string FontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    [Theory]
    [InlineData("direct")]
    [InlineData("composite")]
    [InlineData("snapped")]
    public void DrawingCachedLayouts(string kind)
    {
        Assert.SkipUnless(Enabled, "DALAMUD_TEST_BENCH is not set");
        using var imgui = new FontEngineTests.ImGuiScope();
        var font = CreateFont(kind);
        imgui.SetDefaultFont(font, Size);
        ImGui.GetIO().BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        // About a window of text.
        var text = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog, 0123456789. ", 30));
        var bytes = Encoding.UTF8.GetBytes(text);
        var codepoints = text.Select(ch => (uint)ch).ToArray();
        var glyphIds = new uint[codepoints.Length];
        font.GetGlyphIndices(codepoints, glyphIds);
        // Got every frame, as the cache drops layouts that go unused for a few frames.
        TextLayout Layout() => TextLayouter.Create(font, new(Size, WrapWidth: 700), default, bytes);
        var quads = 0;

        // Rasterized before measuring. The foreground draw list clips to the whole display, so nothing is culled.
        for (var i = 0; i < 3; i++)
        {
            imgui.Frame(() =>
            {
                var dl = ImGui.GetForegroundDrawList();
                var before = dl.VtxBuffer.Size;
                Layout().Render(dl, new(10, 10), uint.MaxValue, flags: ImTextRenderFlags.CompleteGlyphs);
                quads = (dl.VtxBuffer.Size - before) / 4;
            });
        }

        output.WriteLine($"{kind}: {quads} quads per render");

        // For a profiler to attach to, the font named only draws for a while, and the other does nothing.
        if (Environment.GetEnvironmentVariable("DALAMUD_TEST_BENCH_PROFILE") is { } profiled)
        {
            if (profiled != kind)
                return;
            var end = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * ProfileSeconds);
            while (Stopwatch.GetTimestamp() < end)
            {
                imgui.Frame(() =>
                {
                    var dl = ImGui.GetForegroundDrawList();
                    for (var i = 0; i < RendersPerFrame; i++)
                        Layout().Render(dl, new(10, 10), uint.MaxValue);
                });
            }

            return;
        }

        void Measure(string what, Action<ImDrawListPtr> action)
        {
            // The first frames are not counted, for the JIT to have optimized what they run. Tiered compilation starts
            // a while after the first calls, so they last a while too.
            var ticks = 0L;
            var warmUpEnd = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * WarmUpMs / 1000);
            for (var f = -WarmUpFrames; f < Frames; f++)
            {
                if (f == -1 && Stopwatch.GetTimestamp() < warmUpEnd)
                    f = -2;
                var counted = f >= 0;
                imgui.Frame(() =>
                {
                    var dl = ImGui.GetForegroundDrawList();
                    var start = Stopwatch.GetTimestamp();
                    for (var i = 0; i < RendersPerFrame; i++)
                        action(dl);
                    if (counted)
                        ticks += Stopwatch.GetTimestamp() - start;
                });
            }

            var ns = ticks * 1e9 / Stopwatch.Frequency / (Frames * RendersPerFrame * quads);
            output.WriteLine($"{what,-48} {ns,8:0.0} ns per quad");
        }

        Measure("TextLayouter.Create (cached) and Render", dl => Layout().Render(dl, new(10, 10), uint.MaxValue));
        Measure("TextLayouter.Create (cached)", _ => Layout());

        // The renderer copies every vertex to the GPU each frame, which is not in the font profiler's sections.
        var copyTarget = new byte[quads * 4 * sizeof(ImDrawVert)];
        var copyTicks = 0L;
        for (var f = 0; f < Frames; f++)
        {
            imgui.Frame(() =>
            {
                var dl = ImGui.GetForegroundDrawList();
                Layout().Render(dl, new(10, 10), uint.MaxValue);
                var bytes = new ReadOnlySpan<byte>(dl.VtxBuffer.Data, dl.VtxBuffer.Size * sizeof(ImDrawVert));
                var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < RendersPerFrame; i++)
                    bytes.CopyTo(copyTarget);
                copyTicks += Stopwatch.GetTimestamp() - start;
            });
        }

        output.WriteLine(
            $"{$"Copying the vertices ({sizeof(ImDrawVert)} bytes each)",-48} " +
            $"{copyTicks * 1e9 / Stopwatch.Frequency / (Frames * RendersPerFrame * quads),8:0.0} ns per quad");

        var images = new GlyphImage[glyphIds.Length];
        var steps = new byte[glyphIds.Length];
        Measure("GetGlyphImages, a glyph per call", _ =>
        {
            Span<uint> g = stackalloc uint[1];
            Span<GlyphImage> image = stackalloc GlyphImage[1];
            for (var i = 0; i < quads; i++)
            {
                g[0] = glyphIds[i % glyphIds.Length];
                font.GetGlyphImages(g, steps.AsSpan(0, 1), Size, 1, 0, false, image);
            }
        });
        Measure("GetGlyphImages, all in one call", _ =>
        {
            var n = Math.Min(quads, glyphIds.Length);
            font.GetGlyphImages(glyphIds.AsSpan(0, n), steps.AsSpan(0, n), Size, 1, 0, false, images);
        });

        Measure("PrimReserve and PrimQuadUV", dl =>
        {
            for (var i = 0; i < quads; i++)
            {
                ImGuiNative.PrimReserve(dl, 6, 4);
                ImGuiNative.PrimQuadUV(
                    dl, new(0, 0), new(1, 0), new(1, 1), new(0, 1), Vector2.Zero, Vector2.UnitX, Vector2.One,
                    Vector2.UnitY, uint.MaxValue);
            }
        });
        Measure("PrimReserve, PrimQuadUV and Ext through Span", dl =>
        {
            for (var i = 0; i < quads; i++)
            {
                ImGuiNative.PrimReserve(dl, 6, 4);
                ImGuiNative.PrimQuadUV(
                    dl, new(0, 0), new(1, 0), new(1, 1), new(0, 1), Vector2.Zero, Vector2.UnitX, Vector2.One,
                    Vector2.UnitY, uint.MaxValue);
                foreach (ref var v in new Span<ImDrawVert>(dl.Handle->VtxWritePtr - 4, 4))
                {
                    v.Ext[0] = 1;
                    v.Ext[1] = 2;
                    v.Ext[2] = 3;
                    v.Ext[3] = 4;
                    v.Ext[4] = 5;
                }
            }
        });
        Measure("PrimReserve, PrimQuadUV and Ext through fields", dl =>
        {
            for (var i = 0; i < quads; i++)
            {
                ImGuiNative.PrimReserve(dl, 6, 4);
                ImGuiNative.PrimQuadUV(
                    dl, new(0, 0), new(1, 0), new(1, 1), new(0, 1), Vector2.Zero, Vector2.UnitX, Vector2.One,
                    Vector2.UnitY, uint.MaxValue);
                foreach (ref var v in new Span<ImDrawVert>(dl.Handle->VtxWritePtr - 4, 4))
                {
                    v.Ext_0 = 1;
                    v.Ext_1 = 2;
                    v.Ext_2 = 3;
                    v.Ext_3 = 4;
                    v.Ext_4 = 5;
                }
            }
        });
        Measure("Writing quads directly into reserved buffers", dl =>
        {
            ImGuiNative.PrimReserve(dl, quads * 6, quads * 4);
            var vtx = dl.Handle->VtxWritePtr;
            var idx = dl.Handle->IdxWritePtr;
            var baseIdx = dl.Handle->VtxCurrentIdx;
            for (var i = 0; i < quads; i++)
            {
                var b = (ushort)(baseIdx + (i * 4));
                idx[0] = b;
                idx[1] = (ushort)(b + 1);
                idx[2] = (ushort)(b + 2);
                idx[3] = b;
                idx[4] = (ushort)(b + 2);
                idx[5] = (ushort)(b + 3);
                for (var k = 0; k < 4; k++)
                {
                    vtx[k] = default;
                    vtx[k].Col = uint.MaxValue;
                    vtx[k].Ext_0 = 1;
                    vtx[k].Ext_1 = 2;
                    vtx[k].Ext_2 = 3;
                    vtx[k].Ext_3 = 4;
                    vtx[k].Ext_4 = 5;
                }

                vtx += 4;
                idx += 6;
            }

            dl.Handle->VtxWritePtr = vtx;
            dl.Handle->IdxWritePtr = idx;
            dl.Handle->VtxCurrentIdx += (uint)(quads * 4);
        });
    }

    /// <summary>Drawing many short labels, as a window of widgets does. What is timed is
    /// <see cref="TextLayout.Render"/> alone, as the font profiler's "Drawing text" has it.</summary>
    /// <param name="kind">The font, as <see cref="CreateFont"/> takes it.</param>
    [Theory]
    [InlineData("direct")]
    [InlineData("composite")]
    [InlineData("snapped")]
    public void DrawingShortLabels(string kind)
    {
        const int labelCount = 200;
        Assert.SkipUnless(Enabled, "DALAMUD_TEST_BENCH is not set");
        using var imgui = new FontEngineTests.ImGuiScope();
        var font = CreateFont(kind);
        imgui.SetDefaultFont(font, Size);
        ImGui.GetIO().BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        // Words of widget labels, 6 to 20 characters long.
        var random = new Random(1);
        char Letter(int i) => i % 7 == 6 ? ' ' : (char)random.Next('a', 'z' + 1);
        var labels = Enumerable.Range(0, labelCount)
                               .Select(_ => Encoding.UTF8.GetBytes(
                                           new string([.. Enumerable.Range(0, random.Next(6, 21)).Select(Letter)])))
                               .ToArray();
        byte[][] single = [.. Enumerable.Repeat("A"u8.ToArray(), labelCount)];
        var layouts = new TextLayout[labelCount];
        var quads = 0;

        void Measure(string what, byte[][] texts, bool render)
        {
            // Untimed first, for the glyphs to be rasterized and the JIT to have optimized what it runs.
            var ticks = 0L;
            var allocated = 0L;
            var warmUpEnd = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * WarmUpMs / 1000);
            for (var f = -WarmUpFrames; f < Frames; f++)
            {
                if (f == -1 && Stopwatch.GetTimestamp() < warmUpEnd)
                    f = -2;
                var counted = f >= 0;
                imgui.Frame(() =>
                {
                    var dl = ImGui.GetForegroundDrawList();
                    var start = Stopwatch.GetTimestamp();
                    for (var i = 0; i < labelCount; i++)
                        layouts[i] = TextLayouter.Create(font, new(Size), default, texts[i]);
                    if (render)
                    {
                        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                        start = Stopwatch.GetTimestamp();
                        var before = dl.VtxBuffer.Size;
                        for (var i = 0; i < labelCount; i++)
                        {
                            var flags = counted ? ImTextRenderFlags.None : ImTextRenderFlags.CompleteGlyphs;
                            layouts[i].Render(dl, new((i % 4) * 200, (i / 4) * 11), uint.MaxValue, flags: flags);
                        }

                        quads = (dl.VtxBuffer.Size - before) / 4;
                        if (counted)
                            allocated += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                    }

                    if (counted)
                        ticks += Stopwatch.GetTimestamp() - start;
                });
            }

            var ns = ticks * 1e9 / Stopwatch.Frequency / Frames;
            output.WriteLine(
                $"{what,-48} {ns / labelCount,8:0.0} ns per call" +
                (render
                     ? $", {ns / quads,6:0.0} ns per quad, {quads / labelCount,2} quads per call, " +
                       $"{allocated / Frames / labelCount} bytes allocated per call"
                     : string.Empty));
        }

        output.WriteLine(kind);
        Measure("Render, labels of 6 to 20 characters", labels, true);
        Measure("Render, labels of 1 character", single, true);
        Measure("TextLayouter.Create (cached)", labels, false);
    }

    // "direct" for Segoe UI, or "composite" for Segoe UI for ASCII and Malgun Gothic for the rest. "snapped" is the
    // composite with advances snapped to pixels, as FontFactory makes elements.
    private static ManagedFont CreateFont(string kind)
    {
        var segoe = new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0);
        if (kind == "direct")
            return segoe;
        var snapped = kind == "snapped";
        var malgun = new DirectWriteFont("Malgun Gothic", File.ReadAllBytes(Path.Combine(FontsDir, "malgun.ttf")), 0);
        var font = new CompositeFont("bench", Size);
        font.Add(new(segoe, CompositeFont.ToRanges([0x20, 0x7E, 0]), PixelSnapH: snapped));
        font.Add(new(malgun, PixelSnapH: snapped));
        return font;
    }
}
