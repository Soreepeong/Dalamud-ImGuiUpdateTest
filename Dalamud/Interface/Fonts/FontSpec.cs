using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Text;

using Newtonsoft.Json;

namespace Dalamud.Interface.Fonts;

/// <summary>Describes a font. Equal specs share one font.</summary>
/// <remarks><ul>
/// <li>A spec is its own font <see cref="FontId"/>, then each of <see cref="Fallbacks"/> in order, for characters the
/// fonts before it lack.</li>
/// <li>Specs from <see cref="From(IFontId, float, bool)"/> and the other factories fall back to <see cref="Default"/>,
/// unless their <c>fallback</c> argument is <see langword="false"/>.</li>
/// <li>Lengths, such as <see cref="GlyphOffset"/>, are in pixels at <see cref="SizePx"/> if it is positive, else in
/// ems.</li>
/// <li>Make a spec once, such as in a field, and keep it or the <see cref="ImFontRef"/> of it. Making a spec can read
/// a file, and a font is found faster by the same spec object than by an equal one.</li>
/// </ul></remarks>
/// <example><code>
/// // Comic Sans MS at 20px, then Gungsuh at 90% for Hangul, then the default font.
/// var spec = FontSpec.From(
///     [
///         "Comic Sans MS",
///         FontSpec.From("Gungsuh", sizePx: -0.9f, fallback: false) with { CodepointRanges = [0xAC00, 0xD7A3] },
///     ],
///     sizePx: 20);
/// </code></example>
public sealed partial record FontSpec
{
    private const float EmboldenEmPerWeight = 1f / 7200f;

    private static readonly uint IconMin = (uint)Enum.GetValues<FontAwesomeIcon>().Where(x => x > 0).Min();
    private static readonly uint IconMax = (uint)Enum.GetValues<FontAwesomeIcon>().Where(x => x > 0).Max();
    private static readonly DalamudAssetFontAndFamilyId FontAwesome = new(DalamudAsset.FontAwesomeFreeSolid);

    /// <summary>Gets the Dalamud default font from settings, with all the system fallback fonts, at the current font
    /// size.</summary>
    /// <remarks>It has no <see cref="Fallbacks"/>, as it is the chain of fallback fonts itself.</remarks>
    public static FontSpec Default { get; } = new() { FontId = DalamudDefaultFontAndFamilyId.Instance };

    /// <summary>Gets the <see cref="FontAwesomeIcon"/> font, at the current font size.</summary>
    /// <remarks>It has the icons only, and no <see cref="Fallbacks"/>. Each icon advances 1 em. Use
    /// <see cref="WithSize"/> for a size in pixels.</remarks>
    public static FontSpec Icon { get; } =
        From(FontAwesome, fallback: false) with
        {
            CodepointRanges = [IconMin, IconMax],
            GlyphMinAdvanceX = 1,
            GlyphMaxAdvanceX = 1,
        };

    /// <summary>Gets the <see cref="FontAwesomeIcon"/> font, with each icon fit into a square.</summary>
    /// <remarks>It has the space and the icons only, and no <see cref="Fallbacks"/>.</remarks>
    public static FontSpec IconFixedWidth { get; } =
        From(FontAwesome, fallback: false) with
        {
            CodepointRanges = [0x20, 0x20, IconMin, IconMax],
            FitToSquare = true,
        };

    /// <summary>Gets the Dalamud default monospaced font, with all the system fallback fonts.</summary>
    public static FontSpec Mono { get; } = From(DalamudAsset.InconsolataRegular);

    /// <summary>Gets the font id, or <see langword="null"/> for a spec made only of <see cref="Fallbacks"/>.</summary>
    [JsonProperty]
    public IFontId? FontId { get; init; }

    /// <summary>Gets the index of the face in a font collection file (such as TTC).</summary>
    /// <remarks>Relevant for TrueType Collection(<c>.ttc</c>) files. Otherwise, use 0.</remarks>
    [JsonProperty]
    public int FontNo { get; init; }

    /// <summary>Gets the font size.</summary>
    /// <value>The size of the glyphs in pixels before the global scale if positive, a ratio of the current font size
    /// if negative (-1.5 = 150%), or the current font size if 0. -1, NaN and infinities are stored as 0, so that equal
    /// sizes compare equal.</value>
    public float SizePx
    {
        get;
        init => field = !float.IsFinite(value) || Math.Abs(value + 1f) < 1e-6f ? 0 : value;
    }

