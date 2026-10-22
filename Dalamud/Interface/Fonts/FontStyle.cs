using System.Linq;

namespace Dalamud.Interface.Fonts;

/// <summary>The weight, italic and stretch of a face, to choose among the faces of a font family.</summary>
/// <remarks>Combine with <c>|</c>, as in <c>FontStyle.Bold | FontStyle.Italic</c>. Parts left at 0 (and not italic)
/// are unspecified. Unless combined with a style that specifies them, they mean the regular weight, upright, and the
/// normal stretch. The face closest to the style is chosen when the font loads.</remarks>
public readonly record struct FontStyle
{
    /// <summary>Initializes a new instance of the <see cref="FontStyle"/> struct.</summary>
    /// <param name="weight">The weight, from 1 (thinnest) to 999 (heaviest), or 0 for unspecified.</param>
    /// <param name="italicized">Whether italic.</param>
    /// <param name="stretch">The stretch, from 1 (narrowest) to 9 (widest), or 0 for unspecified.</param>
    public FontStyle(int weight = 0, bool italicized = false, int stretch = 0)
    {
        this.Weight = weight;
        this.Italicized = italicized;
        this.Stretch = stretch;
    }

    /// <summary>Gets the unspecified style.</summary>
    public static FontStyle Default => default;

    /// <summary>Gets the weight 100.</summary>
    public static FontStyle Thin => new(100);

    /// <summary>Gets the weight 200.</summary>
    public static FontStyle ExtraLight => new(200);

    /// <summary>Gets the weight 300.</summary>
    public static FontStyle Light => new(300);

    /// <summary>Gets the weight 350.</summary>
    public static FontStyle SemiLight => new(350);

    /// <summary>Gets the weight 400.</summary>
    public static FontStyle Regular => new(400);

    /// <summary>Gets the weight 500.</summary>
    public static FontStyle Medium => new(500);

    /// <summary>Gets the weight 600.</summary>
    public static FontStyle SemiBold => new(600);

    /// <summary>Gets the weight 700.</summary>
    public static FontStyle Bold => new(700);

    /// <summary>Gets the weight 800.</summary>
    public static FontStyle ExtraBold => new(800);

    /// <summary>Gets the weight 900.</summary>
    public static FontStyle Black => new(900);

    /// <summary>Gets the weight 950.</summary>
    public static FontStyle ExtraBlack => new(950);

    /// <summary>Gets the italic style.</summary>
    public static FontStyle Italic => new(italicized: true);

    /// <summary>Gets the stretch 1.</summary>
    public static FontStyle UltraCondensed => new(stretch: 1);

    /// <summary>Gets the stretch 2.</summary>
    public static FontStyle ExtraCondensed => new(stretch: 2);

    /// <summary>Gets the stretch 3.</summary>
    public static FontStyle Condensed => new(stretch: 3);

    /// <summary>Gets the stretch 4.</summary>
    public static FontStyle SemiCondensed => new(stretch: 4);

    /// <summary>Gets the stretch 5.</summary>
    public static FontStyle NormalStretch => new(stretch: 5);

    /// <summary>Gets the stretch 6.</summary>
    public static FontStyle SemiExpanded => new(stretch: 6);

    /// <summary>Gets the stretch 7.</summary>
    public static FontStyle Expanded => new(stretch: 7);

    /// <summary>Gets the stretch 8.</summary>
    public static FontStyle ExtraExpanded => new(stretch: 8);

    /// <summary>Gets the stretch 9.</summary>
    public static FontStyle UltraExpanded => new(stretch: 9);

    /// <summary>Gets the weight, from 1 (thinnest) to 999 (heaviest), or 0 for unspecified.</summary>
    /// <exception cref="ArgumentOutOfRangeException">If out of range.</exception>
    public int Weight
    {
        get;
        init => field = value is >= 0 and <= 999
                            ? value
                            : throw new ArgumentOutOfRangeException(nameof(value), value, "Must be from 0 to 999.");
    }

    /// <summary>Gets a value indicating whether italic. Italic text is in the family's italic or oblique face if it has
    /// one, and otherwise slanted as the game slants italic text.</summary>
    public bool Italicized { get; init; }

    /// <summary>Gets the stretch, from 1 (narrowest) to 9 (widest), or 0 for unspecified.</summary>
    /// <exception cref="ArgumentOutOfRangeException">If out of range.</exception>
    public int Stretch
    {
        get;
        init => field = value is >= 0 and <= 9
                            ? value
                            : throw new ArgumentOutOfRangeException(nameof(value), value, "Must be from 0 to 9.");
    }

    /// <summary>Gets <see cref="Weight"/>, or 400 if unspecified.</summary>
    internal int EffectiveWeight => this.Weight == 0 ? 400 : this.Weight;

    /// <summary>Gets <see cref="Stretch"/>, or 5 if unspecified.</summary>
    internal int EffectiveStretch => this.Stretch == 0 ? 5 : this.Stretch;

    /// <summary>Combines two styles. The parts <paramref name="right"/> specifies replace those of
    /// <paramref name="left"/>.</summary>
    /// <param name="left">The style to start from.</param>
    /// <param name="right">The style to apply.</param>
    /// <returns>The combined style.</returns>
    public static FontStyle operator |(FontStyle left, FontStyle right) =>
        new(
            right.Weight != 0 ? right.Weight : left.Weight,
            right.Italicized || left.Italicized,
            right.Stretch != 0 ? right.Stretch : left.Stretch);

    /// <inheritdoc/>
    public override string ToString()
    {
        string?[] names =
        [
            this.Stretch switch
            {
                1 => "Ultra Condensed",
                2 => "Extra Condensed",
                3 => "Condensed",
                4 => "Semi Condensed",
                6 => "Semi Expanded",
                7 => "Expanded",
                8 => "Extra Expanded",
                9 => "Ultra Expanded",
                _ => null,
            },
            this.Weight switch
            {
                0 or 400 => null,
                100 => "Thin",
                200 => "Extra Light",
                300 => "Light",
                350 => "Semi Light",
                500 => "Medium",
                600 => "Semi Bold",
                700 => "Bold",
                800 => "Extra Bold",
                900 => "Black",
                950 => "Extra Black",
                var w => $"Weight {w}",
            },
            this.Italicized ? "Italic" : null,
        ];
        var name = string.Join(' ', names.OfType<string>());
        return name.Length == 0 ? "Regular" : name;
    }
}
