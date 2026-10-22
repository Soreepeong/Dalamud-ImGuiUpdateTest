using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts;

/// <summary>A link of styled text that is currently being dealt with.</summary>
public readonly ref struct TextLinkContext
{
    /// <summary>Initializes a new instance of the <see cref="TextLinkContext"/> struct.</summary>
    /// <param name="markup">The markup of the text.</param>
    /// <param name="source">The text as read.</param>
    /// <param name="offset">The byte offset of what defines the link in <paramref name="source"/>.</param>
    /// <param name="length">The length of what defines the link.</param>
    /// <param name="target">The link target.</param>
    /// <param name="state">What is happening to the link.</param>
    public TextLinkContext(
        ImTextMarkup markup, ReadOnlySpan<byte> source, int offset, int length, ReadOnlySpan<byte> target,
        TextLinkState state)
    {
        this.Markup = markup;
        this.Source = source;
        this.Offset = offset;
        this.Length = length;
        this.Target = target;
        this.State = state;
    }

    /// <summary>Gets the markup of the text.</summary>
    public ImTextMarkup Markup { get; }

    /// <summary>Gets the text as read.</summary>
    /// <remarks>For <see cref="ImTextMarkup.MacroString"/>, it is the compiled SeString.</remarks>
    public ReadOnlySpan<byte> Source { get; }

    /// <summary>Gets the byte offset of what defines the link in <see cref="Source"/>.</summary>
    /// <remarks>For SeStrings, it is the link payload.</remarks>
    public int Offset { get; }

    /// <summary>Gets the length of what defines the link in bytes.</summary>
    public int Length { get; }

    /// <summary>Gets the link target.</summary>
    /// <remarks>For SeStrings, it is the link payload.</remarks>
    public ReadOnlySpan<byte> Target { get; }

    /// <summary>Gets what is happening to the link.</summary>
    public TextLinkState State { get; }

    /// <summary>Gets a value indicating whether the link is held down.</summary>
    public bool Held => this.State == TextLinkState.Held;
}