    /// <summary>Gets the <see cref="SizePx"/> but in points if it's not a ratio.</summary>
    [JsonIgnore]
    public float SizePt
    {
        get => this.SizePx > 0 ? (this.SizePx * 3) / 4 : this.SizePx;
        init => this.SizePx = value > 0 ? (value * 4) / 3 : value;
    }

    /// <summary>Gets the line height ratio to the font size.</summary>
    /// <remarks><see cref="Fallbacks"/> use the line height of the spec they are in.</remarks>
    [JsonProperty]
    public float LineHeight { get; init; } = 1f;

    /// <summary>Gets the locale to draw text in, such as <c>"ja-JP"</c>.</summary>
    /// <value>A locale name in any case, or <see langword="null"/> to take it from Dalamud's UI language. An empty name
    /// is stored as <see langword="null"/>.</value>
    /// <remarks>This matters for unified CJK range, cyrillic, and maybe some more.</remarks>
    [JsonProperty]
    public string? Locale
    {
        get;
        init => field = string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Gets the ranges of codepoints to take from <see cref="FontId"/>.</summary>
    /// <value>Pairs of inclusive bounds, or <see langword="null"/> for all. A zero after the last pair is dropped,
    /// and a default array is taken as <see langword="null"/>.</value>
    [JsonProperty]
    public ImmutableArray<uint>? CodepointRanges
    {
        get;
        init => field = value is not { IsDefault: false } ranges
                             ? null
                             : (ranges.Length & 1) == 1 && ranges[^1] == 0
                                 ? ranges[..^1]
                                 : ranges;
    }

    /// <summary>Gets the glyph offset of <see cref="FontId"/>, in pixels.</summary>
    [JsonProperty]
    public Vector2 GlyphOffset { get; init; }

    /// <summary>Gets the letter spacing of <see cref="FontId"/>, in pixels.</summary>
    [JsonProperty]
    public float LetterSpacing { get; init; }

    /// <summary>Gets the minimum advance of glyphs of <see cref="FontId"/>, in pixels.</summary>
    [JsonProperty]
    public float GlyphMinAdvanceX { get; init; }

    /// <summary>Gets the maximum advance of glyphs of <see cref="FontId"/>, in pixels.</summary>
    [JsonProperty]
    public float GlyphMaxAdvanceX { get; init; } = float.MaxValue;

    /// <summary>Gets a value indicating whether to scale glyphs of <see cref="FontId"/> down to fit a square.</summary>
    /// <remarks>Use with icon fonts.</remarks>
    [JsonProperty]
    public bool FitToSquare { get; init; }

    /// <summary>Gets the synthetic bold strength of <see cref="FontId"/>, in pixels.</summary>
    [JsonProperty]
    public float Embolden { get; init; }

    /// <summary>Gets the specs to try in order for characters that <see cref="FontId"/> lacks.</summary>
    /// <remarks>A child <see cref="SizePx"/> is relative to this font spec's <see cref="SizePx"/> if not positive.
    /// </remarks>
    [JsonProperty]
    public ImmutableArray<FontSpec> Fallbacks
    {
        get;
        init => field = value.IsDefault ? [] : value;
    }

        = [];

    /// <summary>Gets the ratio to the default font size, or 1 if <see cref="SizePx"/> is in pixels.</summary>
    internal float RelativeSize => this.SizePx >= 0 ? 1f : -this.SizePx;

    /// <inheritdoc/>
    public bool Equals(FontSpec? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && Equals(this.FontId, other.FontId)
            && this.FontNo == other.FontNo
            && this.SizePx.Equals(other.SizePx)
            && this.LineHeight.Equals(other.LineHeight)
            && string.Equals(this.Locale, other.Locale, StringComparison.OrdinalIgnoreCase)
            && this.CodepointRanges.HasValue == other.CodepointRanges.HasValue
            && (this.CodepointRanges ?? []).SequenceEqual(other.CodepointRanges ?? [])
            && this.GlyphOffset.Equals(other.GlyphOffset)
            && this.LetterSpacing.Equals(other.LetterSpacing)
            && this.GlyphMinAdvanceX.Equals(other.GlyphMinAdvanceX)
            && this.GlyphMaxAdvanceX.Equals(other.GlyphMaxAdvanceX)
            && this.FitToSquare == other.FitToSquare
            && this.Embolden.Equals(other.Embolden)
            && this.Fallbacks.SequenceEqual(other.Fallbacks));

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hc = default(HashCode);
        hc.Add(this.FontId);
        hc.Add(this.FontNo);
        hc.Add(this.SizePx);
        hc.Add(this.LineHeight);
        hc.Add(this.Locale, StringComparer.OrdinalIgnoreCase);
        hc.Add(this.GlyphOffset);
        hc.Add(this.LetterSpacing);
        hc.Add(this.Embolden);
        foreach (var r in this.CodepointRanges ?? [])
            hc.Add(r);
        foreach (var f in this.Fallbacks)
            hc.Add(f);
        return hc.ToHashCode();
    }

