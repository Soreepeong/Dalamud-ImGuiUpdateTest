using System.Numerics;

namespace Dalamud.Interface.Fonts;

/// <summary>Something drawn instead of a part of styled text.</summary>
/// <seealso cref="ITextMarkupCallback.TryGetEntity"/>
public readonly record struct TextEntity
{
    /// <summary>Initializes a new instance of the <see cref="TextEntity"/> struct.</summary>
    /// <param name="length">Number of bytes of <see cref="TextEntityContext.Source"/> it replaces.</param>
    /// <param name="size">Its size in pixels.</param>
    /// <param name="draw">Draws it.</param>
    public TextEntity(int length, Vector2 size, TextEntityDrawDelegate draw)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfNegative(size.X, nameof(size));
        ArgumentOutOfRangeException.ThrowIfNegative(size.Y, nameof(size));
        ArgumentNullException.ThrowIfNull(draw);
        this.Length = length;
        this.Size = size;
        this.Draw = draw;
    }

    /// <summary>Gets the number of bytes of <see cref="TextEntityContext.Source"/> it replaces.</summary>
    public int Length { get; }

    /// <summary>Gets its size in pixels.</summary>
    public Vector2 Size { get; }

    /// <summary>Gets what draws it.</summary>
    public TextEntityDrawDelegate Draw { get; }

    /// <summary>Gets a value indicating whether there is an entity.</summary>
    /// <param name="e">The entity.</param>
    public static implicit operator bool(in TextEntity e) => e.Length != 0;
}
