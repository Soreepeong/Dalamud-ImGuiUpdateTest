using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Dalamud.Configuration.Internal;
using Dalamud.Interface.Fonts;
using Dalamud.Interface.Fonts.Internal;
using Dalamud.Interface.Fonts.Internal.Sources;

using Newtonsoft.Json;

using Xunit;

namespace Dalamud.Test.Interface.Fonts;

/// <summary>Font specs, and the fonts made from them with font files only, on a factory without services. The paths
/// used here only need its font caches, and nothing here uses the default font.</summary>
public class FontSpecTests
{
    private static readonly string FontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    private static readonly string Segoe = Path.Combine(FontsDir, "segoeui.ttf");
    private static readonly string Malgun = Path.Combine(FontsDir, "malgun.ttf");
    private static readonly string Consola = Path.Combine(FontsDir, "consola.ttf");

    [Fact]
    public void SpecsConvertUnits()
    {
        var fonts = CreateFactory();

        // At 20px, the part of size 0 has its lengths in ems. The 30px part is 1.5x, with its offset in pixels at its
        // own size.
        var a = fonts.CreateFont(
            new MultiFontSpec
            {
                SizePx = 20,
                FallBackToDefaultFont = false,
                Parts =
                [
                    new SingleFontSpec { FontId = new FileFontId(Segoe), SizePx = 0, GlyphOffset = new(0, 0.1f), LetterSpacing = 0.05f },
                    new SingleFontSpec { FontId = new FileFontId(Malgun), SizePx = 30, GlyphOffset = new(0, 3) },
                ],
            });
        Assert.Equal(20f, a.PushSize);
        Assert.Equal(2f, a.Elements[0].Offset.Y, 0.001f);
        Assert.Equal(1f, a.Elements[0].ExtraAdvanceX, 0.001f);
        Assert.Equal(1.5f, a.Elements[1].SizeRatio, 0.001f);
        Assert.Equal(3f, a.Elements[1].Offset.Y, 0.001f);

        // Following the current size, lengths are in ems, and the part at -2 is twice the font's size. Pixels are at
        // the default size (16px in these tests), so 30px of 1.25 x 16px is 1.5x. A nested multi spec at -2 doubles
        // its parts.
        var r = fonts.CreateFont(
            new MultiFontSpec
            {
                SizePx = -1.25f,
                FallBackToDefaultFont = false,
                Parts =
                [
                    new SingleFontSpec { FontId = new FileFontId(Segoe), SizePx = 0, GlyphOffset = new(0, 0.1f) },
                    new SingleFontSpec { FontId = new FileFontId(Malgun), SizePx = -2, GlyphOffset = new(0, 0.1f) },
                    new SingleFontSpec { FontId = new FileFontId(Consola), SizePx = 30 },
                    new MultiFontSpec
                    {
                        SizePx = -2,
                        FallBackToDefaultFont = false,
                        Parts = [new SingleFontSpec { FontId = new FileFontId(Consola), SizePx = -1.5f }],
                    },
                ],
            });
        Assert.True(r.IsRelative);
        Assert.Equal(1.25f, r.RelativeSize);
        Assert.Equal(-1.25f, r.PushSize);
        Assert.Equal(0.1f, r.Elements[0].Offset.Y, 0.001f);
        Assert.Equal(2f, r.Elements[1].SizeRatio);
        Assert.Equal(0.2f, r.Elements[1].Offset.Y, 0.001f); // 0.1 em of a font twice as large
        Assert.Equal(1.5f, r.Elements[2].SizeRatio, 0.001f);
        Assert.Equal(3f, r.Elements[3].SizeRatio, 0.001f);

        // A single font spec has its lengths in pixels at its size. A size of 0 means 100%.
        var single = fonts.CreateFont(new SingleFontSpec { FontId = new FileFontId(Segoe), SizePx = 20, GlyphOffset = new(0, 2) });
        Assert.Equal(2f, single.Elements[0].Offset.Y, 0.001f);
        Assert.Equal(1f, fonts.CreateFont(new SingleFontSpec { FontId = new FileFontId(Segoe), SizePx = 0 }).RelativeSize);

        // Sources are shared.
        Assert.Same(a.Elements[0].Font, r.Elements[0].Font);
    }

