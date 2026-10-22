using System;
using System.IO;
using System.Linq;
using System.Text;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Layout;
using Dalamud.Interface.Fonts.Internal.Sources;

using Xunit;

namespace Dalamud.Test.Interface.Fonts;

/// <summary>Shaping by the managed fonts, and how the layouter uses it. Layouts need no ImGui context.</summary>
[Collection("ImGui")]
public class ShapingTests
{
    private const float Size = 32f;

    private static readonly string FontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static readonly Lazy<DirectWriteFont> SegoeUi = new(() => Load("Segoe UI", "segoeui.ttf"));
    private static readonly Lazy<DirectWriteFont> Calibri = new(() => Load("Calibri", "calibri.ttf"));

    [Fact]
    public void DirectWriteFormsLigatures()
    {
        var glyphs = Shape(Calibri.Value, "fi");
        Assert.Single(glyphs);
        Assert.Equal(0, glyphs[0].Cluster);
    }

    [Fact]
    public void DirectWriteKerns()
    {
        var pair = Shape(SegoeUi.Value, "AV");
        var alone = Shape(SegoeUi.Value, "A");
        Assert.Equal(2, pair.Length);
        Assert.True(pair[0].Advance < alone[0].Advance, $"{pair[0].Advance} vs {alone[0].Advance}");
        Assert.True(SegoeUi.Value.TryGetPairKerning('A', 'V', Size, 1, out var kerning));
        Assert.Equal(pair[0].Advance - alone[0].Advance, kerning, 3);
    }

    [Fact]
    public void DirectWriteJoinsArabic()
    {
        const string text = "سلام"; // salam
        var shaped = Shape(SegoeUi.Value, text);
        var nominal = new uint[text.Length];
        SegoeUi.Value.GetGlyphIndices(text.Select(c => (uint)c).ToArray(), nominal);
        Assert.Contains(shaped, g => !nominal.Contains(g.Glyph)); // contextual forms, not the isolated ones
    }

    [Fact]
    public void CompositeKeepsGraphemesInTheFontOfTheirFirstCodepoint()
    {
        // U+0301 is outside the first element's ranges, but goes with its base.
        var font = new CompositeFont("test", Size);
        font.Add(new(SegoeUi.Value, CompositeFont.ToRanges([0x20, 0x7E, 0])));
        font.Add(new(Load("Malgun Gothic", "malgun.ttf")));
        var glyphs = Shape(font, "é́");
        Assert.Equal(3, glyphs.Length);
        Assert.All(glyphs, g => Assert.Equal(0u, g.Glyph >> 24));
        Assert.All(glyphs, g => Assert.Equal(0, g.Cluster));
    }

    [Fact]
    public void CompositeKernsAcrossElements()
    {
        // 'A' and 'V' from different elements (the same font) are kerned as within one.
        var font = new CompositeFont("test", Size);
        font.Add(new(SegoeUi.Value, CompositeFont.ToRanges(['A', 'A', 0])));
        font.Add(new(Load("Segoe UI 2", "segoeui.ttf")));
        var glyphs = Shape(font, "AV");
        Assert.Equal(0u, glyphs[0].Glyph >> 24);
        Assert.Equal(1u, glyphs[1].Glyph >> 24);
        Assert.Equal(Shape(SegoeUi.Value, "AV")[0].Advance, glyphs[0].Advance, 3);
    }

