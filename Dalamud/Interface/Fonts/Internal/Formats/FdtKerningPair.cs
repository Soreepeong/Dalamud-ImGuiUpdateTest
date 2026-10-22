using System.Runtime.InteropServices;

namespace Dalamud.Interface.Fonts.Internal.Formats;

/// <summary>A single kerning pair.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FdtKerningPair : IComparable<FdtKerningPair>
{
    /// <summary>The left rune.</summary>
    public FdtRune Left;

    /// <summary>The right rune.</summary>
    public FdtRune Right;

    /// <summary><see cref="Left"/> but in Shift_JIS.</summary>
    public ushort LeftSjis;

    /// <summary><see cref="Right"/> but in Shift_JIS.</summary>
    public ushort RightSjis;

    /// <summary>The horizontal offset adjustment for the right character.</summary>
    public int RightOffset;

    /// <inheritdoc/>
    public int CompareTo(FdtKerningPair other) =>
        this.Left == other.Left ? this.Right.CompareTo(other.Right) : this.Left.CompareTo(other.Left);
}
