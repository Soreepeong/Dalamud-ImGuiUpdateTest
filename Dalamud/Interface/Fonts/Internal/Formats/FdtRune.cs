using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Dalamud.Interface.Fonts.Internal.Formats;

/// <summary>Represents a 32-bit Unicode character for a `.fdt` file.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FdtRune : IComparable<FdtRune>, IComparable<FdtGlyph>, IEquatable<FdtRune>
{
    /// <summary>Integer representation of a Unicode character in UTF-8 in reverse order.</summary>
    public uint Raw;

    /// <summary>Gets or sets the <see langword="uint"/> representation of this rune.</summary>
    /// <remarks>Behavior is undefined for malformed UTF-8 sequences and invalid codepoints.</remarks>
    public uint Codepoint
    {
        readonly get
        {
            var raw = this.Raw;
            if (raw < 0x80)
                return raw;

            var length = 4 - (BitOperations.LeadingZeroCount(raw) / 8);
            return ((raw & 0x3F) | ((raw >> 2) & 0xFC0) | ((raw >> 4) & 0x3F000) | ((raw >> 6) & 0x1C0000))
                   & ((1u << ((5 * length) + 1)) - 1);
        }

        set => this.Raw = value < 0x80
                              ? value
                              : (value & 0x3F)
                                | ((value << 2) & 0x3F00)
                                | ((value << 4) & 0x3F0000)
                                | ((value << 6) & 0x3F000000)
                                | (value < 0x800 ? 0xC080u : value < 0x10000 ? 0xE08080u : 0xF0808080u);
    }

    public static implicit operator uint(FdtRune rune) => rune.Codepoint;

    public static implicit operator FdtRune(uint codepoint) => new() { Codepoint = codepoint };

    public static bool operator ==(FdtRune left, FdtRune right) => left.Equals(right);

    public static bool operator !=(FdtRune left, FdtRune right) => !left.Equals(right);

    /// <inheritdoc/>
    public readonly int CompareTo(FdtGlyph other) => this.Raw.CompareTo(other.Rune.Raw);

    /// <inheritdoc/>
    public readonly int CompareTo(FdtRune other) => this.Raw.CompareTo(other.Raw);

    /// <inheritdoc/>
    public readonly bool Equals(FdtRune other) => this.Raw == other.Raw;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is FdtRune other && this.Equals(other);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => (int)this.Raw;

    /// <inheritdoc/>
    public override readonly string ToString() =>
        (Rune.TryCreate(this.Codepoint, out var rune) ? rune : Rune.ReplacementChar).ToString();
}
