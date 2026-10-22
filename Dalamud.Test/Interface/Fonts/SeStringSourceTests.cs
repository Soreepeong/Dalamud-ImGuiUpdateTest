using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Interface.Fonts.Internal.SeStrings;

using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

using Xunit;

namespace Dalamud.Test.Interface.Fonts;

/// <summary>Macro strings (ImTextMarkup.MacroString), compiled with the offset of each byte in the macro string.
/// </summary>
public class SeStringSourceTests
{
    [Fact]
    public void LiteralTextMapsByteForByte()
    {
        var (compiled, map) = Compile("abc"u8);
        Assert.Equal("abc"u8.ToArray(), compiled);
        Assert.Equal([0, 1, 2, 3], map);
    }

    [Fact]
    public void EscapesTakeTheNextCharacterAsIs()
    {
        // "\#\#" is "##", which ImGui would otherwise take as the start of an ID. "\<" is not a macro.
        var (compiled, map) = Compile(@"a\#\#\<b"u8);
        Assert.Equal("a##<b"u8.ToArray(), compiled);
        Assert.Equal([0, 1, 3, 5, 7, 8], map); // an escape is one character, from its backslash

        // A multibyte character after a backslash.
        (compiled, map) = Compile(@"\가!"u8);
        Assert.Equal("가!"u8.ToArray(), compiled);
        Assert.Equal([0, 0, 0, 4, 5], map);
    }

    [Fact]
    public void MacrosMapToWhereTheyStart()
    {
        var source = "x<italic(1)>y<italic(0)>"u8;
        var (compiled, map) = Compile(source);
        var payloads = new ReadOnlySeString(compiled).ToArray();
        Assert.Equal(4, payloads.Length);
        Assert.Equal(MacroCode.Italic, payloads[1].MacroCode);
        Assert.Equal(MacroCode.Italic, payloads[3].MacroCode);

        // "x", the payload of "<italic(1)>" from offset 1, "y" at 12, then "<italic(0)>" from 13.
        Assert.Equal(0, map[0]);
        Assert.All(map[1..^3], m => Assert.True(m is 1 or 12 or 13));
        Assert.Equal(12, map[1 + payloads[1].EnvelopeByteLength]);
        Assert.Equal(13, map[^2]);
        Assert.Equal(source.Length, map[^1]);
    }

    [Fact]
    public void NestedMacrosAndEscapedBracketsStayInTheirMacro()
    {
        // The link's argument has a macro inside, and an escaped '>' that does not end it.
        var source = @"<link(0x0E,0,0,0,<if(1,a,b)>\>)>t<link(0xCE)>"u8;
        var (compiled, map) = Compile(source);
        var payloads = new ReadOnlySeString(compiled).ToArray();
        Assert.Equal(MacroCode.Link, payloads[0].MacroCode);
        Assert.Equal(ReadOnlySePayloadType.Text, payloads[1].Type);
        Assert.Equal(MacroCode.Link, payloads[2].MacroCode);
        Assert.Equal(source.IndexOf("t<link(0xCE)>"u8), map[payloads[0].EnvelopeByteLength]);
    }

    private static (byte[] Compiled, int[] Map) Compile(ReadOnlySpan<byte> source)
    {
        var map = new List<int>();
        var compiled = MacroStringStyler.Compile(source, [], map);
        Assert.Equal(compiled.Length + 1, map.Count);
        return (compiled, map.ToArray());
    }
}
