using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>Carets, hit tests and moving through the text.</summary>
internal sealed partial class TextLayout
{
    /// <summary>Gets the caret at a byte offset.</summary>
    /// <param name="textOffset">The byte offset.</param>
    /// <param name="preferPreviousLine">At a wrap point, the end of the previous line.</param>
    /// <returns>The caret.</returns>
    public ImTextCaret GetCaret(int textOffset, bool preferPreviousLine = false) =>
        this.MakeCaret(textOffset, this.LineOf(textOffset, preferPreviousLine));

    /// <summary>Gets the caret nearest to a point relative to the layout origin, with the run or object under it.
    /// </summary>
    /// <param name="point">The point.</param>
    /// <returns>The caret.</returns>
    public ImTextCaret HitTest(Vector2 point)
    {
        var (u, v) = this.FromScreen(point);
        var lineIndex = this.lines.FindIndex(l => v < l.BlockPos + l.BlockSize);
        if (lineIndex < 0)
            lineIndex = this.lines.Count - 1;

        var line = this.lines[lineIndex];
        var offset = -1;
        var under = -1;
        for (var k = line.ItemBegin; k < line.ItemEnd; k++)
        {
            ref readonly var item = ref this.ItemAt(k);
            if (item.Kind == ItemKind.Newline)
                break;
            if (u >= item.X && u < item.X + item.Advance)
                under = k;
            if (offset < 0)
                offset = this.OffsetInItem(item, u);
        }

        if (offset < 0)
        {
            offset = line.EndsWithNewline
                         ? line.TextEnd
                         : Math.Max(
                             line.TextEnd,
                             line.TextNext == line.TextBegin ? line.TextEnd : this.LineEndOffset(line));
        }

        var caret = this.MakeCaret(offset, lineIndex);
        caret.IsInside = under >= 0 && v >= line.BlockPos && v < line.BlockPos + line.BlockSize && u >= 0;
        caret.UserData = 0;
        if (under >= 0)
        {
            ref readonly var item = ref this.ItemAt(under);
            caret.RunIndex = item.Run;
            caret.ObjectIndex = item.Object;
            caret.UserData = this.UserDataOf(item.Run, item.Object);
        }

        return caret;
    }

    /// <summary>Moves a caret.</summary>
    /// <remarks>Moves along the block axis first (<paramref name="dy"/> lines, or the first or last line), then to a
    /// place on that line, then <paramref name="dx"/> units along the text.</remarks>
    /// <param name="from">The caret to move from.</param>
    /// <param name="flags">Where to move, and the unit of <paramref name="dx"/>.</param>
    /// <param name="dx">Units along the text, after the axes.</param>
    /// <param name="dy">Lines, for <see cref="ImTextMoveFlags.BlockRelative"/>.</param>
    /// <param name="preferredInlinePos">Position along the line to keep, or &lt; 0 for the caret's.</param>
    /// <returns>The caret.</returns>
    public ImTextCaret Move(in ImTextCaret from, ImTextMoveFlags flags, int dx, int dy, float preferredInlinePos = -1)
    {
        var block = flags & ImTextMoveFlags.BlockMask;
        var inline = flags & ImTextMoveFlags.InlineMask;
        int lineIndex;
        int target;
        if (block == ImTextMoveFlags.BlockRelative && dy == 0 && inline == ImTextMoveFlags.InlineKeep)
        {
            // Not moving along the axes, so start from the caret itself, keeping its side of a wrap point.
            lineIndex = Math.Clamp(from.LineIndex, 0, this.lines.Count - 1);
            target = from.TextOffset;
        }
        else
        {
            lineIndex = block switch
            {
                ImTextMoveFlags.BlockFirst => 0,
                ImTextMoveFlags.BlockLast => this.lines.Count - 1,
                _ => from.LineIndex + dy,
            };
            if (lineIndex < 0 || lineIndex >= this.lines.Count)
            {
                var past = lineIndex >= this.lines.Count;
                lineIndex = past ? this.lines.Count - 1 : 0;
                if ((flags & ImTextMoveFlags.BlockClampKeepInline) == 0)
                    inline = past ? ImTextMoveFlags.InlineEnd : ImTextMoveFlags.InlineBegin;
            }

            var line = this.lines[lineIndex];
            switch (inline)
            {
                case ImTextMoveFlags.InlineBegin:
                    target = line.TextBegin;
                    break;
                case ImTextMoveFlags.InlineEnd:
                    target = line.EndsWithNewline ? line.TextEnd : this.LineEndOffset(line);
                    break;
                default:
                    var pos = preferredInlinePos >= 0 ? preferredInlinePos : from.InlinePos;
                    target = -1;
                    for (var k = line.ItemBegin; k < line.ItemEnd && target < 0; k++)
                    {
                        ref readonly var item = ref this.ItemAt(k);
                        target = item.Kind == ItemKind.Newline ? item.Offset : this.OffsetInItem(item, pos);
                    }

                    if (target < 0)
                        target = this.LineEndOffset(line);
                    break;
            }
        }

        if (dx == 0)
            return this.MakeCaret(target, lineIndex);

        var offset = this.StepUnits(target, flags & ImTextMoveFlags.UnitMask, dx);
        if ((flags & ImTextMoveFlags.DxWithinLine) != 0)
        {
            var line = this.lines[lineIndex];
            var end = line.EndsWithNewline ? line.TextEnd : this.LineEndOffset(line);
            return this.MakeCaret(Math.Clamp(offset, line.TextBegin, end), lineIndex);
        }

        return this.MakeCaret(offset, this.LineOf(offset, false));
    }

