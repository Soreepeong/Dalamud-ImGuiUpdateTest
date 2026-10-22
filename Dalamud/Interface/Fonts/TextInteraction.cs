using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts;

/// <summary>What happened to a link of the styled text drawn last.</summary>
public readonly ref struct TextInteraction
{
    /// <summary>Initializes a new instance of the <see cref="TextInteraction"/> struct.</summary>
    /// <param name="markup">The markup of the text.</param>
    /// <param name="offset">The byte offset of what defines the link, in the text as drawn.</param>
    /// <param name="length">The length of what defines the link, in the text as drawn.</param>
    /// <param name="source">The text as read.</param>
    /// <param name="sourceOffset">The byte offset of what defines the link in <paramref name="source"/>.</param>
    /// <param name="sourceLength">The length of what defines the link in <paramref name="source"/>.</param>
    /// <param name="target">The link target.</param>
    /// <param name="flags">What happened.</param>
    /// <param name="held">Whether the link is held down.</param>
    public TextInteraction(
        ImTextMarkup markup, int offset, int length, ReadOnlySpan<byte> source, int sourceOffset, int sourceLength,
        ReadOnlySpan<byte> target, TextInteractionFlags flags, bool held)
    {
        this.Markup = markup;
        this.Offset = offset;
        this.Length = length;
        this.Source = source;
        this.SourceOffset = sourceOffset;
        this.SourceLength = sourceLength;
        this.Target = target;
        this.Flags = flags;
        this.Held = held;
    }

    /// <summary>Gets the markup of the text.</summary>
    public ImTextMarkup Markup { get; }

    /// <summary>Gets the byte offset of what defines the link, in the text as drawn.</summary>
    public int Offset { get; }

    /// <summary>Gets the length of what defines the link in bytes, in the text as drawn.</summary>
    public int Length { get; }

    /// <summary>Gets the text as read.</summary>
    /// <remarks>For <see cref="ImTextMarkup.MacroString"/>, it is the compiled SeString. For the others, it is the
    /// text as drawn.</remarks>
    public ReadOnlySpan<byte> Source { get; }

    /// <summary>Gets the byte offset of what defines the link in <see cref="Source"/>.</summary>
    public int SourceOffset { get; }

    /// <summary>Gets the length of what defines the link in <see cref="Source"/>.</summary>
    public int SourceLength { get; }

    /// <summary>Gets the link target.</summary>
    /// <remarks>For SeStrings, it is the link payload.</remarks>
    public ReadOnlySpan<byte> Target { get; }

    /// <summary>Gets what happened.</summary>
    public TextInteractionFlags Flags { get; }

    /// <summary>Gets a value indicating whether the link is held down.</summary>
    public bool Held { get; }
}
