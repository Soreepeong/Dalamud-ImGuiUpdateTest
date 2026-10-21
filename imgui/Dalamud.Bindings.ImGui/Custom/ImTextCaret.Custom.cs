namespace Dalamud.Bindings.ImGui;

/// <summary>A caret position in a text layout, from getting the caret of an offset, hit testing a point, or moving
/// another caret.</summary>
/// <remarks>
/// <para><see cref="Pos"/> is the start of the caret line, relative to the layout origin. That is the top of the line
/// for horizontal text, or the left of the column for vertical text. <see cref="Length"/> is the length of the caret
/// line, which is the line height or column width. <see cref="InlinePos"/> is the position along the inline axis
/// within the line.</para>
/// <para><see cref="TextOffset"/> is in bytes. <see cref="RunIndex"/> is the run of the grapheme at the caret, or
/// before it at the end of the text, or -1. <see cref="ObjectIndex"/> is the inline object at the caret or under the
/// point, or -1. <see cref="UserData"/> is the user data of the run or object, or 0.</para>
/// </remarks>
public partial struct ImTextCaret
{
    /// <summary>Gets or sets a value indicating whether the caret is at the end of <see cref="LineIndex"/> at a wrap
    /// point, rather than the start of the next line.</summary>
    public bool IsPreviousLine
    {
        readonly get => this.PreviousLine != 0;
        set => this.PreviousLine = value ? (byte)1 : (byte)0;
    }

    /// <summary>Gets or sets a value indicating whether the hit tested point is inside a grapheme's box.</summary>
    public bool IsInside
    {
        readonly get => this.Inside != 0;
        set => this.Inside = value ? (byte)1 : (byte)0;
    }
}