    private static bool IsWordChar(uint c) =>
        c >= 0x80 || c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_';

    // Inline position of the grapheme boundary at or before 'offset', which is within the item.
    private float InlinePosInItem(in Item item, int offset)
    {
        var n = this.GraphemeCount(item);
        if (n <= 1)
            return item.X;
        var k = -1;
        for (var cp = item.CpBegin; cp < item.CpEnd; cp++)
        {
            if (!this.IsGraphemeStart(cp))
                continue;
            if (this.cpOffsets[cp] > offset)
                break;
            k++;
        }

        return item.X + ((item.Advance - item.Kern) * Math.Max(k, 0) / n);
    }

    // Offset of the grapheme boundary within the item nearest to 'u', or -1 if 'u' is past the middle of its last
    // grapheme.
    private int OffsetInItem(in Item item, float u)
    {
        var n = item.Kind == ItemKind.Ellipsis ? 1 : this.GraphemeCount(item);
        if (n <= 1)
            return u < item.X + (item.Advance * 0.5f) ? item.Offset : -1;
        var w = (item.Advance - item.Kern) / n;
        var k = 0;
        for (var cp = item.CpBegin; cp < item.CpEnd; cp++)
        {
            if (!this.IsGraphemeStart(cp))
                continue;
            if (u < item.X + (w * (k + 0.5f)))
                return this.cpOffsets[cp];
            k++;
        }

        return -1;
    }

    private int LineEndOffset(in Line line)
    {
        if (line.ItemEnd <= line.ItemBegin)
            return line.TextBegin;
        ref readonly var last = ref this.ItemAt(line.ItemEnd - 1);
        return Math.Max(line.TextEnd, last.Kind == ItemKind.Newline ? line.TextEnd : last.Offset + last.Length);
    }

    private float InlinePosAt(in Line line, int offset)
    {
        for (var i = line.ItemBegin; i < line.ItemEnd; i++)
        {
            ref readonly var item = ref this.ItemAt(i);
            if (item.Kind == ItemKind.Ellipsis || offset <= item.Offset)
                return item.X;
            if (offset < item.Offset + item.Length)
                return this.InlinePosInItem(item, offset);
        }

        return line.FullWidth;
    }

    private int LineOf(int offset, bool preferPreviousLine)
    {
        for (var n = 0; n + 1 < this.lines.Count; n++)
        {
            var next = this.lines[n + 1].TextBegin;
            if (offset < next || (preferPreviousLine && !this.lines[n].EndsWithNewline && offset == next))
                return n;
        }

        return this.lines.Count - 1;
    }

