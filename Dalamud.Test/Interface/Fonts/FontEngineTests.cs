using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts;
using Dalamud.Interface.Fonts.Internal;
using Dalamud.Interface.Fonts.Internal.Formats;
using Dalamud.Interface.Fonts.Internal.Glyphs;
using Dalamud.Interface.Fonts.Internal.Layout;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Utility;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using Xunit;

namespace Dalamud.Test.Interface.Fonts;

/// <summary>Managed fonts drawn by ImGui through the text callbacks and Dalamud's layouter, without a renderer.
/// ImGui is driven for a few frames and the draw data and glyph pages are inspected.</summary>
[Collection("ImGui")]
public unsafe class FontEngineTests
{
    private static readonly string FontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    /// <summary>Directory with AXIS_*.fdt and font*.tex from the game (tools/imgui_font_scan/extract_gamefont.py).
    /// The game font test is skipped without it.</summary>
    private static readonly string? GameFontDir = Environment.GetEnvironmentVariable("DALAMUD_TEST_GAMEFONT");

    /// <summary>Where to write glyph page dumps (BMP), if set.</summary>
    private static readonly string? DumpDir = Environment.GetEnvironmentVariable("DALAMUD_TEST_FONT_DUMP");

    [Fact]
    public void InputTakesCodepointsPastBmp()
    {
        using var imgui = new ImGuiScope();
        var io = ImGui.GetIO();

        // WM_CHAR sends an emoji as two surrogates. The frame moves input events to the queue.
        ImGuiNative.AddInputCharacterUTF16(io.Handle, 0xD83D);
        ImGuiNative.AddInputCharacterUTF16(io.Handle, 0xDE00);
        var queued = Array.Empty<uint>();
        imgui.Frame(() => queued = io.InputQueueCharacters.ToArray());
        Assert.Equal([0x1F600u], queued);
    }

    [Fact]
    public void ColorGlyphsAreInTheColorPlane()
    {
        var path = Path.Combine(FontsDir, "seguiemj.ttf");
        Assert.SkipWhen(!File.Exists(path), "Segoe UI Emoji is not installed");
        using var imgui = new ImGuiScope();
        using var colorGlyphs = new ColorGlyphRasterizer(CreateWicFactory);
        var emoji = new DirectWriteFont("Segoe UI Emoji", File.ReadAllBytes(path), 0, colorGlyphs);

        // A color glyph and a plain one of the same font.
        Span<uint> glyphs = stackalloc uint[2];
        emoji.GetGlyphIndices([0x1F600, '#'], glyphs);
        Assert.NotEqual(0u, glyphs[0]);
        Assert.NotEqual(0u, glyphs[1]);
        Span<GlyphImage> images = stackalloc GlyphImage[2];
        emoji.GetGlyphImages(glyphs, stackalloc byte[2], 32, 1, 0, false, images);

        Assert.Equal(GlyphKind.Color, images[0].Kind);
        Assert.Equal(GlyphKind.Grayscale, images[1].Kind);
        Assert.Equal(1, GlyphAtlas.Shared.Color.SliceCount);
        Assert.Equal(1, GlyphAtlas.Shared.Coverage.SliceCount);

        // The smiley has colors of its own.
        Gpu.Flush();
        var pixels = GlyphAtlas.Shared.Color.Read(images[0].Slice);
        var x0 = (int)(images[0].TexelMin & 0xFFFF);
        var y0 = (int)(images[0].TexelMin >> 16);
        var x1 = (int)(images[0].TexelMax & 0xFFFF);
        var y1 = (int)(images[0].TexelMax >> 16);
        var colored = false;
        for (var y = y0; y < y1 && !colored; y++)
        {
            for (var x = x0; x < x1 && !colored; x++)
            {
                var i = ((y * GlyphAtlas.SliceSize) + x) * 4;
                colored = pixels[i + 3] > 128 && pixels[i] != pixels[i + 2];
            }
        }

        Assert.True(colored);
        imgui.DumpPage("color");
    }