    [Fact]
    public void SpecsCompareByValue()
    {
        SingleFontSpec Single(ImmutableArray<uint>? ranges) =>
            new() { FontId = new FileFontId(Segoe), CodepointRanges = ranges };

        Assert.Equal(Single([0x20, 0x7E]), Single([0x20, 0x7E]));
        Assert.Equal(Single([0x20, 0x7E]).GetHashCode(), Single([0x20, 0x7E]).GetHashCode());
        Assert.NotEqual(Single([0x20, 0x7F]), Single([0x20, 0x7E]));
        Assert.NotEqual(Single(null), Single([]));

        MultiFontSpec Multi(ImmutableArray<uint>? ranges) =>
            FontSpec.From([Single(ranges), new FileInfo(Malgun)], sizePx: 16);

        Assert.Equal(Multi([0x20, 0x7E]), Multi([0x20, 0x7E]));
        Assert.Equal(Multi([0x20, 0x7E]).GetHashCode(), Multi([0x20, 0x7E]).GetHashCode());
        Assert.NotEqual(Multi([0x20, 0x7F]), Multi([0x20, 0x7E]));
        Assert.Equal(new MultiFontSpec(), new MultiFontSpec { Parts = default });
    }

    [Fact]
    public void SpecsRoundTripThroughJson()
    {
        // As the configuration saves them.
        var settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.All,
            SerializationBinder = MovedTypeSerializationBinder.Instance,
        };
        T RoundTrip<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value, settings), settings)!;

        var single = new SingleFontSpec { FontId = new FileFontId(Segoe), SizePx = 20, CodepointRanges = [0x20, 0x1F600] };
        Assert.Equal(single, RoundTrip<IFontSpec>(single));
        Assert.DoesNotContain("\"GlyphRanges\"", JsonConvert.SerializeObject(single, settings));

        var all = new SingleFontSpec { FontId = new FileFontId(Segoe) };
        Assert.Null(((SingleFontSpec)RoundTrip<IFontSpec>(all)).CodepointRanges);

        var multi = FontSpec.From([single, FontSpec.From(new FileInfo(Malgun), sizePx: -1.5f)], sizePx: 20);
        Assert.Equal(multi, RoundTrip<IFontSpec>(multi));

        // Ids with internal constructors, and installed fonts by name.
        Assert.Equal(FontSpec.Mono, RoundTrip<IFontSpec>(FontSpec.Mono));
        var named = FontSpec.From([("Segoe UI", FontStyle.Bold | FontStyle.Italic), GameFontFamily.Axis]);
        Assert.Equal(named, RoundTrip<IFontSpec>(named));

        // Saved when font specs were in Dalamud.Interface.FontIdentifier.
        var game = new SingleFontSpec { FontId = new GameFontAndFamilyId(GameFontFamily.Axis), SizePx = 18 };
        var oldNames = JsonConvert.SerializeObject(game, settings)
                                  .Replace("Dalamud.Interface.Fonts.", "Dalamud.Interface.FontIdentifier.");
        Assert.Contains("Dalamud.Interface.FontIdentifier.SingleFontSpec", oldNames);
        Assert.Equal(game, JsonConvert.DeserializeObject<IFontSpec>(oldNames, settings));

        // Installed fonts saved with the weight, stretch and style as numbers.
        var face = Assert.IsType<SystemFontId>(FontSpec.From("Segoe UI", FontStyle.Bold | FontStyle.Italic).FontId);
        var faceJson = JsonConvert.SerializeObject(face, settings);
        Assert.Contains("\"FaceStyle\"", faceJson);
        var oldFace = JsonConvert.DeserializeObject<IFontId>(
            System.Text.RegularExpressions.Regex.Replace(
                faceJson,
                "\"FaceStyle\":\\{[^}]*\\}",
                "\"Weight\":700,\"Stretch\":5,\"Style\":2"),
            settings);
        Assert.Equal(face, oldFace);

        // Saved by older versions (ushort), or by versions with codepoint glyph ranges.
        var json = JsonConvert.SerializeObject(all, settings);
        foreach (var ranges in new[] { "[32,126,0]", "[32,128512,0]" })
        {
            var saved = json.Replace("\"CodepointRanges\":null", $"\"GlyphRanges\":{ranges}");
            Assert.Contains("GlyphRanges", saved);
            var read = JsonConvert.DeserializeObject<SingleFontSpec>(saved, settings)!;
            Assert.Equal(JsonConvert.DeserializeObject<uint[]>(ranges), read.CodepointRanges!.Value);
        }
    }

    [Fact]
    public void GameFontsHaveFamiliesAndSizes()
    {
        Assert.Equal(GameFontFamily.JupiterNumeric, GameFontFamilyAndSize.Jupiter45.Family);
        Assert.Equal(GameFontFamily.Undefined, GameFontFamilyAndSize.Undefined.Family);
        Assert.Equal(GameFontFamilyAndSize.TrumpGothic184, GameFontFamily.TrumpGothic.FamilyWithMinimumSize);
        Assert.Equal(18.4f, GameFontFamilyAndSize.TrumpGothic184.BaseSizePt);
        Assert.Equal(24f, GameFontFamilyAndSize.Axis18.BaseSizePx);
        Assert.Equal(GameFontFamilyAndSize.Axis18, GameFontFamily.Axis.GetRecommendedFamilyAndSize(16));
        Assert.Equal(GameFontFamilyAndSize.Axis36, GameFontFamily.Axis.GetRecommendedFamilyAndSize(100));
        Assert.Equal(GameFontFamilyAndSize.Undefined, GameFontFamily.Axis.GetRecommendedFamilyAndSize(0));
        Assert.Equal(GameFontFamilyAndSize.Undefined, GameFontFamily.Undefined.FamilyWithMinimumSize);
        Assert.Equal(0f, GameFontFamilyAndSize.Undefined.BaseSizePx);
        Assert.Throws<ArgumentOutOfRangeException>(() => ((GameFontFamilyAndSize)999).Family);

        // A game font at one of its sizes. Bold emboldens by 1/24 em (1px at 24px), and nothing else applies.
        var axis18 = FontSpec.From(GameFontFamilyAndSize.Axis18);
        Assert.Equal((GameFontFamily.Axis, 24f, 0f), (((GameFontAndFamilyId)axis18.FontId).GameFontFamily, axis18.SizePx, axis18.Weight));
        Assert.Equal(1f, FontSpec.From(GameFontFamily.Axis, FontStyle.Bold, 24).Weight, 0.001f);
        Assert.Equal(1f / 24, FontSpec.From(GameFontFamily.Axis, FontStyle.Bold).Weight, 0.0001f);
        Assert.Equal(0f, FontSpec.From(GameFontFamily.Axis, FontStyle.Light | FontStyle.Italic, 24).Weight);
        var part = FontSpec.From([(GameFontFamily.TrumpGothic, FontStyle.Bold)]).Parts[0];
        Assert.Equal(FontSpec.From(GameFontFamily.TrumpGothic, FontStyle.Bold), part);
    }

    [Fact]
    public void StylesCombine()
    {
        // The right side wins where it says something.
        Assert.Equal(new FontStyle(700, true), FontStyle.Bold | FontStyle.Italic);
        Assert.Equal(new FontStyle(300, true, 3), FontStyle.Bold | FontStyle.Italic | FontStyle.Condensed | FontStyle.Light);
        Assert.Equal(FontStyle.Bold, FontStyle.Default | FontStyle.Bold);
        Assert.Equal(FontStyle.Bold, FontStyle.Bold | FontStyle.Default);
        Assert.Equal("Condensed Bold Italic", (FontStyle.Condensed | FontStyle.Bold | FontStyle.Italic).ToString());
        Assert.Equal("Regular", FontStyle.Default.ToString());

        Assert.Throws<ArgumentOutOfRangeException>(() => new FontStyle(1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FontStyle(stretch: 10));

        // Saved and loaded as it is.
        var roundTrip = JsonConvert.SerializeObject(FontStyle.Bold | FontStyle.Italic);
        Assert.Equal(FontStyle.Bold | FontStyle.Italic, JsonConvert.DeserializeObject<FontStyle>(roundTrip));
    }

    [Fact]
    public void InterfaceTypedValuesBecomePartsByConstructor()
    {
        IFontSpec mono = FontSpec.Mono;
        IFontId game = FontSpec.From(GameFontFamily.Axis).FontId;
        IFontFamilyId family = IFontFamilyId.ListGameFonts()[0];
        var concrete = (GameFontAndFamilyId)game;

        // A game font id is both a font and a family. Without a style, the overload priority picks the font.
        var spec = FontSpec.From([new(mono), new(game), new(family, FontStyle.Bold), new(concrete), new(family)]);
        Assert.Same(FontSpec.Mono, spec.Parts[0]);
        Assert.Equal(FontSpec.From(GameFontFamily.Axis), spec.Parts[1]);
        Assert.Equal(FontSpec.From(family, FontStyle.Bold), spec.Parts[2]);
        Assert.Equal(spec.Parts[1], spec.Parts[3]);
        Assert.Equal(FontSpec.From(family), spec.Parts[4]);
        Assert.Equal(FontSpec.From(concrete), FontSpec.From(concrete, sizePx: 0));

        // A family that is not a font goes to the family overload, with or without a style.
        var segoe = new SystemFontFamilyId("Segoe UI");
        Assert.Equal(FontSpec.From("Segoe UI"), FontSpec.From(segoe));
        Assert.Equal(FontSpec.From("Segoe UI"), new FontSpec.FontSource(segoe).Spec);
        Assert.Throws<ArgumentNullException>(() => new FontSpec.FontSource((IFontSpec)null!));

        // [new(...)] and [] fit ReadOnlySpan<byte> too. The overload priority picks the several-font overload.
        Assert.Empty(FontSpec.From([]).Parts);
    }

    [Fact]
    public void SourcesBecomeParts()
    {
        var spec = FontSpec.From(
            [
                "Segoe UI",
                ("Segoe UI", FontStyle.Bold),
                GameFontFamily.Axis,
                GameFontFamilyAndSize.Axis18,
                new FileInfo(Consola),
                (new FileInfo(Malgun), 1),
                FontSpec.Icon,
            ],
            sizePx: 20,
            lineHeight: 1.2f,
            fallback: false);
        Assert.Equal(20f, spec.SizePx);
        Assert.Equal(1.2f, spec.LineHeight);
        Assert.False(spec.FallBackToDefaultFont);
        Assert.Equal(7, spec.Parts.Length);

        // A font by name is looked up when the font loads, in the face closest to the style.
        var bold = Assert.IsType<SingleFontSpec>(spec.Parts[1]);
        var boldId = Assert.IsType<SystemFontId>(bold.FontId);
        Assert.Equal("Segoe UI", boldId.Family.EnglishName);
        Assert.Equal(new FontStyle(700, false, 5), boldId.Style);
        Assert.Equal(FontStyle.Regular | FontStyle.NormalStretch, ((SingleFontSpec)spec.Parts[0]).FontId.Style);

        // Parts follow the spec's size, but a game font size is its own.
        Assert.Equal(0f, spec.Parts[2].SizePx);
        Assert.Equal(GameFontFamilyAndSize.Axis18.BaseSizePx, spec.Parts[3].SizePx);
        Assert.Equal(1, ((SingleFontSpec)spec.Parts[5]).FontNo);
        Assert.Same(FontSpec.Icon, spec.Parts[6]);

        Assert.Throws<ArgumentException>(() => FontSpec.From([default(FontSpec.FontSource)]));

        // Installed faces load by name. Segoe UI Bold is a file of its own. A name no installed font has fails when
        // the font loads, not before.
        var fonts = CreateFactory();
        var regularFont = fonts.CreateFont(FontSpec.From("Segoe UI")).Elements[0].Font;
        var boldFont = fonts.CreateFont(FontSpec.From("Segoe UI", FontStyle.Bold)).Elements[0].Font;
        Assert.NotSame(regularFont, boldFont);
        Assert.Same(fonts.CreateFont(FontSpec.From(new FileInfo(Segoe))).Elements[0].Font, regularFont);
        var missing = FontSpec.From("No Such Font Family 1b2c3d");
        Assert.ThrowsAny<Exception>(() => fonts.CreateFont(missing));
    }

    [Fact]
    public void StreamFontsShareByContent()
    {
        using var s1 = File.OpenRead(Segoe);
        var id1 = Assert.IsType<StreamFontId>(FontSpec.From(s1).FontId);
        var id2 = Assert.IsType<StreamFontId>(FontSpec.From(File.ReadAllBytes(Segoe)).FontId);
        Assert.Equal(id1, id2);
        Assert.Equal(FontSpec.From(File.ReadAllBytes(Segoe)), FontSpec.From(new ReadOnlySpan<byte>(File.ReadAllBytes(Segoe))));
        using var s3 = File.OpenRead(Malgun);
        Assert.NotEqual(id1, FontSpec.From(s3).FontId);

        var fonts = CreateFactory();
        Assert.Same(fonts.GetStreamFont(id1, 0), fonts.GetStreamFont(id2, 0));
        Assert.Throws<JsonSerializationException>(() => JsonConvert.SerializeObject(id1));
    }

    [Fact]
    public void GlyphRangesCoverAllPlanes()
    {
        var ranges = default(FluentGlyphRangeBuilder).With(0x41, 0x43).With(0x1F600).BuildExact();
        Assert.Equal(new uint[] { 0x41, 0x43, 0x1F600, 0x1F600, 0 }, ranges);
        Assert.True(new CompositeFont.Element(null!, CompositeFont.ToRanges(ranges)).Contains(0x1F600));
        Assert.False(new CompositeFont.Element(null!, CompositeFont.ToRanges(ranges)).Contains(0x44));
    }

    // A factory without its constructor, so only its caches and locks are made. Services are never provided here.
    // Touching one (even its GetNullable) waits for the container forever, so the default font spec is set.
    private static FontFactory CreateFactory()
    {
        var fonts = (FontFactory)RuntimeHelpers.GetUninitializedObject(typeof(FontFactory));
        foreach (var field in typeof(FontFactory).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if ((field.FieldType.IsGenericType &&
                 field.FieldType.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<,>)) ||
                field.FieldType == typeof(Lock))
            {
                field.SetValue(fonts, Activator.CreateInstance(field.FieldType));
            }
        }

        fonts.DefaultFontSpecOverride = new SingleFontSpec { FontId = new FileFontId(Segoe), SizePx = 16 };
        return fonts;
    }
}
