using System.Numerics;

namespace Dalamud.Interface.Fonts;

/// <summary>Something drawn instead of a part of styled text.</summary>
/// <seealso cref="ITextMarkupCallback.TryGetEntity"/>
public readonly record struct TextEntity
{
    /// <summary>Initializes a new instance of the <see cref="TextEntity"/> struct.</summary>
    /// <param name="length">Number of bytes of <see cref="TextEntityContext.Source"/> it replaces, at least 1.</param>
    /// <param name="size">Its size in pixels, as for <see cref="Size"/>.</param>
    /// <param name="draw">Draws it.</param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="length"/> is not positive, or
    /// <paramref name="size"/> is not finite or has a negative width.</exception>
    /// <exception cref="ArgumentNullException">If <paramref name="draw"/> is null.</exception>
    public TextEntity(int length, Vector2 size, TextEntityDrawDelegate draw)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(size), size, "The size must be finite, with a width of 0 or more.");
        }

        ArgumentNullException.ThrowIfNull(draw);
        this.Length = length;
        this.Size = size;
        this.Draw = draw;
    }

    /// <summary>Gets the number of bytes of <see cref="TextEntityContext.Source"/> it replaces.</summary>
    /// <remarks>Macros within those bytes, such as color changes and link ends, are not applied.</remarks>
    public int Length { get; }

    /// <summary>Gets its size in pixels.</summary>
    /// <remarks>A negative height is a multiple of the height of the line as its text makes it. So -1 fills the line.
    /// <see cref="TextEntityContext.Size"/> has the height in pixels.</remarks>
    public Vector2 Size { get; }

    /// <summary>Gets what draws it.</summary>
    public TextEntityDrawDelegate Draw { get; }

    /// <summary>Gets where it is placed in its line.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not one of <see cref="TextEntityAlignment"/>.
    /// </exception>
    public TextEntityAlignment Alignment
    {
        get;
        init => field = value is >= TextEntityAlignment.Center and <= TextEntityAlignment.TextBottom
                            ? value
                            : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>Gets data for <see cref="Draw"/>, given to it as <see cref="TextEntityContext.Data"/>.</summary>
    /// <remarks>It lets one static method draw many entities, without a closure for each.</remarks>
    public ulong Data { get; init; }
}