    [Fact]
    public void LayouterMovesByGrapheme()
    {
        const string text = "éx\U0001F468\u200D\U0001F469\u200D\U0001F467";
        var layout = Layout(SegoeUi.Value, text);
        var caret = layout.GetCaret(0);
        caret = layout.Move(caret, ImTextMoveFlags.Grapheme, 1, 0);
        Assert.Equal(3, caret.TextOffset); // e + U+0301 (2 bytes)
        caret = layout.Move(caret, ImTextMoveFlags.Grapheme, 1, 0);
        Assert.Equal(4, caret.TextOffset);
        caret = layout.Move(caret, ImTextMoveFlags.Grapheme, 1, 0);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), caret.TextOffset); // the family emoji is one grapheme
        caret = layout.Move(caret, ImTextMoveFlags.Grapheme, -1, 0);
        Assert.Equal(4, caret.TextOffset);

        // By codepoint (the default unit), the caret moves into the grapheme.
        Assert.Equal(1, layout.Move(layout.GetCaret(0), ImTextMoveFlags.Codepoint, 1, 0).TextOffset);
        Assert.Equal(3, layout.Move(layout.GetCaret(0), ImTextMoveFlags.Codepoint, 2, 0).TextOffset);
        Assert.Equal(1, layout.Move(layout.GetCaret(3), ImTextMoveFlags.Codepoint, -1, 0).TextOffset);
    }

    [Fact]
    public void LayouterDividesLigaturesForCarets()
    {
        var layout = Layout(Calibri.Value, "fi");
        var width = layout.Size.X;
        Assert.Equal(width / 2, layout.GetCaret(1).Pos.X, 3);
        Assert.Equal(1, layout.HitTest(new(width * 0.4f, 1)).TextOffset);
    }

    [Fact]
    public void LayouterGivesGraphemesTheRunOfTheirFirstCodepoint()
    {
        var layout = Layout(
            SegoeUi.Value,
            "é",
            [new(0, 1, UserData: 1), new(1, 3, Style: new ImTextStyle { Color = 0xFF0000FF }, UserData: 2)]);
        Assert.Equal(1, layout.GetRunRects(0).Length);
        Assert.Equal(0, layout.GetRunRects(1).Length); // the combining mark only
    }

    [Theory]
    [InlineData("Segoe UI", "segoeui.ttf")]
    [InlineData("Malgun Gothic", "malgun.ttf")]
    public void CharactersNoFontHasAreDrawnAsTheGameDoes(string name, string file)
    {
        // U+3013 GETA MARK if the font has it, else a hyphen. A private use character is in neither font.
        var font = Load(name, file);
        Span<uint> glyph = stackalloc uint[1];
        font.GetGlyphIndices([0x3013], glyph);
        var expected = glyph[0] != 0 ? "\u3013" : "-";
        Assert.Equal(Layout(font, expected).Size.X, Layout(font, "").Size.X, 3);
        Assert.Equal(Layout(font, "a" + expected + "b").Size.X, Layout(font, "ab").Size.X, 3);
    }

    [Fact]
    public void LayoutsFromThePoolAreAsNewOnes()
    {
        // A longer text with runs first, so that the arrays reused are longer than the next needs.
        var font = SegoeUi.Value;
        var runs = new TextRun[] { new(0, 5, Style: new ImTextStyle { Color = 0xFF0000FF }, UserData: 3) };
        var desc = new TextLayoutDesc(Size, WrapWidth: Size * 6);
        var longer = TextLayout.Rent(font, desc, default, "Hello, world, and more text\nwith lines"u8, 1, runs, []);
        longer.Recycle();

        var text = "ab cd\nef"u8;
        var pooled = TextLayout.Rent(font, desc, default, text, 1, [], []);
        var fresh = new TextLayout(font, desc, default, text, 1, [], []);
        Assert.Same(longer, pooled);
        Assert.Equal(fresh.Size, pooled.Size);
        Assert.Equal(fresh.Ink, pooled.Ink);
        Assert.Equal(fresh.TextLength, pooled.TextLength);
        Assert.Equal(fresh.RunCount, pooled.RunCount);
        Assert.Equal(0, pooled.RunCount);
        Assert.Equal(fresh.Lines.ToArray(), pooled.Lines.ToArray());
        Assert.True(pooled.GetRunRects(0).IsEmpty);
        for (var offset = 0; offset <= text.Length; offset++)
            Assert.Equal(fresh.GetCaret(offset), pooled.GetCaret(offset));
    }

    [Fact]
    public void LayouterReportsOffsetsIntoTheSourceText()
    {
        // "a", 5 bytes that show nothing (such as a SeString payload), then "b" in a run of its own.
        var layout = new TextLayout(
            SegoeUi.Value,
            new(Size),
            default,
            "ab"u8,
            1,
            [new(1, 2, Style: new ImTextStyle { Color = 0xFF0000FF }, UserData: 9)],
            [],
            [0, 6, 7]);
        Assert.Equal(7, layout.TextLength);
        Assert.Equal(7, layout.Lines[0].TextEnd);

        // The hidden bytes belong to "a". Carets are at 0, 6 and 7, and offsets between round down.
        var width = layout.Size.X;
        Assert.Equal(6, layout.HitTest(new(width * 0.55f, 1)).TextOffset);
        Assert.Equal(7, layout.HitTest(new(width, 1)).TextOffset);
        Assert.True(layout.GetCaret(6).Pos.X > 0);
        Assert.Equal(0, layout.GetCaret(3).Pos.X);
        Assert.Equal(9ul, layout.HitTest(new(width * 0.9f, 1)).UserData);
    }

    [Fact]
    public void LayouterKernsAcrossRunsButDoesNotLigate()
    {
        TextRun[] split = [new(0, 1, Style: new ImTextStyle { Color = 0xFF0000FF }), new(1, 2)];

        // Kerning stays across a style change.
        var together = Layout(SegoeUi.Value, "AV").Size.X;
        var apart = Layout(SegoeUi.Value, "AV", split).Size.X;
        var a = Layout(SegoeUi.Value, "A").Size.X;
        var v = Layout(SegoeUi.Value, "V").Size.X;
        Assert.True(together < a + v, $"{together} vs {a} + {v}");
        Assert.Equal(together, apart, 3);

        // A ligature does not.
        var ligature = Layout(Calibri.Value, "fi").Size.X;
        apart = Layout(Calibri.Value, "fi", split).Size.X;
        var f = Layout(Calibri.Value, "f").Size.X;
        var i = Layout(Calibri.Value, "i").Size.X;
        Assert.Equal(f + i, apart, 2);
        Assert.NotEqual(ligature, apart, 2);
    }

    [Fact]
    public void LayouterReshapesWhereABreakIsUnsafe()
    {
        // A word too long for the line breaks inside. Arabic joining is unsafe to break, so each line is shaped
        // without the other. Each is as wide as its text alone.
        const string word = "سلامسلامسلامسلام";
        var full = Layout(SegoeUi.Value, word).Size.X;
        var wrapped = Layout(SegoeUi.Value, word, wrapWidth: full * 0.6f);
        Assert.Equal(2, wrapped.Lines.Length);
        var bytes = Encoding.UTF8.GetBytes(word);
        foreach (var line in wrapped.Lines)
        {
            var alone = Layout(SegoeUi.Value, Encoding.UTF8.GetString(bytes[line.TextBegin..line.TextEnd]));
            Assert.Equal(alone.Size.X, line.Size.X, 2);
        }
    }

    [Fact]
    public void LayouterBreaksLinesWhereUax14Allows()
    {
        // After a hyphen, but not between the letters of a word.
        var full = Layout(SegoeUi.Value, "aaaa-bbbb").Size.X;
        var wrapped = Layout(SegoeUi.Value, "aaaa-bbbb", wrapWidth: full * 0.8f);
        Assert.Equal(2, wrapped.Lines.Length);
        Assert.Equal(5, wrapped.Lines[1].TextBegin);

        // Before an opening parenthesis after a space, never after it.
        full = Layout(SegoeUi.Value, "aaaa (bbbb").Size.X;
        wrapped = Layout(SegoeUi.Value, "aaaa (bbbb", wrapWidth: full * 0.8f);
        Assert.Equal(2, wrapped.Lines.Length);
        Assert.Equal(5, wrapped.Lines[1].TextBegin);
    }

    [Fact]
    public void LayouterReadsAllEncodings()
    {
        const string text = "A\U0001F600B";
        var utf8 = Layout(SegoeUi.Value, text);
        var utf16 = TextLayouter.Create(SegoeUi.Value, new(Size), default, Encoding.Unicode.GetBytes(text), 2);
        var utf32 = TextLayouter.Create(SegoeUi.Value, new(Size), default, Encoding.UTF32.GetBytes(text), 4);
        Assert.Equal(utf8.Size, utf16.Size);
        Assert.Equal(utf8.Size, utf32.Size);
        Assert.Equal(6, utf16.Move(utf16.GetCaret(2), ImTextMoveFlags.Grapheme, 1, 0).TextOffset); // the surrogate pair
        Assert.Equal(8, utf32.Move(utf32.GetCaret(4), ImTextMoveFlags.Grapheme, 1, 0).TextOffset);
    }

    [Fact]
    public void LayouterStacksVerticalColumnsRightToLeft()
    {
        // One em per grapheme along y. A second column (after the line break) is to the left of the first.
        var desc = new TextLayoutDesc(Size, Flags: ImTextLayoutFlags.Vertical);
        var layout = TextLayouter.Create(SegoeUi.Value, desc, default, "ab\ncde"u8);
        Assert.Equal(2, layout.Lines.Length);
        Assert.Equal(new(Size * 2, Size * 3), layout.Size);
        Assert.Equal(new(Size, 0), layout.Lines[0].Pos);
        Assert.Equal(new(0, 0), layout.Lines[1].Pos);

        var caret = layout.GetCaret(1);
        Assert.Equal(new(Size, Size), caret.Pos);
        Assert.Equal(Size, caret.InlinePos);
        Assert.Equal(Size, caret.Length);

        // The second grapheme of the second column is 'd'.
        var hit = layout.HitTest(new(Size * 0.5f, Size * 1.2f));
        Assert.Equal(1, hit.LineIndex);
        Assert.Equal(4, hit.TextOffset);
        Assert.Equal(5, layout.Move(hit, ImTextMoveFlags.Grapheme, 1, 0).TextOffset);
        Assert.Equal(1, layout.Move(hit, ImTextMoveFlags.BlockRelative, 0, -1).TextOffset);

        // Wrapping along y.
        var wrapped = TextLayouter.Create(SegoeUi.Value, desc with { WrapWidth = Size * 2.5f }, default, "aaaa"u8);
        Assert.Equal(new(Size * 2, Size * 2), wrapped.Size);
    }

    [Fact]
    public void LayouterGivesRangeRectsPerLine()
    {
        var full = Layout(SegoeUi.Value, "aaaa bbbb").Size.X;
        var wrapped = Layout(SegoeUi.Value, "aaaa bbbb", wrapWidth: full * 0.8f);
        Span<System.Numerics.Vector4> rects = stackalloc System.Numerics.Vector4[4];
        Assert.Equal(2, wrapped.GetRangeRects(2, 7, rects));
        Assert.Equal(wrapped.GetCaret(2).Pos.X, rects[0].X, 3);
        Assert.Equal(0, rects[1].X);
        Assert.Equal(wrapped.GetCaret(7).Pos.X, rects[1].Z, 3);
        Assert.True(rects[1].Y >= rects[0].W - 0.001f);
        Assert.Equal(full * 0.8f, rects[0].Z, 3); // continues past the wrap point to the wrap width
        Assert.Equal(1, wrapped.GetRangeRects(0, 1, rects));

        // Through line breaks, rectangles extend to the layout box. The end of the text is not extended.
        var lines = Layout(SegoeUi.Value, "aaa\nbbbbbbb\nccc");
        Assert.Equal(3, lines.GetRangeRects(1, 13, rects));
        Assert.Equal(lines.GetCaret(1).Pos.X, rects[0].X, 3);
        Assert.Equal(lines.Size.X, rects[0].Z, 3);
        Assert.Equal(0, rects[1].X);
        Assert.Equal(lines.Size.X, rects[1].Z, 3);
        Assert.Equal(lines.GetCaret(13).Pos.X, rects[2].Z, 3);
        Assert.Equal(1, lines.GetRangeRects(12, 15, rects));
        Assert.Equal(lines.GetCaret(15).Pos.X, rects[0].Z, 3);

        // Blanks hanging past the wrap width count with WrapKeepBlanks only.
        var aaaa = Layout(SegoeUi.Value, "aaaa").Size.X;
        var hanging = Encoding.UTF8.GetBytes("aaaa        bbbb");
        var desc = new TextLayoutDesc(Size, WrapWidth: aaaa * 1.2f);
        var dropped = TextLayouter.Create(SegoeUi.Value, desc, default, hanging);
        Assert.Equal(2, dropped.GetRangeRects(0, 14, rects));
        Assert.Equal(aaaa * 1.2f, rects[0].Z, 3);
        var kept = TextLayouter.Create(SegoeUi.Value, desc with { Flags = ImTextLayoutFlags.WrapKeepBlanks }, default, hanging);
        Assert.Equal(2, kept.GetRangeRects(0, 14, rects));
        Assert.True(rects[0].Z > aaaa * 1.5f);
    }

    [Fact]
    public void LayouterReadsLineHeightAsPixelsOrScale()
    {
        Assert.Equal(Size, TextLayouter.Create(SegoeUi.Value, new(Size), default, "a"u8).Size.Y);
        Assert.Equal(Size * 1.5f, TextLayouter.Create(SegoeUi.Value, new(Size, LineHeight: -1.5f), default, "a"u8).Size.Y);
        Assert.Equal(40f, TextLayouter.Create(SegoeUi.Value, new(Size, LineHeight: 40), default, "a"u8).Size.Y);
    }

    [Fact]
    public void LayouterMasksWithTheMaskChar()
    {
        var star = TextLayouter.Create(SegoeUi.Value, new(Size, Flags: ImTextLayoutFlags.PasswordMask), default, "abc"u8);
        var dot = TextLayouter.Create(SegoeUi.Value, new(Size, Flags: ImTextLayoutFlags.PasswordMask, MaskChar: 0x25CF), default, "abc"u8);
        Assert.Equal(Layout(SegoeUi.Value, "***").Size.X, star.Size.X, 3);
        Assert.Equal(Layout(SegoeUi.Value, "●●●").Size.X, dot.Size.X, 3);
    }

    [Fact]
    public void LayouterStopsAtMaxWidthWithoutEllipsis()
    {
        var full = Layout(SegoeUi.Value, "aaaaaaaaaa").Size.X;
        var layout = TextLayouter.Create(SegoeUi.Value, new(Size, MaxWidth: full / 2), default, "aaaaaaaaaa"u8);
        Assert.Single(layout.Lines.ToArray());
        Assert.InRange(layout.TextLength, 1, 9);
        Assert.Equal(-1, layout.EllipsisOffset);
        Assert.True(layout.Size.X <= full / 2 + 0.001f, $"{layout.Size.X} vs {full / 2}");
    }

    [Fact]
    public void LayouterTruncatesWithEllipsis()
    {
        var full = Layout(SegoeUi.Value, "aaaaaaaaaa").Size.X;
        var desc = new TextLayoutDesc(Size, MaxWidth: full / 2, Flags: ImTextLayoutFlags.Ellipsis);
        var layout = TextLayouter.Create(SegoeUi.Value, desc, default, "aaaaaaaaaa"u8);
        Assert.Single(layout.Lines.ToArray());
        Assert.InRange(layout.EllipsisOffset, 1, 9);
        Assert.Equal(layout.EllipsisOffset, layout.Lines[0].TextEnd);
        Assert.Equal(10, layout.TextLength);
        Assert.True(layout.Size.X <= full / 2 + 0.001f, $"{layout.Size.X} vs {full / 2}");

        // As wide as the text kept and the ellipsis after it.
        var kept = new string('a', layout.EllipsisOffset) + "…";
        Assert.Equal(Layout(SegoeUi.Value, kept).Size.X, layout.Size.X, 2);

        // Text that fits is left alone.
        var fits = TextLayouter.Create(SegoeUi.Value, desc with { MaxWidth = full * 2 }, default, "aaaaaaaaaa"u8);
        Assert.Equal(-1, fits.EllipsisOffset);
        Assert.Equal(full, fits.Size.X, 3);
    }

    [Fact]
    public void LayouterTruncatesEachWrappedLine()
    {
        var aaaa = Layout(SegoeUi.Value, "aaaaaaaa").Size.X;
        var text = "aaaaaaaa aaaaaaaa aaaaaaaa"u8;
        var desc = new TextLayoutDesc(Size, WrapWidth: aaaa * 1.1f, MaxWidth: aaaa / 2, Flags: ImTextLayoutFlags.Ellipsis);
        var layout = TextLayouter.Create(SegoeUi.Value, desc, default, text);
        Assert.Equal(3, layout.Lines.Length);
        Assert.InRange(layout.EllipsisOffset, 1, 7);
        Assert.Equal(9, layout.Lines[1].TextBegin);
        Assert.Equal(18, layout.Lines[2].TextBegin);
        foreach (var line in layout.Lines)
        {
            Assert.True(line.Size.X <= aaaa / 2 + 0.001f, $"{line.Size.X} vs {aaaa / 2}");
            Assert.True(line.TextEnd < line.TextNext);
        }
    }

    [Fact]
    public void LayouterShowsAnEllipsisWiderThanTheBox()
    {
        var desc = new TextLayoutDesc(Size, MaxWidth: 1, Flags: ImTextLayoutFlags.Ellipsis);
        var layout = TextLayouter.Create(SegoeUi.Value, desc, default, "abc"u8);
        Assert.Equal(0, layout.EllipsisOffset);
        Assert.Equal(0, layout.Lines[0].TextEnd);
        Assert.Equal(Layout(SegoeUi.Value, "…").Size.X, layout.Size.X, 2);
    }

    [Fact]
    public void LayouterTruncatesAtWholeGraphemesAndLigatures()
    {
        // "fi" is one ligature in Calibri, and e with U+0301 one grapheme of three bytes.
        var fi = Layout(Calibri.Value, "fififififi").Size.X;
        var desc = new TextLayoutDesc(Size, MaxWidth: fi * 0.55f, Flags: ImTextLayoutFlags.Ellipsis);
        var ligatures = TextLayouter.Create(Calibri.Value, desc, default, "fififififi"u8);
        Assert.InRange(ligatures.EllipsisOffset, 2, 8);
        Assert.Equal(0, ligatures.EllipsisOffset % 2);

        var accented = string.Concat(Enumerable.Repeat("é", 10));
        var width = Layout(SegoeUi.Value, accented).Size.X;
        var graphemes = TextLayouter.Create(
            SegoeUi.Value,
            desc with { MaxWidth = width * 0.55f },
            default,
            Encoding.UTF8.GetBytes(accented));
        Assert.InRange(graphemes.EllipsisOffset, 3, 27);
        Assert.Equal(0, graphemes.EllipsisOffset % 3);
    }

    private static DirectWriteFont Load(string name, string file) =>
        new(name, File.ReadAllBytes(Path.Combine(FontsDir, file)), 0);

    private static TextLayout Layout(ManagedFont font, string text, TextRun[]? runs = null, float wrapWidth = 0) =>
        TextLayouter.Create(font, new(Size, WrapWidth: wrapWidth), default, Encoding.UTF8.GetBytes(text), 1, runs);

    private static ShapedGlyph[] Shape(ManagedFont font, string text)
    {
        var cps = text.EnumerateRunes().Select(r => (uint)r.Value).ToArray();
        var output = new ShapedGlyphList();
        font.Shape(new(cps, 0, cps.Length, Size, 1), output);
        return output.Span.ToArray();
    }
}
