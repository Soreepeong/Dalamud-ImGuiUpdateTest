namespace Dalamud.Bindings.ImGui;

/// <summary>Lines under, over, or through a text, in one style.</summary>
/// <param name="Positions">Where the lines are, or <see cref="ImTextLinePosition.None"/> for no lines.</param>
/// <param name="Style">How the lines are drawn, or <see cref="ImTextLineStyle.None"/> for no lines.</param>
/// <param name="Color">The color, or 0 for the color of the text.</param>
/// <param name="Thickness">The thickness of each line in pixels, or 0 for what the font says.</param>
/// <param name="Count">The number of lines at each position. 0 also means 1.</param>
/// <seealso cref="ImTextStyle.Lines"/>
public readonly record struct ImTextLineDecoration(
    ImTextLinePosition Positions,
    ImTextLineStyle Style = ImTextLineStyle.Solid,
    ImTextColor Color = default,
    float Thickness = 0,
    byte Count = 1)
{
    /// <summary>Gets a value indicating whether there are lines.</summary>
    public bool IsOn => this.Positions != ImTextLinePosition.None && this.Style != ImTextLineStyle.None;

    public static implicit operator ImTextDecoration(in ImTextLineDecoration d) => d.ToDecoration();

    /// <summary>Reads the lines of a decoration.</summary>
    /// <param name="decoration">The decoration.</param>
    /// <param name="lines">The lines.</param>
    /// <returns>Whether it is a <see cref="ImTextDecorationId.Line"/>.</returns>
    public static bool TryRead(in ImTextDecoration decoration, out ImTextLineDecoration lines)
    {
        if (decoration.Id != ImTextDecorationId.Line)
        {
            lines = default;
            return false;
        }

        var data = decoration.Data;
        lines = new(
            (ImTextLinePosition)(byte)data,
            (ImTextLineStyle)(byte)(data >> 8),
            decoration.Context,
            BitConverter.UInt32BitsToSingle((uint)(data >> 32)),
            (byte)(data >> 16));
        return true;
    }

    /// <summary>Gets the decoration of these lines, such as for <see cref="ImTextStyle.ExtraDecoration"/>.</summary>
    /// <returns>The decoration, of <see cref="ImTextDecorationId.Line"/>.</returns>
    public ImTextDecoration ToDecoration() =>
        new(
            ImTextDecorationId.Line,
            this.Color,
            (byte)this.Positions |
            ((ulong)this.Style << 8) |
            ((ulong)Math.Max(this.Count, (byte)1) << 16) |
            ((ulong)BitConverter.SingleToUInt32Bits(this.Thickness) << 32));
}
