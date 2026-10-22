using System.Numerics;
using System.Runtime.InteropServices;

namespace Dalamud.Interface.Fonts.Internal.Formats;

/// <summary>An entry of a game bitmap font icon table file (.gfd).</summary>
[StructLayout(LayoutKind.Sequential, Size = 0x10)]
internal struct GfdEntry
{
    /// <summary>ID of the entry.</summary>
    public ushort Id;

    /// <summary>The left offset of the entry.</summary>
    public ushort Left;

    /// <summary>The top offset of the entry.</summary>
    public ushort Top;

    /// <summary>The width of the entry.</summary>
    public ushort Width;

    /// <summary>The height of the entry.</summary>
    public ushort Height;

    /// <summary>Unknown/unused.</summary>
    public ushort Unk0A;

    /// <summary>The redirected entry, maybe.</summary>
    public ushort Redirect;

    /// <summary>Unknown/unused.</summary>
    public ushort Unk0E;

    /// <summary>Gets a value indicating whether this entry is effectively empty.</summary>
    public readonly bool IsEmpty => this.Width == 0 || this.Height == 0;

    /// <summary>Gets the UV0 of this entry.</summary>
    public readonly Vector2 Uv0 => new(this.Left / 512f, this.Top / 1024f);

    /// <summary>Gets the UV1 of this entry.</summary>
    public readonly Vector2 Uv1 => new((this.Left + this.Width) / 512f, (this.Top + this.Height) / 1024f);

    /// <summary>Gets the UV0 of the HQ version of this entry.</summary>
    public readonly Vector2 HqUv0 => new(this.Left / 256f, (this.Top + 170.5f) / 512f);

    /// <summary>Gets the UV1 of the HQ version of this entry.</summary>
    public readonly Vector2 HqUv1 => new((this.Left + this.Width) / 256f, (this.Top + this.Height + 170.5f) / 512f);

    /// <summary>Calculates the size in pixels of a GFD entry when drawn along with a text.</summary>
    /// <param name="fontSize">Font size in pixels.</param>
    /// <param name="useHq">Whether to draw the HQ texture.</param>
    /// <returns>Determined size of the GFD entry when drawn.</returns>
    public readonly Vector2 CalculateScaledSize(float fontSize, out bool useHq)
    {
        useHq = fontSize > 19;
        var targetHeight = useHq ? fontSize : 20;
        return new(this.Width * (targetHeight / this.Height), targetHeight);
    }
}