    [Fact]
    public void ColorGlyphsKeepTheirGradients()
    {
        var path = Path.Combine(FontsDir, "seguiemj.ttf");
        Assert.SkipWhen(!File.Exists(path), "Segoe UI Emoji is not installed");
        var data = File.ReadAllBytes(path);
        Assert.SkipWhen(
            ColrVersion(data) < 1 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
            "Segoe UI Emoji has no gradients here, or they cannot be drawn");
        using var imgui = new ImGuiScope();
        using var colorGlyphs = new ColorGlyphRasterizer(CreateWicFactory);
        var emoji = new DirectWriteFont("Segoe UI Emoji", data, 0, colorGlyphs);

        Span<uint> glyph = stackalloc uint[1];
        emoji.GetGlyphIndices([0x1F600], glyph);
        Span<GlyphImage> image = stackalloc GlyphImage[1];
        emoji.GetGlyphImages(glyph, stackalloc byte[1], 64, 1, 0, false, image);

        // Solid layers give a few opaque colors, about 50. Gradients give many, about 1600.
        Gpu.Flush();
        var pixels = GlyphAtlas.Shared.Color.Read(image[0].Slice);
        var colors = new HashSet<uint>();
        for (var y = (int)(image[0].TexelMin >> 16); y < (int)(image[0].TexelMax >> 16); y++)
        {
            for (var x = (int)(image[0].TexelMin & 0xFFFF); x < (int)(image[0].TexelMax & 0xFFFF); x++)
            {
                var i = ((y * GlyphAtlas.SliceSize) + x) * 4;
                if (pixels[i + 3] == 255)
                    colors.Add(BitConverter.ToUInt32(pixels, i));
            }
        }

        Assert.True(colors.Count > 400, $"{colors.Count} colors");

        // The version of the COLR table, or -1 if there is none.
        static int ColrVersion(byte[] font)
        {
            var tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
            for (var t = 0; t < tables; t++)
            {
                var record = font.AsSpan(12 + (t * 16));
                if (record[..4].SequenceEqual("COLR"u8))
                    return BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(BinaryPrimitives.ReadInt32BigEndian(record[8..])));
            }

            return -1;
        }
    }

    [Fact]
    public void LayoutsKeepGlyphImagesWhileTheirSlicesAreCurrent()
    {
        using var imgui = new ImGuiScope();
        var font = new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0);
        imgui.SetDefaultFont(font, 16f);
        // At a density of 1.25, a pixel to the right puts the glyphs another quarter of a device pixel right.
        var layout = TextLayout.Rent(font, new(16f, 1.25f), default, "Hello"u8, 1, [], []);
        var atlas = GlyphAtlas.Shared;

        // The number of glyphs whose images were got, rather than kept, for drawing the layout once. ImGui draws text
        // of its own in a frame too.
        long Lookups(Vector2 pos)
        {
            var quads = 0;
            imgui.Frame(() =>
            {
                var dl = ImGui.GetForegroundDrawList();
                var before = dl.VtxBuffer.Size;
                FontProfiler.EndFrame();
                layout.Render(dl, pos, uint.MaxValue, flags: ImTextRenderFlags.CompleteGlyphs);
                FontProfiler.EndFrame();
                quads = (dl.VtxBuffer.Size - before) / 4;
            });
            Assert.Equal(5, quads);
            return FontProfiler.Last(FontProfiler.Section.RenderImages).Calls;
        }

        FontProfiler.MeasureAnyThread = true;
        try
        {
            // Kept for the same subpixel steps, and got again for others.
            long[] lookups = [Lookups(new(10, 10)), Lookups(new(10, 10)), Lookups(new(11, 10)), Lookups(new(11, 10))];
            Assert.Equal([5, 0, 5, 0], lookups);

            // Got again once their slice is retired, as other glyphs fill the atlas while the layout is not drawn.
            var big = new GlyphBitmap { Width = 2000, Height = 2000, Alpha = new byte[2000 * 2000] };
            atlas.Coverage.MaxSlices = 1;
            atlas.EndFrame();
            while (atlas.Coverage.EvictionCount == 0)
            {
                _ = atlas.Add(big);
                atlas.EndFrame();
            }

            Assert.Equal(5, Lookups(new(11, 10)));
            Assert.Equal(0, Lookups(new(11, 10)));
        }
        finally
        {
            FontProfiler.MeasureAnyThread = false;
            layout.Recycle();
        }
    }

    [Fact]
    public void CompositeImagesIgnoreSnappedAndExtraAdvances()
    {
        // Snapped and extra advances move the pens of later glyphs, not the images, so the images are got without
        // the metrics. An offset still moves them.
        using var imgui = new ImGuiScope();
        var segoe = new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0);
        var plain = new CompositeFont("plain", 16f);
        plain.Add(new(segoe, Offset: new(1, 2)));
        var adjusted = new CompositeFont("adjusted", 16f);
        adjusted.Add(new(segoe, Offset: new(1, 2), ExtraAdvanceX: 3, PixelSnapH: true));

        Span<uint> glyphs = stackalloc uint[3];
        plain.GetGlyphIndices(['A', 'g', 'W'], glyphs);
        ReadOnlySpan<byte> steps = [0, 1, 3];
        Span<GlyphImage> expected = stackalloc GlyphImage[3];
        Span<GlyphImage> actual = stackalloc GlyphImage[3];
        plain.GetGlyphImages(glyphs, steps, 16, 1.25f, 0, false, expected);
        adjusted.GetGlyphImages(glyphs, steps, 16, 1.25f, 0, false, actual);
        for (var i = 0; i < glyphs.Length; i++)
        {
            Assert.False(expected[i].IsEmpty);
            Assert.Equal(expected[i].Rect, actual[i].Rect);
            Assert.Equal(expected[i].TexelMin, actual[i].TexelMin);
        }
    }

    [Fact]
    public void DirectWriteCompositeLaysOutAndDraws()
    {
        using var imgui = new ImGuiScope();
        var segoe = new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0);
        var malgun = new DirectWriteFont("Malgun Gothic", File.ReadAllBytes(Path.Combine(FontsDir, "malgun.ttf")), 0);
        var font = new CompositeFont("test", 16f);
        font.Add(new(segoe, CompositeFont.ToRanges([0x20, 0x7E, 0])));
        font.Add(new(malgun));
        var fontPtr = imgui.SetDefaultFont(font, 16f);

        // Codepoints go to the first element that has them.
        Assert.Equal(0u, fontPtr.GetGlyphIndex('A') >> 24);
        Assert.Equal(1u, fontPtr.GetGlyphIndex('한') >> 24);
        Assert.NotEqual(0u, fontPtr.GetGlyphIndex('A'));
        Assert.Equal(0u, fontPtr.GetGlyphIndex(0xE000)); // a private use codepoint in neither font

        // ImGui measures through the layout callback, which lays out with the managed font.
        imgui.Frame(() =>
        {
            var size = ImGui.CalcTextSize("Hello, 한국어");
            Assert.True(size.X > 50, $"width {size.X}");
            Assert.Equal(16f, size.Y);

            ImGui.Begin("test");
            ImGui.Text("Hello, 한국어");
            ImGui.Button("Button");
            ImGui.End();
        });

        // Draw again. The first frame of a window measures it.
        imgui.Frame(() =>
        {
            ImGui.Begin("test");
            ImGui.Text("Hello, 한국어");
            ImGui.End();
        });

        var drawData = ImGui.GetDrawData();
        var vertices = 0;
        foreach (var list in new Span<ImDrawListPtr>(drawData.Handle->CmdLists.Data, drawData.Handle->CmdLists.Size))
            vertices += list.VtxBuffer.Size;
        Assert.True(vertices > 4 * 10, $"{vertices} vertices");
        Assert.True(GlyphAtlas.Shared.GlyphCount >= 10, $"{GlyphAtlas.Shared.GlyphCount} glyphs");
        Assert.Equal(1, GlyphAtlas.Shared.SliceCount);
        imgui.DumpPage("directwrite");
    }

    [Fact]
    public void FontStylesApplyToImGuiText()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            var font = ImGui.Font;
            var plain = ImGui.CalcTextSize("Hello");
            using (ImGui.PushBold())
            {
                Assert.True(ImGui.Font.Style.Bold);
                Assert.NotEqual(font, ImGui.Font);
                Assert.True(ImGui.CalcTextSize("Hello").X > plain.X);
            }

            Assert.Equal(font, ImGui.Font);
            Assert.Equal(plain, ImGui.CalcTextSize("Hello"));

            // A font per style, and the font itself for the default style.
            var style = new ImTextStyle { Italic = true, Edge = new(0xFF000000, 0.1f) };
            Assert.Equal(font.WithStyleForFrame(style), font.WithStyleForFrame(style));
            Assert.Equal(font, font.WithStyleForFrame(default));
            Assert.Equal(font, font.WithStyleForFrame(style).WithStyleForFrame(default));
        });
    }

    [Fact]
    public void StyledFontsUnusedForTwoFramesGetNewStyles()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        var made = new HashSet<nint>();
        var kept = new ImTextStyle { Italic = true };
        var keptFont = default(nint);

        // As with an animated color, a new style every time.
        void MakeStyles(uint first)
        {
            for (var i = first; i < first + 1000; i++)
                made.Add((nint)ImGui.Font.WithStyleForFrame(new ImTextStyle { Color = 0xFF000000 | i }).Handle);
        }

        imgui.Frame(() =>
        {
            keptFont = (nint)ImGui.Font.WithStyleForFrame(kept).Handle;
            MakeStyles(0);
        });
        imgui.Frame(() => { });
        imgui.Frame(() =>
        {
            Assert.Equal(keptFont, (nint)ImGui.Font.WithStyleForFrame(kept).Handle);
            MakeStyles(1000);
        });
        Assert.Equal(1000, made.Count);
        imgui.Frame(() => MakeStyles(2000));

        // The second batch reused the first, unused for two frames, and the third could not reuse the second.
        Assert.Equal(2000, made.Count);
        unsafe
        {
            Assert.Equal(kept, ((ImFont*)keptFont)->Style);
        }
    }

    [Fact]
    public void TextColorsTakeVectorsAsImGuiPacksThem()
    {
        using var imgui = new ImGuiScope();
        foreach (var v in new Vector4[] { new(1, 0, 0, 1), new(0.25f, 0.5f, 0.75f, 0.1f), new(-1, 2, 0.5f, 0) })
            Assert.Equal(ImGui.ColorConvertFloat4ToU32(v), (uint)(ImTextColor)v);

        var style = new ImTextStyle { Edge = new(new Vector4(0, 0, 0, 1)), Shadow = new(0x80000000) };
        Assert.Equal(0xFF000000u, (uint)style.Edge.Color);
        Assert.Equal(style, style with { Edge = new(0xFF000000) });
        Assert.True(style.Shadow.IsOn);
        Assert.Equal(new Vector4(0, 0, 0, 1), style.Edge.Color.Vector4);
    }

    [Fact]
    public void StylesAreEqualWhateverOrderTheyWereMadeIn()
    {
        // The color and the flags, the shadow, edge, background and lines, then the extra decoration at 0x30.
        Assert.Equal(0x40, sizeof(ImTextStyle));
        Assert.Equal(0x30, (int)Marshal.OffsetOf<ImTextStyle>("<ExtraDecoration>k__BackingField"));

        var wavy = new ImTextLineDecoration(ImTextLinePosition.Under, ImTextLineStyle.Wavy, 0xFF0000FF);
        var style = default(ImTextStyle);
        style.Lines = new(ImTextLinePosition.Under);
        style.ExtraDecoration = wavy;
        style.Edge = new(0xFF000000);
        style.Shadow = new(0xFF000000);
        Assert.Equal(new ImTextLineDecoration(ImTextLinePosition.Under), style.Lines);
        Assert.True(ImTextLineDecoration.TryRead(style.ExtraDecoration, out var extra));
        Assert.Equal(wavy, extra);

        // Equal styles share fonts and layouts.
        var other = new ImTextStyle
        {
            Shadow = new(0xFF000000),
            Edge = new(0xFF000000),
            ExtraDecoration = wavy,
            Lines = new(ImTextLinePosition.Under),
        };
        Assert.Equal(style, other);
        Assert.Equal(style, default(ImTextStyle).WithShadow(0xFF000000).WithEdge(0xFF000000).WithDecoration(wavy)
                                                .WithLines(ImTextLinePosition.Under));

        // Transparent, zero width or no offset is off.
        Assert.False(new ImTextEdge(0x00FF0000).IsOn);
        Assert.False(new ImTextShadow(0xFF000000, Vector2.Zero).IsOn);
        Assert.Equal(default, style.WithoutDecoration(ImTextDecorationId.Line).ExtraDecoration);
        Assert.False(style.WithoutDecoration(ImTextDecorationId.Line).Lines.IsOn);

        style.Bold = true;
        style.Markup = ImTextMarkup.MacroString;
        Assert.True(style.Bold);
        Assert.False(style.Italic);
        Assert.Equal(ImTextMarkup.MacroString, style.Markup);
    }

    [Fact]
    public void EdgesAreStoredInStepsAndLimited()
    {
        // The color and a byte each for the width, the softness and the limits.
        Assert.Equal(8, sizeof(ImTextEdge));

        // The default width and softnesses the shader takes are exact.
        var edge = new ImTextEdge(0xFF000000, ImTextEdge.DefaultWidth, 1.5f);
        Assert.Equal(ImTextEdge.DefaultWidth, edge.Width);
        Assert.Equal(1.5f, edge.Softness);
        Assert.Equal(96, edge.PackedSoftness);

        // Values are rounded to their steps and limited to their ranges.
        Assert.Equal(new ImTextEdge(0xFF000000, 0.2f), new ImTextEdge(0xFF000000, 51 / 256f));
        Assert.Equal(255 / 16f, new ImTextEdge(0xFF000000, minWidth: 100).MinWidth);
        Assert.Equal(0, new ImTextEdge(0xFF000000, -1).Width);

        // The width in pixels is limited, the most last.
        Assert.Equal(1f, new ImTextEdge(0xFF000000, ImTextEdge.DefaultWidth).WidthAt(16));
        Assert.Equal(2f, new ImTextEdge(0xFF000000, ImTextEdge.DefaultWidth, minWidth: 2).WidthAt(16));
        Assert.Equal(1.5f, new ImTextEdge(0xFF000000, 0.5f, maxWidth: 1.5f).WidthAt(16));
        Assert.Equal(1.5f, new ImTextEdge(0xFF000000, 0, minWidth: 2, maxWidth: 1.5f).WidthAt(16));

        // A width in pixels alone is an edge.
        Assert.True(new ImTextEdge(0xFF000000, 0, minWidth: 1).IsOn);
        Assert.False(new ImTextEdge(0xFF000000, 0).IsOn);
    }

    [Fact]
    public void EffectsThatAreOffAreStoredAsDefaults()
    {
        // Leftover values of effects that are off would make styles that look the same differ.
        var off = new ImTextStyle
        {
            Shadow = new(0x00FF0000, new(1, 1)),
            Edge = new(0xFF000000, 0),
            Background = 0x00FFFFFF,
            Lines = new(ImTextLinePosition.None, ImTextLineStyle.Wavy, 0xFF0000FF, 2),
            ExtraDecoration = new(ImTextDecorationId.None, 0xFF000000, 5),
        };
        Assert.Equal(default, off);
        Assert.Equal(
            new ImTextStyle { Lines = new(ImTextLinePosition.Under, Count: 1) },
            new ImTextStyle { Lines = new(ImTextLinePosition.Under, Count: 0) });
    }

    [Fact]
    public void PushedDecorationsReplaceTheirKindAndKeepWhatIsNotGiven()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            static ImTextLineDecoration Lines() => ImGui.TextStyle.Lines;

            using (ImGui.PushUnderline(0xFF0000FF))
            {
                Assert.Equal(new ImTextLineDecoration(ImTextLinePosition.Under, Color: 0xFF0000FF), Lines());
                using (ImGui.PushUnderline())
                    Assert.Equal(0xFF0000FFu, (uint)Lines().Color);

                // More positions share the style of the lines.
                using (ImGui.PushStrikethrough())
                {
                    Assert.Equal(ImTextLinePosition.Under | ImTextLinePosition.Through, Lines().Positions);
                    Assert.Equal(0xFF0000FFu, (uint)Lines().Color);
                }

                // After removing the lines, new ones are in the color of the text.
                using (ImGui.PushLines(ImTextLinePosition.None))
                using (ImGui.PushUnderline())
                    Assert.Equal(0u, (uint)Lines().Color);

                // Lines as the extra decoration are apart from the lines of the style.
                var wavy = new ImTextLineDecoration(ImTextLinePosition.Under, ImTextLineStyle.Wavy, 0xFF00FF00);
                using (ImGui.PushDecoration(ImTextDecorationId.Line, wavy.Color, wavy.ToDecoration().Data))
                {
                    Assert.Equal(0xFF0000FFu, (uint)Lines().Color);
                    Assert.True(ImTextLineDecoration.TryRead(ImGui.TextStyle.ExtraDecoration, out var extra));
                    Assert.Equal(ImTextLineStyle.Wavy, extra.Style);

                    // Pushing the same kind keeps what is not given.
                    using (ImGui.PushDecoration(ImTextDecorationId.Line, 0xFFFFFFFF))
                    {
                        Assert.True(ImTextLineDecoration.TryRead(ImGui.TextStyle.ExtraDecoration, out extra));
                        Assert.Equal(ImTextLineStyle.Wavy, extra.Style);
                    }
                }
            }

            using (ImGui.PushTextEdge(width: 0.2f))
            {
                Assert.Equal(new ImTextEdge(0xFF000000, 0.2f), ImGui.TextStyle.Edge);
                using (ImGui.PushTextEdge(0xFF00FF00))
                    Assert.Equal(new ImTextEdge(0xFF00FF00, 0.2f), ImGui.TextStyle.Edge);
            }

            Assert.Equal(default, ImGui.TextStyle);
        });
    }

    [Fact]
    public void DecorationsAreDrawnUnderOrOverTheText()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            var dl = ImGui.GetWindowDrawList();
            var from = dl.VtxBuffer.Size;
            var wavy = new ImTextLineDecoration(ImTextLinePosition.Under, ImTextLineStyle.Wavy, 0xFF00FF00);
            using (ImGui.PushLines(ImTextLinePosition.Under | ImTextLinePosition.Through, color: 0xFF0000FF))
            using (ImGui.PushTextStyle(ImGui.TextStyle with { ExtraDecoration = wavy }))
                ImGui.Text("Hello");

            // One decoration is drawn in two passes. Its underline is drawn before the glyphs, and its strikethrough
            // after.
            var vertices = new Span<ImDrawVert>(dl.VtxBuffer.Data, dl.VtxBuffer.Size)[from..].ToArray();
            int First(uint color) => Array.FindIndex(vertices, v => v.Col == color);
            int Last(uint color) => Array.FindLastIndex(vertices, v => v.Col == color);
            var glyph = First(ImGui.GetColorU32(ImGuiCol.Text));
            Assert.True(glyph >= 0);
            Assert.InRange(First(0xFF0000FF), 0, glyph - 1);
            Assert.True(Last(0xFF0000FF) > glyph, "The strikethrough is over the text");
            Assert.InRange(First(0xFF00FF00), 0, glyph - 1);
            Assert.InRange(Last(0xFF00FF00), 0, glyph - 1);
        });
    }

    [Fact]
    public void OnlyTheItemsInTheClipRectangleAreDrawn()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            // One long line, and lines below it. Only what is near a small clip rectangle at the start is drawn.
            var text = string.Concat(Enumerable.Repeat("abcdefghij", 300)) + string.Concat(Enumerable.Repeat("\nklm", 50));
            var layout = ImGui.CreateTextLayout(text);
            try
            {
                var dl = ImGui.GetWindowDrawList();
                var pos = ImGui.GetCursorScreenPos();
                var from = dl.VtxBuffer.Size;
                layout.Render(dl, pos, ImGui.GetColorU32(ImGuiCol.Text), new Vector4(pos, pos.X + 100, pos.Y + 10));
                var clipped = dl.VtxBuffer.Size - from;
                Assert.InRange(clipped / 4, 1, 40);

                // Without clipping, everything is drawn. That is 3000 glyphs on the first line, and 150 below it.
                from = dl.VtxBuffer.Size;
                layout.Render(dl, pos, ImGui.GetColorU32(ImGuiCol.Text), new Vector4(pos, pos.X + 100000, pos.Y + 100000));
                Assert.Equal(3150, (dl.VtxBuffer.Size - from) / 4);
            }
            finally
            {
                layout.Release();
            }
        });
    }

    [Fact]
    public void LayoutsUsedInOneFrameOnlyAreDroppedSoon()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        TextLayouter.ClearCache();

        // Text that stays, and text that changes every frame, as a counter does.
        for (var frame = 0; frame < 20; frame++)
        {
            imgui.Frame(() =>
            {
                ImGui.Text("Stays"u8);
                ImGui.Text($"Counter {frame}");
            });
        }

        // Of the 20 counters, only the last few remain, though up to two layouts each (text is measured and drawn with
        // other parameters), and ImGui draws text of its own, such as the window's title.
        Assert.InRange(TextLayouter.CacheCount, 2, 12);
        Assert.InRange(TextLayouter.ReusedCount, 1, 6);

        // The text that stays was reused, and is kept long after its last use.
        for (var frame = 0; frame < 10; frame++)
            imgui.Frame(() => { });
        var misses = TextLayouter.Misses;
        imgui.Frame(() => ImGui.Text("Stays"u8));
        Assert.Equal(misses, TextLayouter.Misses);

        // A counter from long ago is laid out again.
        imgui.Frame(() => ImGui.Text("Counter 3"u8));
        Assert.True(TextLayouter.Misses > misses);
    }

    [Fact]
    public void SoftHyphensShowOnlyWhereLinesWrap()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            static ImTextLine[] Lines(string text, float wrapWidth = 0)
            {
                var layout = ImGui.CreateTextLayout(text, wrapWidth: wrapWidth);
                try
                {
                    return layout.GetLines().ToArray();
                }
                finally
                {
                    layout.Release();
                }
            }

            var plain = Lines("hyphenation")[0].Size.X;
            var hyphenated = Lines("hyphen-")[0].Size.X;

            // Unbroken, the soft hyphen takes no space.
            var unbroken = Lines("hyphen\u00ADation");
            Assert.Single(unbroken);
            Assert.Equal(plain, unbroken[0].Size.X, 0.01f);

            // Wrapped after it, it shows as a hyphen at the end of the line.
            var wrapped = Lines("hyphen\u00ADation", (hyphenated + plain) / 2);
            Assert.Equal(2, wrapped.Length);
            Assert.Equal(hyphenated, wrapped[0].Size.X, 0.01f);
        });
    }

    [Fact]
    public void BackgroundsOfAlphaZeroAreNone()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            var dl = ImGui.GetWindowDrawList();
            int Drawn(ImTextStyle style)
            {
                var from = dl.VtxBuffer.Size;
                ImGui.TextStyled("Hello", style);
                return dl.VtxBuffer.Size - from;
            }

            var plain = Drawn(ImGui.TextStyle);
            Assert.True(Drawn(ImGui.TextStyle.WithDecoration(new(ImTextDecorationId.Background, 0xFF0000FF))) > plain);
            Assert.Equal(plain, Drawn(ImGui.TextStyle.WithDecoration(new(ImTextDecorationId.Background))));

            using (ImGui.PushBackground(0xFF0000FF))
            {
                using (ImGui.PushBackground(0))
                    Assert.Equal(0u, (uint)ImGui.TextStyle.Background);
            }
        });
    }

    [Fact]
    public void PushFontFamilyKeepsTheCurrentStyle()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            var plainFont = ImGui.Font;
            using (ImGui.PushUnderline(0xFF0000FF))
            {
                var style = ImGui.TextStyle;
                using (plainFont.Push())
                    Assert.False(ImGui.TextStyle.Lines.IsOn);
                using (ImGui.PushFontFamily(plainFont))
                    Assert.Equal(style, ImGui.TextStyle);
                using (ImGui.PushFontFamily(ImFontPtr.Null))
                    Assert.Equal(style, ImGui.TextStyle);
            }
        });
    }

    [Fact]
    public void RegisteredDecorationsGetMergedSpansUntilUnregistered()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        var calls = new List<(float X0, float X1, uint Context, ulong Data)>();
        var id = TextDecorationRegistry.Register(
            (in TextDecorationSpan s) => calls.Add((s.X0, s.X1, s.Context, s.Data)),
            TextDecorationFlags.None);
        try
        {
            // Bold for "Hello" and the decoration over all of it. It is one span, though the text is in two runs.
            void Draw() =>
                ImGui.TextStyledRanges(
                    "Hello, world"u8,
                    [
                        new(0, 12, ImGui.TextStyle with { ExtraDecoration = new(id, 7, 9) }),
                        new(0, 5, ImGui.TextStyle with { Bold = true, ExtraDecoration = new(id, 7, 9) }),
                    ]);

            imgui.Frame(Draw);
            var call = Assert.Single(calls);
            Assert.Equal((7u, 9ul), (call.Context, call.Data));
            Assert.True(call.X1 > call.X0);

            TextDecorationRegistry.Unregister(id);
            calls.Clear();
            imgui.Frame(Draw);
            Assert.Empty(calls);

            // A later decoration may take its place in the table, but not its ID.
            var next = TextDecorationRegistry.Register((in TextDecorationSpan _) => { }, TextDecorationFlags.None);
            Assert.NotEqual(id, next);
            TextDecorationRegistry.Unregister(next);
        }
        finally
        {
            TextDecorationRegistry.Unregister(id);
        }
    }

    [Fact]
    public void DecorationsDrawGlyphsInTheirLayersBandByBand()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        const uint edgeColor = 0xFF101010;
        const uint red = 0xFF0000FF;
        const uint green = 0xFF00FF00;
        var calls = new List<(TextDecorationLayer Layer, int Band, uint Context)>();
        var id = TextDecorationRegistry.Register(
            (in TextDecorationSpan s) =>
            {
                calls.Add((s.Layer, s.Band, s.Context));
                if (s.Layer == TextDecorationLayer.BelowFill)
                    s.DrawGlyphs(s.Band == 0 ? red : green, 2 - s.Band);
            },
            TextDecorationFlags.BelowText | TextDecorationFlags.BelowEdge | TextDecorationFlags.BelowFill |
            TextDecorationFlags.OverText,
            2);
        try
        {
            uint[] quadColors = [];
            imgui.Frame(() =>
            {
                // "Hello" and "world" are two spans, as their contexts differ. The text has an edge.
                var dl = ImGui.GetWindowDrawList();
                var from = dl.VtxBuffer.Size;
                var style = ImGui.TextStyle.WithEdge(edgeColor);
                ImGui.TextStyledRanges(
                    "Hello world"u8,
                    [
                        new(0, 11, style),
                        new(0, 5, style with { ExtraDecoration = new(id, 1) }),
                        new(6, 5, style with { ExtraDecoration = new(id, 2) }),
                    ]);
                quadColors = [.. new Span<ImDrawVert>(dl.VtxBuffer.Data, dl.VtxBuffer.Size)[from..]
                                 .ToArray()
                                 .Where((_, i) => i % 4 == 0)
                                 .Select(v => v.Col)];
            });

            // Each layer is drawn for every band before the next, and each band for every span before the next.
            (TextDecorationLayer, int, uint)[] expected =
            [
                .. from layer in Enum.GetValues<TextDecorationLayer>()
                   from band in new[] { 0, 1 }
                   from context in new[] { 1u, 2u }
                   select (layer, band, context),
            ];
            Assert.Equal(expected, calls);

            // The edge of every glyph, then the bands from the widest, then the fill, so that no band covers a fill.
            uint[] runs = [.. quadColors.Where((c, i) => i == 0 || quadColors[i - 1] != c)];
            Assert.Equal([edgeColor, red, green, ImGui.GetColorU32(ImGuiCol.Text)], runs);
            Assert.Equal(10, quadColors.Count(c => c == red));
            Assert.Equal(10, quadColors.Count(c => c == green));
        }
        finally
        {
            TextDecorationRegistry.Unregister(id);
        }
    }

    [Fact]
    public void TextStyledRangesStylesOnlyTheRanges()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            // "Hello, world" has 6 glyphs before the space and 5 after. Each is a quad of 4 vertices.
            var dl = ImGui.GetWindowDrawList();
            foreach (var utf16 in new[] { false, true })
            {
                var from = dl.VtxBuffer.Size;
                var y = ImGui.GetCursorScreenPos().Y;
                ReadOnlySpan<ImTextStyleRange> ranges = [new(7, 5, ImGui.TextStyle with { Color = 0xFF0000FF })];
                if (utf16)
                    ImGui.TextStyledRanges("Hello, world", ranges);
                else
                    ImGui.TextStyledRanges("Hello, world"u8, ranges);

                var vertices = new Span<ImDrawVert>(dl.VtxBuffer.Data, dl.VtxBuffer.Size)[from..].ToArray();
                Assert.Equal(20, vertices.Count(v => v.Col == 0xFF0000FF));
                Assert.Equal(24, vertices.Count(v => v.Col == ImGui.GetColorU32(ImGuiCol.Text)));
                Assert.True(ImGui.GetCursorScreenPos().Y > y, "The text is an item, as with ImGui.Text");
            }
        });
    }

    [Fact]
    public void TabsReachTheNextStopFromTheLineStart()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            // Where the char at an index starts.
            static float X(string text, int index, int tabSize = 0)
            {
                var layout = ImGui.CreateTextLayout(text, tabSize: tabSize);
                try
                {
                    return layout.GetCaret(index * 2).Pos.X;
                }
                finally
                {
                    layout.Release();
                }
            }

            var space = X("  ", 1);
            Assert.True(space > 0);
            Assert.Equal(space * 4, X("\tx", 1), 0.01f);
            Assert.Equal(space * 4, X("   \tx", 4), 0.01f);
            Assert.Equal(space * 2, X("\tx", 1, tabSize: 2), 0.01f);

            // Each line counts from its own start, and a stop closer than half a space is skipped.
            Assert.Equal(space * 4, X("ab\n\tx", 4), 0.01f);
            Assert.Equal(space * 8, X("    \tx", 5), 0.01f);
        });
    }

    [Fact]
    public void MarkupLayoutsGoThroughTheirStylers() => RunOnMainThread(() =>
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        var seString = new Lumina.Text.SeStringBuilder().Append("Hello").PushColorType(1).Append(", world").PopColorType().ToArray();
        var styler = new RecordingStyler();
        var previous = TextStylers.Get(ImTextMarkup.SeString);
        TextStylers.Register(styler);
        try
        {
            imgui.Frame(() =>
            {
                var layout = ImGui.CreateTextLayout(new Lumina.Text.ReadOnly.ReadOnlySeStringSpan(seString), wrapWidth: 300);
                try
                {
                    Assert.False(layout.IsNull);
                    Assert.Equal(seString, styler.Asked?.Text);
                    Assert.Equal(ImTextMarkup.SeString, styler.Asked?.Markup);
                    Assert.Equal(300, styler.Asked?.WrapWidth);
                    Assert.True(layout.GetCaret(11).Pos.X > 0);
                }
                finally
                {
                    layout.Release();
                }

                // Equal arguments share the layout, so the styler is not asked again.
                styler.Asked = null;
                ImGui.CreateTextLayout(seString, ImTextMarkup.SeString, wrapWidth: 300).Release();
                Assert.Null(styler.Asked);
            });
        }
        finally
        {
            TextStylers.Unregister(styler);
            if (previous is not null)
                TextStylers.Register(previous);
        }
    });

    [Fact]
    public void CreatedTextLayoutsAreStyledAndQueryable()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            var layout = ImGui.CreateTextLayout(
                "Hello, world"u8,
                [new(7, 5, ImGui.TextStyle with { Color = 0xFF0000FF })]);
            try
            {
                Assert.False(layout.IsNull);

                // Queries are in bytes of the text. "world" starts right of where the hit test lands before it.
                var world = layout.GetCaret(7);
                Assert.Equal(7, layout.HitTest(world.Pos + new Vector2(0.5f, 1)).TextOffset);
                Span<Vector4> rects = stackalloc Vector4[2];
                Assert.Equal(1, layout.GetRangeRects(7, 12, rects));
                Assert.Equal(world.Pos.X, rects[0].X, 0.01f);

                // Drawn with and without a clip rectangle, only "world" in the range's color.
                var dl = ImGui.GetWindowDrawList();
                var from = dl.VtxBuffer.Size;
                var pos = ImGui.GetCursorScreenPos();
                layout.Render(dl, pos, ImGui.GetColorU32(ImGuiCol.Text));
                Assert.Equal(20, new Span<ImDrawVert>(dl.VtxBuffer.Data, dl.VtxBuffer.Size)[from..].ToArray().Count(v => v.Col == 0xFF0000FF));
                from = dl.VtxBuffer.Size;
                layout.RenderRange(dl, pos, ImGui.GetColorU32(ImGuiCol.Text), 7, 12, new Vector4(pos, pos.X + 1000, pos.Y + 100));
                Assert.Equal(20, dl.VtxBuffer.Size - from);
            }
            finally
            {
                layout.Release();
            }
        });
    }

    [Fact]
    public void StrikethroughIsDrawnThroughTheText()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            var dl = ImGui.GetWindowDrawList();
            var top = ImGui.GetCursorScreenPos().Y;
            var from = dl.VtxBuffer.Size;
            using (ImGui.PushStrikethrough(0xFF0000FF))
                ImGui.Text("Hello");

            // Between the line top and the baseline (about 12 px below the top), whole pixels thick. It is not blurred
            // by anti-aliasing, which adds transparent fringes.
            var vertices = new Span<ImDrawVert>(dl.VtxBuffer.Data, dl.VtxBuffer.Size)[from..].ToArray();
            Assert.DoesNotContain(vertices, v => v.Col == 0x000000FF);
            var ys = vertices.Where(v => v.Col == 0xFF0000FF).Select(v => v.Pos.Y - top).ToArray();
            Assert.NotEmpty(ys);
            Assert.InRange(ys.Min(), 4f, 12f);
            Assert.Equal(1f, ys.Max() - ys.Min());
        });
    }

    [Fact]
    public void SubpixelGlyphsTakeThreeTexelsPerPixel()
    {
        var atlas = GlyphAtlas.Shared;
        try
        {
            // Two pixels of red, green and blue coverage.
            byte[] rgb = [10, 20, 30, 40, 50, 60];
            var image = atlas.Add(new GlyphBitmap { Width = 2, Height = 1, Alpha = [30, 60], Rgb = rgb });
            Assert.Equal(GlyphKind.Subpixel, image.Kind);

            // The six coverages in a row.
            Gpu.Flush();
            var pixels = atlas.Coverage.Read(image.Slice);
            for (var i = 0; i < rgb.Length; i++)
                Assert.Equal(rgb[i], pixels[(image.Y * GlyphAtlas.SliceSize) + image.X + i]);

            var drawn = atlas.ToImage(image, 1, default);
            Assert.Equal(6u, (drawn.TexelMax & 0xFFFF) - (drawn.TexelMin & 0xFFFF));
            Assert.Equal(2, drawn.Rect.Z - drawn.Rect.X);

            // A grayscale glyph shares the slice.
            var gray = atlas.Add(new GlyphBitmap { Width = 2, Height = 1, Alpha = [1, 2] });
            Assert.Equal(image.Slice, gray.Slice);
        }
        finally
        {
            GlyphAtlas.DisposeShared();
        }
    }

    [Fact]
    public void AtlasKeepsSlicesInUseBeyondItsCapUpToItsHardLimit()
    {
        var atlas = GlyphAtlas.Shared;
        try
        {
            atlas.Coverage.MaxSlices = 2;

            // Glyphs of 2000 pixels square, 4 to a slice.
            var big = new GlyphBitmap { Width = 2000, Height = 2000, Alpha = new byte[2000 * 2000] };
            GlyphAtlas.CachedImage[] FillSlice() => [.. Enumerable.Range(0, 4).Select(_ => atlas.Add(big))];
            void Draw(GlyphAtlas.CachedImage[] images)
            {
                foreach (var image in images)
                    _ = atlas.ToImage(image, 1, default);
            }

            // Slices used in a frame are not retired, so the plane grows past the cap, and stays there while they are.
            var first = FillSlice();
            var second = FillSlice();
            var third = FillSlice();
            atlas.EndFrame();
            Assert.Equal(3, atlas.SliceCount);
            Assert.Equal(0, atlas.EvictionCount);
            Draw(first);
            Draw(second);
            Draw(third);
            atlas.EndFrame();
            Assert.Equal(3, atlas.SliceCount);
            Assert.All(first.Concat(second).Concat(third), x => Assert.True(atlas.IsValid(x)));

            // Once some are not used, they go down to the cap, the one used least recently first, and of those the
            // oldest.
            Draw(third);
            atlas.EndFrame();
            Assert.Equal(2, atlas.SliceCount);
            Assert.Equal(1, atlas.EvictionCount);
            Assert.All(first, x => Assert.False(atlas.IsValid(x)));
            Assert.All(second.Concat(third), x => Assert.True(atlas.IsValid(x)));

            // Up to the hard limit. Glyphs beyond have no room, and are tried again.
            atlas.Coverage.MaxSlices = 1;
            atlas.EndFrame();
            var many = Enumerable.Range(0, atlas.Coverage.HardMaxSlices + 1).SelectMany(_ => FillSlice()).ToArray();
            Assert.Equal(atlas.Coverage.HardMaxSlices, atlas.SliceCount);
            Assert.Contains(GlyphAtlas.CachedImage.Full, many);
        }
        finally
        {
            GlyphAtlas.DisposeShared();
        }
    }

    [Fact]
    public void GlyphsOfClearedSlicesAreCopiedBackWhenDrawnSoon()
    {
        byte[] alpha = [1, 2, 3, 4, 5, 6];
        byte[] rgba = [.. Enumerable.Range(0, 2 * 3 * 4).Select(x => (byte)(x + 10))];
        byte[] mask = [7, 8, 9, 10, 11, 12];
        var big = new GlyphBitmap { Width = 2000, Height = 2000, Alpha = new byte[2000 * 2000] };
        var bigColor = big with { Rgba = new byte[2000 * 2000 * 4] };

        // Adds glyphs a frame until the first slices of both planes are retired, as they fill and were used before.
        void RetireFirstSlices(GlyphAtlas atlas)
        {
            atlas.EndFrame();
            while (atlas.Coverage.EvictionCount == 0 || (atlas.Color.SliceCount > 0 && atlas.Color.EvictionCount == 0))
            {
                _ = atlas.Add(big);
                if (atlas.Color.SliceCount > 0)
                    _ = atlas.Add(bigColor);
                atlas.EndFrame();
            }
        }

        try
        {
            var atlas = GlyphAtlas.Shared;
            atlas.Coverage.MaxSlices = 1;
            var gray = atlas.Add(new GlyphBitmap { Width = 3, Height = 2, Alpha = alpha });
            var stale = atlas.Add(new GlyphBitmap { Width = 3, Height = 2, Alpha = alpha });
            RetireFirstSlices(atlas);
            Assert.False(atlas.IsValid(gray));

            var restored = gray;
            Assert.True(atlas.TryRestore(ref restored));
            Assert.True(atlas.IsValid(restored));
            Assert.Equal(1, atlas.RestoreCount);
            Assert.Equal(
                gray with { Slice = restored.Slice, X = restored.X, Y = restored.Y, Generation = restored.Generation },
                restored);

            // Copied on the GPU.
            Gpu.Flush();
            var pixels = atlas.Coverage.Read(restored.Slice);
            for (var y = 0; y < 2; y++)
            {
                for (var x = 0; x < 3; x++)
                    Assert.Equal(alpha[(y * 3) + x], pixels[((restored.Y + y) * GlyphAtlas.SliceSize) + restored.X + x]);
            }

            // Not for long.
            for (var i = 0; i < 4; i++)
                atlas.EndFrame();
            Assert.False(atlas.TryRestore(ref stale));
        }
        finally
        {
            GlyphAtlas.DisposeShared();
        }

        try
        {
            // The color glyph is in the color plane, and its mask in the coverage plane. Both are retired.
            var atlas = GlyphAtlas.Shared;
            atlas.Coverage.MaxSlices = 1;
            atlas.Color.MaxSlices = 1;
            var color = atlas.Add(new GlyphBitmap { Width = 3, Height = 2, Alpha = alpha, Rgba = rgba, Mask = mask });
            RetireFirstSlices(atlas);
            Assert.False(atlas.IsValid(color));

            var restored = color;
            Assert.True(atlas.TryRestore(ref restored));
            Assert.True(atlas.IsValid(restored));
            Assert.NotEqual(color.Slice, restored.Slice);
            Assert.NotEqual(color.MaskSlice, restored.MaskSlice);

            Gpu.Flush();
            var colors = atlas.Color.Read(restored.Slice);
            var masks = atlas.Coverage.Read(restored.MaskSlice);
            for (var y = 0; y < 2; y++)
            {
                for (var x = 0; x < 3; x++)
                {
                    Assert.Equal(mask[(y * 3) + x], masks[((restored.MaskY + y) * GlyphAtlas.SliceSize) + restored.MaskX + x]);
                    for (var c = 0; c < 4; c++)
                        Assert.Equal(rgba[(((y * 3) + x) * 4) + c], colors[((((restored.Y + y) * GlyphAtlas.SliceSize) + restored.X + x) * 4) + c]);
                }
            }
        }
        finally
        {
            GlyphAtlas.DisposeShared();
        }
    }

    [Fact]
    public void GlyphsCopiedBackAreKeptApartFromNewOnes()
    {
        var atlas = GlyphAtlas.Shared;
        try
        {
            atlas.Coverage.MaxSlices = 1;
            var small = new GlyphBitmap { Width = 3, Height = 2, Alpha = [1, 2, 3, 4, 5, 6] };
            var big = new GlyphBitmap { Width = 2000, Height = 2000, Alpha = new byte[2000 * 2000] };
            var glyph = atlas.Add(small);

            // A glyph a frame, until the first slice fills and is retired.
            atlas.EndFrame();
            while (atlas.EvictionCount == 0)
            {
                _ = atlas.Add(big);
                atlas.EndFrame();
            }

            // Copied back, it goes to a slice of its own, which new glyphs do not go to.
            Assert.True(atlas.TryRestore(ref glyph));
            var fresh = atlas.Add(small);
            Assert.True(atlas.IsValid(fresh));
            Assert.NotEqual(glyph.Slice, fresh.Slice);
            Assert.Equal(fresh.Slice, atlas.Add(small).Slice);
        }
        finally
        {
            GlyphAtlas.DisposeShared();
        }
    }

    [Fact]
    public void AtlasKeepsNearItsCapWhenGlyphsFromAllOverItAreDrawn()
    {
        var atlas = GlyphAtlas.Shared;
        try
        {
            atlas.Coverage.MaxSlices = 4;
            var cache = new GlyphImageCache();
            var random = new Random(1);

            // Glyphs of 100 texels square with the gap, 1600 to a slice.
            var bitmap = new GlyphBitmap { Width = 99, Height = 99, Alpha = new byte[99 * 99] };
            var next = 0ul;
            var most = 0;
            for (var frame = 0; frame < 200; frame++)
            {
                // New glyphs, and glyphs drawn before, from all over the atlas, as random text does.
                for (var i = 0; i < 100; i++)
                    _ = atlas.ToImage(cache.Add(next++, bitmap, 0), 1, default);
                for (var i = 0; i < 100; i++)
                {
                    if (cache.TryGet((ulong)random.NextInt64((long)next), out var image))
                        _ = atlas.ToImage(image, 1, default);
                }

                // It grows past the cap while the slices are drawn from, but stays far from the hard limit.
                most = Math.Max(most, atlas.SliceCount);
                atlas.EndFrame();
            }

            Assert.True(most <= atlas.Coverage.HardMaxSlices / 2, $"{most} slices");
            Assert.True(atlas.EvictionCount > 0);

            // Out of use, it goes back to the cap.
            atlas.EndFrame();
            Assert.Equal(4, atlas.SliceCount);
        }
        finally
        {
            GlyphAtlas.DisposeShared();
        }
    }

    [Fact]
    public void NativeLayoutAnswersQueries()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            var text = "ab\ncd"u8;
            ImTextLayout* nativeLayout;
            fixed (byte* p = text)
                Assert.Equal(0, ImGuiPNative.CreateTextLayout(ImGui.Font, null, p, p + text.Length, &nativeLayout));
            ImTextLayoutPtr layout = nativeLayout;
            Assert.False(layout.IsNull);
            ImTextLayout* failed;
            Assert.True(ImGuiPNative.CreateTextLayout(null, null, null, null, &failed) < 0);
            Assert.True(failed is null);
            // The cache holds a reference too.
            var refs = layout.AddRef();
            Assert.True(refs >= 2, $"{refs} references");
            Assert.Equal(refs - 1, layout.Release());

            var box = layout.GetBoundingBox(out _);
            Assert.Equal(ImGui.CalcTextSize("ab\ncd").Y, box.W);
            var lines = layout.GetLines();
            Assert.Equal(2, lines.Length);
            Assert.Equal(3, lines[1].TextBegin);
            Assert.Equal(16f, lines[1].Pos.Y);

            var hit = layout.HitTest(new(1, 20));
            Assert.Equal(1, hit.LineIndex);
            Assert.Equal(3, hit.TextOffset);
            Assert.Equal(4, layout.Move(hit, ImTextMoveFlags.Grapheme, 1, 0).TextOffset);
            Assert.Equal(0, layout.Move(hit, ImTextMoveFlags.BlockRelative, 0, -1).TextOffset);

            // Past the last line goes to the end of the text, or (BlockClampKeepInline) along the last line.
            var first = layout.GetCaret(1);
            Assert.Equal(5, layout.Move(first, ImTextMoveFlags.BlockRelative, 0, 2).TextOffset);
            Assert.Equal(4, layout.Move(first, ImTextMoveFlags.BlockRelative | ImTextMoveFlags.BlockClampKeepInline, 0, 2).TextOffset);
            Assert.Equal(0, layout.Move(first, ImTextMoveFlags.BlockRelative, 0, -1).TextOffset);
            Assert.Equal(1, layout.Move(first, ImTextMoveFlags.BlockRelative | ImTextMoveFlags.BlockClampKeepInline, 0, -1).TextOffset);

            // Block axis, then inline axis, then dx. That is the next line's start, one grapheme on.
            Assert.Equal(4, layout.Move(first, ImTextMoveFlags.InlineBegin | ImTextMoveFlags.Grapheme, 1, 1).TextOffset);

            // dx crosses lines, or (DxWithinLine) stops at the line's ends.
            Assert.Equal(2, layout.Move(hit, ImTextMoveFlags.Grapheme, -1, 0).TextOffset);
            Assert.Equal(3, layout.Move(hit, ImTextMoveFlags.Grapheme | ImTextMoveFlags.DxWithinLine, -1, 0).TextOffset);
            Assert.Equal(2, layout.Move(first, ImTextMoveFlags.Grapheme | ImTextMoveFlags.DxWithinLine, 5, 0).TextOffset);
            Assert.Equal(1, layout.Move(first, ImTextMoveFlags.Grapheme | ImTextMoveFlags.DxWithinLine, 5, 1).LineIndex);

            Span<Vector4> rects = stackalloc Vector4[2];
            Assert.Equal(2, layout.GetRangeRects(1, 4, rects));
            Assert.Equal(-1, layout.GetEllipsisOffset());
            Assert.Equal(0, layout.GetRunCount());
            Assert.True(layout.GetRunRects(0).IsEmpty);

            // Its own, with the cache's left.
            Assert.Equal(refs - 2, layout.Release());
        });
    }

    [Fact]
    public void ContextLayoutMatchesNative()
    {
        // Fields ImGui hands out pointers to. InputTextMovesThroughLayouts checks InputTextState, past KeysMayBeCharInput.
        using var imgui = new ImGuiScope();
        var ctx = ImGui.GetCurrentContext().Handle;
        Assert.Equal((nint)ImGuiNative.GetIO(), (nint)(&ctx->IO));
        Assert.Equal((nint)ImGuiNative.GetPlatformIO(), (nint)(&ctx->PlatformIO));
        Assert.Equal((nint)ImGuiNative.GetStyle(), (nint)(&ctx->Style));
        Assert.Equal((nint)ImGuiNative.GetDrawListSharedData(), (nint)(&ctx->DrawListSharedData));

        // Table column indices are 16-bit (they were mapped to 8-bit types).
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);
        imgui.Frame(() =>
        {
            ImGui.Begin("table");
            Assert.True(ImGui.BeginTable("t", 3));
            ImGui.TableSetupColumn("a");
            ImGui.TableSetupColumn("b");
            ImGui.TableSetupColumn("c");
            var table = ImGuiP.GetCurrentTable().Handle;
            Assert.Equal(3, table->ColumnsCount);
            Assert.Equal(3, table->DeclColumnsCount);
            Assert.Equal((nint)ImGuiP.GetCurrentWindow().Handle, (nint)table->InnerWindow);
            ImGui.EndTable();
            ImGui.End();
        });
    }

    [Fact]
    public void InputTextMovesThroughLayouts()
    {
        using var imgui = new ImGuiScope();
        imgui.SetDefaultFont(new DirectWriteFont("Segoe UI", File.ReadAllBytes(Path.Combine(FontsDir, "segoeui.ttf")), 0), 16f);

        // "áb" (a + U+0301), then a paragraph that wraps.
        var text = "áb\n" + string.Concat(Enumerable.Repeat("word ", 20));
        var buf = new byte[256];
        var length = System.Text.Encoding.UTF8.GetBytes(text, buf);
        var origin = Vector2.Zero;
        var focus = true;
        void Draw()
        {
            ImGui.SetNextWindowPos(Vector2.Zero);
            ImGui.SetNextWindowSize(new(400, 300));
            ImGui.Begin("test", ImGuiWindowFlags.NoDecoration);
            if (focus)
                ImGui.SetKeyboardFocusHere();
            origin = ImGui.GetCursorScreenPos() + ImGui.GetStyle().FramePadding;
            ImGui.InputTextMultiline("##text", buf, new(200, 200), ImGuiInputTextFlags.WordWrap);
            ImGui.End();
        }

        var io = ImGui.GetIO();
        int Cursor() => ImGui.GetCurrentContext().Handle->InputTextState.GetCursorPos();
        void Press(ImGuiKey key, bool ctrl = false)
        {
            if (ctrl)
                io.AddKeyEvent(ImGuiKey.ModCtrl, true);
            io.AddKeyEvent(key, true);
            imgui.Frame(Draw);
            io.AddKeyEvent(key, false);
            if (ctrl)
                io.AddKeyEvent(ImGuiKey.ModCtrl, false);
            imgui.Frame(Draw);
        }

        void Click(Vector2 pos)
        {
            io.AddMousePosEvent(pos.X, pos.Y);
            imgui.Frame(Draw);
            io.AddMouseButtonEvent(0, true);
            imgui.Frame(Draw);
            io.AddMouseButtonEvent(0, false);
            imgui.Frame(Draw);
        }

        imgui.Frame(Draw);
        focus = false;
        imgui.Frame(Draw);
        var ctx = ImGui.GetCurrentContext().Handle;
        Assert.Equal((nint)ImGuiPNative.GetInputTextState(ctx->ActiveId), (nint)(&ctx->InputTextState));
        Press(ImGuiKey.Home, ctrl: true);
        Assert.Equal(0, Cursor());

        // By grapheme. The combining mark goes with its base.
        Press(ImGuiKey.RightArrow);
        Assert.Equal(3, Cursor());
        Press(ImGuiKey.RightArrow);
        Assert.Equal(4, Cursor());
        Press(ImGuiKey.LeftArrow);
        Assert.Equal(3, Cursor());

        // Down into the paragraph, then along its first visual line to the wrap point.
        Press(ImGuiKey.DownArrow);
        Assert.InRange(Cursor(), 5, 8);
        Press(ImGuiKey.End);
        var wrap = Cursor();
        Assert.InRange(wrap, 10, length - 1); // a wrap point, not the end of the paragraph
        Assert.Equal(' ', (char)buf[wrap - 1]);
        Press(ImGuiKey.DownArrow);
        Assert.True(Cursor() > wrap, $"{Cursor()} after {wrap}");
        Press(ImGuiKey.UpArrow);
        Assert.InRange(Cursor(), 5, wrap); // back on the paragraph's first visual line
        Press(ImGuiKey.UpArrow);
        Assert.Equal(4, Cursor()); // the end of "áb", as the goal x is past it

        // Click past the end of the first line, then at the start of the third visual line.
        Click(origin + new Vector2(150, 8));
        Assert.Equal(4, Cursor());
        Click(origin + new Vector2(0, 16 * 2 + 8));
        Assert.Equal(wrap, Cursor());
    }

    [Fact]
    public void SystemFallbackFindsInstalledFonts()
    {
        using var imgui = new ImGuiScope();
        var fallback = new SystemFallbackFont("ja-JP");
        Span<uint> glyphs = stackalloc uint[3];
        fallback.GetGlyphIndices([0x3042, 'A', 0x10FFFD], glyphs);
        Assert.NotEqual(0u, glyphs[0]); // a Japanese font
        Assert.NotEqual(0u, glyphs[1]);
        Assert.Equal(0u, glyphs[2]); // a private use codepoint no font has
        Assert.NotNull(fallback.GetFace(glyphs[0]));

        var font = new CompositeFont("test", 16f);
        font.Add(new(new DirectWriteFont("Consolas", File.ReadAllBytes(Path.Combine(FontsDir, "consola.ttf")), 0)));
        font.AddTerminal(new(fallback));
        font.Add(new(new DirectWriteFont("Malgun Gothic", File.ReadAllBytes(Path.Combine(FontsDir, "malgun.ttf")), 0)));
        var fontPtr = imgui.SetDefaultFont(font, 16f);

        // Fonts added after the end go before it.
        Assert.Equal(1u, fontPtr.GetGlyphIndex('한') >> 24);
        Assert.Equal(2u, fontPtr.GetGlyphIndex(0x0E01) >> 24); // Thai, in neither Consolas nor Malgun Gothic

        imgui.Frame(() =>
        {
            ImGui.Begin("test");
            ImGui.Text("กข abc あいう 한글");
            ImGui.End();
        });
        Assert.True(GlyphAtlas.Shared.GlyphCount >= 8, $"{GlyphAtlas.Shared.GlyphCount} glyphs");
        imgui.DumpPage("fallback");
    }

    [Fact]
    public void FdtRunesAreBigEndianUtf8()
    {
        Assert.Equal(0xE38182u, ((FdtRune)0x3042).Raw);
        foreach (var c in new uint[] { 0, 0x41, 0x7F, 0x80, 0x7FF, 0x800, 0xFFFF, 0x10000, 0x1F600, 0x10FFFF })
            Assert.Equal(c, ((FdtRune)c).Codepoint);
    }

    [Fact]
    public void FdtFileViewFindsGlyphsAndKerning()
    {
        // U+1F600 is F0 9F 98 80 in UTF-8. That is negative as an int, so it sorts last only when compared unsigned.
        uint[] chars = ['A', 'B', 0x3042, 0x1F600];
        var glyphsOffset = Unsafe.SizeOf<FdtFileHeader>() + Unsafe.SizeOf<FdtGlyphTableHeader>();
        var kerningOffset = glyphsOffset + (chars.Length * Unsafe.SizeOf<FdtGlyph>());
        var pairsOffset = kerningOffset + Unsafe.SizeOf<FdtKerningPairTableHeader>();
        var data = new byte[pairsOffset + (2 * Unsafe.SizeOf<FdtKerningPair>())];
        var span = data.AsSpan();
        MemoryMarshal.Write(
            span,
            new FdtFileHeader
            {
                GlyphTableHeaderOffset = Unsafe.SizeOf<FdtFileHeader>(),
                KerningTableHeaderOffset = kerningOffset,
            });
        "fcsv0100"u8.CopyTo(span);
        MemoryMarshal.Write(
            span[Unsafe.SizeOf<FdtFileHeader>()..],
            new FdtGlyphTableHeader { GlyphCount = chars.Length, KerningPairCount = 2 });
        "fthd"u8.CopyTo(span[Unsafe.SizeOf<FdtFileHeader>()..]);
        for (var i = 0; i < chars.Length; i++)
        {
            MemoryMarshal.Write(
                span[(glyphsOffset + (i * Unsafe.SizeOf<FdtGlyph>()))..],
                new FdtGlyph
                {
                    Rune = chars[i],
                    BoundingWidth = (byte)(i + 1),
                });
        }

        MemoryMarshal.Write(span[kerningOffset..], new FdtKerningPairTableHeader { Count = 2 });
        "knhd"u8.CopyTo(span[kerningOffset..]);

        // Sorted by left then right. Sorted by right first, (B, A) would come before (A, C).
        MemoryMarshal.Write(
            span[pairsOffset..],
            new FdtKerningPair { Left = 'A', Right = 'C', RightOffset = -2 });
        MemoryMarshal.Write(
            span[(pairsOffset + Unsafe.SizeOf<FdtKerningPair>())..],
            new FdtKerningPair { Left = 'B', Right = 'A', RightOffset = -3 });

        var view = new FdtFileView(data);
        for (var i = 0; i < chars.Length; i++)
        {
            Assert.True(view.TryGetGlyph(chars[i], out var glyph), $"U+{chars[i]:X4}");
            Assert.Equal(i + 1, glyph.BoundingWidth);
        }

        Assert.False(view.TryGetGlyph('C', out _));
        Assert.False(view.TryGetGlyph(0x110000, out _));
        Assert.Equal(-2, view.GetKerning('A', 'C'));
        Assert.Equal(-3, view.GetKerning('B', 'A'));
        Assert.Equal(0, view.GetKerning('C', 'A'));

        // Truncated tables are clamped, and truncated headers are rejected.
        Assert.Equal(1, new FdtFileView(data.AsMemory(..^1)).PairAdjustments.Length);
        Assert.Throws<InvalidDataException>(() => new FdtFileView(data.AsMemory(..16)));
    }

    [Fact]
    public void GameFontFamilyUsesNativeSizes()
    {
        Assert.SkipWhen(GameFontDir is null, "DALAMUD_TEST_GAMEFONT is not set");
        using var imgui = new ImGuiScope();

        var strikes = new[] { "AXIS_96", "AXIS_12", "AXIS_14", "AXIS_18", "AXIS_36" }
                      .Select(n => (new FdtFileView(File.ReadAllBytes(Path.Combine(GameFontDir!, n + ".fdt"))), n == "AXIS_36" ? -4 : -1))
                      .ToArray();
        var family = new GameFontFamilyFont("AXIS", strikes, LoadTexture);
        Assert.Contains(strikes, x => Math.Abs((x.Item1.GlyphTableHeader.Size * 4 / 3) - 16f) < 0.01f);

        var font = new CompositeFont("AXIS 18px", 18f);
        font.Add(new(family, Embolden: 0f));
        var fontPtr = imgui.SetDefaultFont(font, 18f);
        Assert.Equal('A', fontPtr.GetGlyphIndex('A'));

        imgui.Frame(() =>
        {
            ImGui.Begin("test");
            ImGui.Text("AXIS 18px: The quick brown fox");
            ImGui.PushFont(ImFontPtr.Null, 16f);
            ImGui.Text("AXIS 16px (native)");
            ImGui.PopFont();
            ImGui.End();
        });

        Assert.True(GlyphAtlas.Shared.GlyphCount >= 20, $"{GlyphAtlas.Shared.GlyphCount} glyphs");
        imgui.DumpPage("gamefont");
    }

    private static ComPtr<IWICImagingFactory> CreateWicFactory()
    {
        var factory = default(ComPtr<IWICImagingFactory>);
        var hr = Create();

        // Test runner threads may not have set COM up.
        if (hr == CO.CO_E_NOTINITIALIZED)
        {
            _ = TerraFX.Interop.Windows.Windows.CoInitializeEx(null, (uint)COINIT.COINIT_MULTITHREADED);
            hr = Create();
        }

        hr.ThrowOnError();
        return factory;

        HRESULT Create()
        {
            fixed (Guid* clsid = &CLSID.CLSID_WICImagingFactory)
            fixed (Guid* iid = &IID.IID_IWICImagingFactory)
            {
                return TerraFX.Interop.Windows.Windows.CoCreateInstance(
                    clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, iid, (void**)factory.GetAddressOf());
            }
        }
    }

    private static GameFontFamilyFont.Texture LoadTexture(int index)
    {
        // A .tex file is an 80-byte header (format +4, width +8, height +10), then the pixels.
        var data = File.ReadAllBytes(Path.Combine(GameFontDir!, $"font{index + 1}.tex"));
        var format = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
        return new(width, height, format == 0x1440, data[80..]);
    }

    // Runs on a thread of its own that counts as the main thread, which test threads otherwise do not.
    private static void RunOnMainThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            ThreadSafety.MarkMainThread();
            try
            {
                action();
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.Start();
        thread.Join();
        if (error is not null)
            ExceptionDispatchInfo.Throw(error);
    }

    /// <summary>An ImGui context with Dalamud's layouter and no renderer.</summary>
    internal sealed class ImGuiScope : IDisposable
    {
        private readonly ImGuiContextPtr context;
        private ImFontPtr font;

        public ImGuiScope()
        {
            this.context = ImGui.CreateContext();
            TextLayouter.Install();
            TextLayouter.ClearCache();

            var io = ImGui.GetIO();
            io.Handle->IniFilename = null;
            io.DisplaySize = new(800, 600);
            io.DeltaTime = 1f / 60;
            io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
        }

        public ImFontPtr SetDefaultFont(ManagedFont managedFont, float sizePx)
        {
            managedFont.AddRef();
            this.font = managedFont.NativePointer;
            ImGui.GetIO().Handle->FontDefault = this.font;
            ImGui.GetStyle().FontSizeBase = sizePx;
            return this.font;
        }

        public void Frame(Action build)
        {
            ImGui.NewFrame();
            build();
            ImGui.Render();

            // Stand in for the renderer.
            foreach (var tex in ImGui.GetPlatformIO().Textures)
            {
                if (tex.Status is ImTextureStatus.WantCreate or ImTextureStatus.WantUpdates)
                {
                    tex.SetTexID(new ImTextureID(tex.UniqueID));
                    tex.SetStatus(ImTextureStatus.Ok);
                }
            }
        }

        public void DumpPage(string name)
        {
            if (DumpDir is null)
                return;
            var atlas = GlyphAtlas.Shared;
            Gpu.Flush();
            foreach (var plane in (GlyphAtlas.Plane[])[atlas.Coverage, atlas.Color])
            {
                for (var i = 0; i < plane.SlotCount; i++)
                {
                    var texels = plane.Read(i);
                    var rgba = plane.Kind == GlyphAtlas.PlaneKind.Color ? texels : new byte[texels.Length * 4];
                    if (plane.Kind == GlyphAtlas.PlaneKind.Coverage)
                    {
                        for (var j = 0; j < texels.Length; j++)
                            rgba[(j * 4) + 3] = texels[j];
                    }

                    fixed (byte* pixels = rgba)
                    {
                        WriteBmp(
                            Path.Combine(DumpDir, $"{name}-{plane.Kind}{i}.bmp"),
                            GlyphAtlas.SliceSize,
                            GlyphAtlas.SliceSize,
                            pixels);
                    }
                }
            }
        }

        public void Dispose()
        {
            GlyphAtlas.DisposeShared();
            ImGui.DestroyContext(this.context);
            if (!this.font.IsNull)
                this.font.Release();
        }

        private static void WriteBmp(string path, int width, int height, byte* rgba)
        {
            using var f = File.Create(path);
            using var w = new BinaryWriter(f);
            w.Write((byte)'B');
            w.Write((byte)'M');
            w.Write(54 + (width * height * 4));
            w.Write(0);
            w.Write(54);
            w.Write(40);
            w.Write(width);
            w.Write(-height);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(0);
            w.Write(width * height * 4);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(0);

            // Each channel's coverage in its color, and alpha as white, on black.
            for (var i = 0; i < width * height; i++)
            {
                var a = rgba[(i * 4) + 3];
                w.Write(Math.Max(rgba[(i * 4) + 2], a));
                w.Write(Math.Max(rgba[(i * 4) + 1], a));
                w.Write(Math.Max(rgba[i * 4], a));
                w.Write((byte)255);
            }
        }
    }

    /// <summary>Records what it was asked to read, and shows the text as it is.</summary>
    private sealed class RecordingStyler : TextStyler
    {
        public (byte[] Text, ImTextMarkup Markup, float WrapWidth)? Asked { get; set; }

        public override ImTextMarkup Markup => ImTextMarkup.SeString;

        protected override void Parse(
            ManagedFont font, in TextLayoutDesc desc, in ImTextStyle style, ReadOnlySpan<byte> text,
            ITextMarkupCallback[] callbacks, bool nested, StyledText output)
        {
            this.Asked = (text.ToArray(), style.Markup, desc.WrapWidth);
            output.Source = text.ToArray();
            output.DefaultStyle = style with { Markup = ImTextMarkup.Plain };
            for (var i = 0; i < text.Length; i++)
                output.Append(text.Slice(i, 1), i);
        }
    }

    /// <summary>A WARP device, for the atlas to make its changes on, and to read them back from.</summary>
    private static class Gpu
    {
        private static readonly Lazy<(nint Device, nint Context)> Instance = new(Create);

        /// <summary>Makes the changes queued by the atlas.</summary>
        public static void Flush()
        {
            var (device, context) = Instance.Value;
            _ = GlyphAtlas.Shared.Upload((ID3D11Device*)device, (ID3D11DeviceContext*)context);
        }

        private static (nint Device, nint Context) Create()
        {
            ID3D11Device* device;
            ID3D11DeviceContext* context;
            DirectX.D3D11CreateDevice(
                    null,
                    D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP,
                    HMODULE.NULL,
                    0,
                    null,
                    0,
                    D3D11.D3D11_SDK_VERSION,
                    &device,
                    null,
                    &context)
                .ThrowOnError();
            return ((nint)device, (nint)context);
        }
    }
}