    private ImTextCaret MakeCaret(int offset, int lineIndex)
    {
        var line = this.lines[lineIndex];
        var lineEnd = this.LineEndOffset(line);
        offset = Math.Clamp(offset, line.TextBegin, Math.Max(line.TextNext, lineEnd));
        var inlinePos = offset >= lineEnd && !line.EndsWithNewline ? line.FullWidth : this.InlinePosAt(line, offset);
        if (line.EndsWithNewline && offset >= line.TextEnd)
            inlinePos = line.Width;
        var caret = new ImTextCaret
        {
            Pos = this.vertical
                      ? new(this.Size.X - line.BlockPos - line.BlockSize, inlinePos)
                      : new(inlinePos, line.BlockPos),
            Length = line.BlockSize,
            InlinePos = inlinePos,
            TextOffset = offset,
            LineIndex = lineIndex,
            IsPreviousLine = lineIndex + 1 < this.lines.Count && !line.EndsWithNewline &&
                             offset == this.lines[lineIndex + 1].TextBegin,
            RunIndex = -1,
            ObjectIndex = -1,
        };
        for (var i = line.ItemBegin; i < line.ItemEnd; i++)
        {
            if (this.ItemAt(i).Offset >= offset || i + 1 == line.ItemEnd)
            {
                ref readonly var item =
                    ref this.ItemAt(this.ItemAt(i).Offset > offset && i > line.ItemBegin ? i - 1 : i);
                caret.RunIndex = item.Run;
                caret.ObjectIndex = item.Object;
                break;
            }
        }

        caret.UserData = this.UserDataOf(caret.RunIndex, caret.ObjectIndex);
        return caret;
    }

    private int StepGrapheme(int offset, int dir)
    {
        var n = this.cpCount;
        var index = this.CpIndex(offset);
        if (dir > 0)
        {
            index = this.NextGrapheme(index, n);
        }
        else
        {
            if (this.cpOffsets[index] == offset)
                index = Math.Max(index - 1, 0);
            while (index > 0 && !this.IsGraphemeStart(index))
                index--;
        }

        return this.cpOffsets[index];
    }

    // 'count' units along the text from 'offset' (sign = direction), within the laid out text.
    private int StepUnits(int offset, ImTextMoveFlags unit, int count)
    {
        var textLength = Math.Min(this.textBytes, this.TextLength);
        var dir = count < 0 ? -1 : 1;
        for (var n = Math.Abs(count); n > 0; n--)
        {
            switch (unit)
            {
                case ImTextMoveFlags.Codepoint:
                    offset = this.StepCodepoint(offset, dir);
                    break;
                case ImTextMoveFlags.Grapheme:
                    offset = this.StepGrapheme(offset, dir);
                    break;
                default:
                    // WordStart skips the current word, then blanks and punctuation. WordEnd skips them, then the word.
                    if (dir > 0)
                    {
                        var first = unit != ImTextMoveFlags.WordStart;
                        while (offset < textLength && IsWordChar(this.CharAt(offset)) != first)
                            offset = this.StepGrapheme(offset, 1);
                        while (offset < textLength && IsWordChar(this.CharAt(offset)) == first)
                            offset = this.StepGrapheme(offset, 1);
                    }
                    else
                    {
                        var first = unit == ImTextMoveFlags.WordStart;
                        while (offset > 0 && IsWordChar(this.CharAt(this.StepGrapheme(offset, -1))) != first)
                            offset = this.StepGrapheme(offset, -1);
                        while (offset > 0 && IsWordChar(this.CharAt(this.StepGrapheme(offset, -1))) == first)
                            offset = this.StepGrapheme(offset, -1);
                    }

                    break;
            }
        }

        return Math.Clamp(offset, 0, textLength);
    }

    private int StepCodepoint(int offset, int dir)
    {
        var index = this.CpIndex(offset);
        if (dir > 0)
            index = Math.Min(index + 1, this.cpCount);
        else if (this.cpOffsets[index] == offset)
            index = Math.Max(index - 1, 0);
        return this.cpOffsets[index];
    }

    private uint CharAt(int offset)
    {
        var index = this.CpIndex(offset);
        return index < this.cpCount ? this.cps[index] : 0;
    }
}
