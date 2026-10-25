using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts;

/// <summary>What happened to a link of the styled text drawn last.</summary>
public readonly ref struct TextInteraction
{
    /// <summary>Initializes a new instance of the <see cref="TextInteraction"/> struct.</summary>
    /// <param name="markup">The markup of the text.</param>
    /// <param name="source">The text as read.</param>
    /// <param name="offset">The byte offset of what defines the link in <paramref name="source"/>.</param>
    /// <param name="length">The length of what defines the link in <paramref name="source"/>.</param>
    /// <param name="drawnOffset">The byte offset of what defines the link, in the text as drawn.</param>
    /// <param name="drawnLength">The length of what defines the link, in the text as drawn.</param>
    /// <param name="target">The link target.</param>
    /// <param name="flags">What happened.</param>
    /// <param name="held">Whether the link is held down.</param>
    internal TextInteraction(
        ImTextMarkup markup, ReadOnlySpan<byte> source, int offset, int length, int drawnOffset, int drawnLength,
        ReadOnlySpan<byte> target, TextInteractionFlags flags, bool held)
    {
        this.Markup = markup;
        this.Source = source;
        this.Offset = offset;
        this.Length = length;
        this.DrawnOffset = drawnOffset;
        this.DrawnLength = drawnLength;
        this.Target = target;
        this.Flags = flags;
        this.Held = held;
    }

    /// <summary>Gets the markup of the text.</summary>
    public ImTextMarkup Markup { get; }

    /// <summary>Gets the text as read.</summary>
    /// <remarks>For <see cref="ImTextMarkup.MacroString"/>, it is the compiled SeString. For the others, it is the
    /// text as drawn.</remarks>
    public ReadOnlySpan<byte> Source { get; }

    /// <summary>Gets the byte offset of what defines the link in <see cref="Source"/>.</summary>
    /// <remarks>It is in the same text as <see cref="TextLinkContext.Offset"/>.</remarks>
    public int Offset { get; }

    /// <summary>Gets the length of what defines the link in bytes, in <see cref="Source"/>.</summary>
    public int Length { get; }

    /// <summary>Gets the byte offset of what defines the link, in the text as drawn.</summary>
    /// <remarks>It differs from <see cref="Offset"/> only for <see cref="ImTextMarkup.MacroString"/>.</remarks>
    public int DrawnOffset { get; }

    /// <summary>Gets the length of what defines the link in bytes, in the text as drawn.</summary>
    public int DrawnLength { get; }

    /// <summary>Gets the link target.</summary>
    /// <remarks>For SeStrings, it is the link payload.</remarks>
    public ReadOnlySpan<byte> Target { get; }

    /// <summary>Gets what happened.</summary>
    public TextInteractionFlags Flags { get; }

    /// <summary>Gets a value indicating whether the link is held down.</summary>
    public bool Held { get; }
}