    /// <summary>Gets this spec at another size, with its lengths in proportion to the size.</summary>
    /// <param name="sizePx">The size, as for <see cref="SizePx"/>.</param>
    /// <returns>The spec at the size.</returns>
    /// <remarks>Lengths change their unit with <see cref="SizePx"/>, so <c>spec with { SizePx = 32 }</c> turns the
    /// 1 em advance of <see cref="Icon"/> into 1 pixel. This keeps it 1 em. Fallbacks in pixels are scaled with this
    /// spec if both sizes are in pixels.</remarks>
    public FontSpec WithSize(float sizePx)
    {
        var resized = this with { SizePx = sizePx };
        var k = (resized.SizePx > 0 ? resized.SizePx : 1f) / (this.SizePx > 0 ? this.SizePx : 1f);
        if (k == 1f)
            return resized;

        return resized with
        {
            GlyphOffset = this.GlyphOffset * k,
            LetterSpacing = this.LetterSpacing * k,
            GlyphMinAdvanceX = this.GlyphMinAdvanceX * k,
            GlyphMaxAdvanceX = this.GlyphMaxAdvanceX >= float.MaxValue ? float.MaxValue : this.GlyphMaxAdvanceX * k,
            Embolden = this.Embolden * k,
            Fallbacks = this.SizePx > 0 && resized.SizePx > 0
                            ? [.. this.Fallbacks.Select(f => f.SizePx > 0 ? f.WithSize(f.SizePx * k) : f)]
                            : this.Fallbacks,
        };
    }

    /// <summary>Describes this spec, preferably in the requested locale.</summary>
    /// <param name="localeCode">The locale, such as "ko-KR", in any case.</param>
    /// <returns>The description.</returns>
    public string ToLocalizedString(string localeCode)
    {
        var sb = new StringBuilder();
        if (this.FontId != null)
        {
            sb.Append($"{this.FontId.Family.Name.Get(localeCode)}({this.FontId.Name.Get(localeCode)}");
            if (this.GlyphOffset != default)
                sb.Append($", O={this.GlyphOffset.X:0.##},{this.GlyphOffset.Y:0.##}");
            if (this.LetterSpacing != 0f)
                sb.Append($", LS={this.LetterSpacing:0.##}");
            sb.Append(')');
        }

        foreach (var f in this.Fallbacks)
        {
            if (sb.Length > 0)
                sb.Append(" + ");
            sb.Append(f.Equals(Default) ? "(Default)" : $"[{f.ToLocalizedString(localeCode)}]");
        }

        sb.Append(this.SizePx > 0 ? $" @ {this.SizePt}pt" : $" @ {this.RelativeSize:0.##}x");
        if (Math.Abs(this.LineHeight - 1f) > 0.000001f)
            sb.Append($", LH={this.LineHeight:0.##}");
        if (this.Locale is not null)
            sb.Append($", {this.Locale}");
        return sb.ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => this.ToLocalizedString("en");

    /// <summary>Gets the first font id of this spec or its fallbacks.</summary>
    /// <returns>The font id, or <see langword="null"/> if there is none.</returns>
    internal IFontId? GetFirstFontId()
    {
        if (this.FontId != null)
            return this.FontId;
        foreach (var f in this.Fallbacks)
        {
            if (f.GetFirstFontId() is { } fid)
                return fid;
        }

        return null;
    }

    // Synthetic bold for a weight above a face's, in ems for specs following the current size, else in pixels at the
    // size. Weight 700 over 400 gives 1/24 em.
    private static float EmboldenOf(FontStyle style, FontStyle face, float sizePx) =>
        (Math.Max(0, style.EffectiveWeight - Math.Max(face.EffectiveWeight, 400)) * EmboldenEmPerWeight)
        * (sizePx > 0 ? sizePx : 1);
}
