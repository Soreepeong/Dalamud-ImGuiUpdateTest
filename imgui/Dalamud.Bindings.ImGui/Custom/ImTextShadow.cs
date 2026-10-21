using System.Numerics;

namespace Dalamud.Bindings.ImGui;

/// <summary>A shadow under the glyphs and edge of a text.</summary>
/// <param name="Color">The color, or alpha 0 for no shadow.</param>
/// <param name="Offset">The offset in ems, or 0 for no shadow.</param>
/// <seealso cref="ImTextStyle.Shadow"/>
public readonly record struct ImTextShadow(ImTextColor Color, Vector2 Offset)
{
    /// <summary>Initializes a new instance of the <see cref="ImTextShadow"/> struct, offset by
    /// <see cref="DefaultOffset"/>.</summary>
    /// <param name="color">The color, or alpha 0 for no shadow.</param>
    public ImTextShadow(ImTextColor color)
        : this(color, DefaultOffset)
    {
    }

    /// <summary>Gets the default offset, in ems.</summary>
    public static Vector2 DefaultOffset => new(0, 1 / 16f);

    /// <summary>Gets a value indicating whether there is a shadow.</summary>
    public bool IsOn => this.Color.Alpha != 0 && this.Offset != Vector2.Zero;
}
