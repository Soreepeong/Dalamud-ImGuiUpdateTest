using System.Collections.Generic;

using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>Line breaking.</summary>
internal sealed partial class TextLayout
{
    // Lines from the items, each filled, measured, truncated if it must be, and placed below the one before.
    private void BreakLines(List<Item> src, Item ellipsisItem, int ellipsisCount)
    {
        var maxWidth = this.desc.MaxWidth > 0 ? this.desc.MaxWidth : float.MaxValue;
        var useEllipsis = (this.desc.Flags & ImTextLayoutFlags.Ellipsis) != 0 && maxWidth < float.MaxValue;
        var stopWidth = useEllipsis ? float.MaxValue : maxWidth;
        this.TextLength = this.textBytes;
        var blockPos = 0f;
        var i = 0;
        while (true)
        {
            var line = new Line
                { ItemBegin = this.items.Count, TextBegin = i < src.Count ? src[i].Offset : this.textBytes };
            line.EndsWithNewline = this.FillLine(src, ref i, line.ItemBegin, stopWidth, out var stopped);

            // Kerning towards the next line does not apply.
            this.DropLastKern(line.ItemBegin);
            line.ItemEnd = this.items.Count;
            this.MeasureLine(ref line, src, i, stopped);
            if (useEllipsis && line.Width > maxWidth)
                this.Truncate(ref line, ellipsisItem, ellipsisCount, maxWidth);
            this.PlaceLine(ref line, ref blockPos);

            if (stopped || ((this.desc.Flags & ImTextLayoutFlags.StopOnNewLine) != 0 &&
                            (line.EndsWithNewline || i < src.Count)))
            {
                if (!stopped)
                    this.TextLength = line.TextNext;
                break;
            }

            if (i >= src.Count && !line.EndsWithNewline)
                break;
        }

        var inlineMax = 0f;
        foreach (var l in this.lines)
            inlineMax = Math.Max(inlineMax, l.Width);
        this.Size = this.vertical ? new(blockPos, inlineMax) : new(inlineMax, blockPos);
    }

    // Places items from src[i] until a newline (returned true), the wrap width, or the end. Stops the text where an
    // item would pass stopWidth.
    private bool FillLine(List<Item> src, ref int i, int lineItemBegin, float stopWidth, out bool stopped)
    {
        var wrap = this.desc.WrapWidth;
        var breakBefore = this.Scratch.BreakBefore;
        var x = 0f;
        var breakAt = -1; // index into src where the next line may start
        var lineFirst = i;
        stopped = false;
        for (; i < src.Count; i++)
        {
            var item = src[i];
            if (item.Kind == ItemKind.Newline)
            {
                item.X = x;
                item.Advance = 0;
                this.items.Add(item);
                i++;
                return true;
            }

            if (i > lineFirst && breakBefore[item.CpBegin])
                breakAt = i;

            if (item.Cp == '\t' && !this.Masked && !this.vertical)
                item.Advance = this.TabAdvance(x, item.Advance);

            if (x + item.Advance - item.Kern > stopWidth && item.Kind != ItemKind.Blank)
            {
                this.TextLength = item.Offset;
                stopped = true;
                return false;
            }

            if (wrap > 0 && item.Kind != ItemKind.Blank && x + item.Advance - item.Kern > wrap && i > lineFirst)
            {
                var cut = breakAt > lineFirst ? breakAt : i;

                // Undo items already placed past the cut.
                while (this.items.Count > lineItemBegin && this.items[^1].Offset >= src[cut].Offset)
                    this.items.RemoveAt(this.items.Count - 1);
                this.ReshapeAtBreak(src, cut, lineItemBegin);

                // A soft hyphen the line wraps after shows as a hyphen.
                if (this.items.Count > lineItemBegin && this.items[^1].Kind == ItemKind.SoftHyphen)
                {
                    ref var hyphen = ref this.ItemAt(this.items.Count - 1);
                    hyphen.Kind = ItemKind.Glyph;
                    hyphen.Advance = hyphen.HyphenAdvance;
                }

                i = cut;
                return false;
            }

            item.X = x;
            x += item.Advance;
            this.items.Add(item);
        }

        return false;
    }

    // Takes the kerning towards what follows off the last item of the line, if any.
    private void DropLastKern(int lineItemBegin)
    {
        if (this.items.Count <= lineItemBegin)
            return;
        ref var last = ref this.ItemAt(this.items.Count - 1);
        last.Advance -= last.Kern;
        last.Kern = 0;
    }

    // The widths and text ends of a filled line. Trailing blanks count with WrapKeepBlanks only.
    private void MeasureLine(ref Line line, List<Item> src, int next, bool stopped)
    {
        var keepBlanks = (this.desc.Flags & ImTextLayoutFlags.WrapKeepBlanks) != 0;
        var endItem = line.ItemEnd - (line.EndsWithNewline ? 1 : 0);
        line.FullWidth = 0;
        for (var k = line.ItemBegin; k < endItem; k++)
            line.FullWidth = this.ItemAt(k).X + this.ItemAt(k).Advance;
        var trailing = endItem;
        if (!keepBlanks)
        {
            while (trailing > line.ItemBegin && this.ItemAt(trailing - 1).Kind == ItemKind.Blank)
                trailing--;
        }

        line.TextEnd = trailing > line.ItemBegin
                           ? this.ItemAt(trailing - 1).Offset + this.ItemAt(trailing - 1).Length
                           : line.TextBegin;
        if (line.EndsWithNewline && keepBlanks)
            line.TextEnd = this.ItemAt(line.ItemEnd - 1).Offset;
        line.Width = trailing > line.ItemBegin
                         ? this.ItemAt(trailing - 1).X + this.ItemAt(trailing - 1).Advance -
                           (trailing < endItem ? this.ItemAt(trailing - 1).Kern : 0)
                         : 0;
        line.TextNext = next < src.Count ? src[next].Offset : stopped ? this.TextLength : this.textBytes;
        if (line.EndsWithNewline)
            line.TextNext = this.ItemAt(line.ItemEnd - 1).Offset + this.ItemAt(line.ItemEnd - 1).Length;
    }

    // Truncates the line to maxWidth, including the ellipsis after what is kept.
    private void Truncate(ref Line line, in Item ellipsisItem, int ellipsisCount, float maxWidth)
    {
        var ellipsisAdvance = ellipsisItem.Advance;
        var endItem = line.ItemEnd - (line.EndsWithNewline ? 1 : 0);
        var keep = line.ItemBegin;
        while (keep < endItem &&
               this.ItemAt(keep).X + this.ItemAt(keep).Advance + (ellipsisAdvance * ellipsisCount) <= maxWidth)
            keep++;
        var cutOffset = keep < endItem ? this.ItemAt(keep).Offset : line.TextEnd;
        if (this.EllipsisOffset < 0)
            this.EllipsisOffset = cutOffset;
        var ex = keep > line.ItemBegin
                     ? this.ItemAt(keep - 1).X + this.ItemAt(keep - 1).Advance - this.ItemAt(keep - 1).Kern
                     : 0;
        this.items.RemoveRange(keep, this.items.Count - keep);
        this.DropLastKern(line.ItemBegin);
        for (var e = 0; e < ellipsisCount; e++)
        {
            var item = ellipsisItem;
            item.Offset = cutOffset;
            item.X = ex;
            ex += ellipsisAdvance;
            this.items.Add(item);
        }

        line.ItemEnd = this.items.Count;
        line.TextEnd = cutOffset;
        line.Width = line.FullWidth = ex;
    }

    // Block metrics come from the largest text on the line, whose size a multiplier is of, as in CSS, and grow to
    // fit inline objects. The line goes at blockPos, which moves past it.
    private void PlaceLine(ref Line line, ref float blockPos)
    {
        var maxSize = 0f;
        var maxFont = 0;
        for (var k = line.ItemBegin; k < line.ItemEnd; k++)
        {
            if (this.ItemAt(k).Size > maxSize)
            {
                maxSize = this.ItemAt(k).Size;
                maxFont = this.ItemAt(k).FontIndex;
            }
        }

        if (maxSize == 0)
            maxSize = this.desc.Size;
        var m = this.MetricsOf(maxFont, maxSize);
        line.BlockSize = this.desc.LineHeight switch
        {
            > 0 => this.desc.LineHeight,
            < 0 => -this.desc.LineHeight * maxSize,
            _ => m.LineHeight,
        };
        line.Baseline = m.BaselineIn(line.BlockSize);
        for (var k = line.ItemBegin; k < line.ItemEnd; k++)
        {
            ref readonly var item = ref this.ItemAt(k);
            if (item.Kind != ItemKind.Object)
                continue;
            var b = this.objects[item.Object].Bounds;
            if (this.vertical)
            {
                line.BlockSize = Math.Max(line.BlockSize, b.Z - b.X);
            }
            else
            {
                line.Baseline = Math.Max(line.Baseline, -b.Y);
                line.BlockSize = Math.Max(line.BlockSize, line.Baseline + b.W);
            }
        }

        line.BlockPos = blockPos;
        blockPos += line.BlockSize;
        for (var k = line.ItemBegin; k < line.ItemEnd; k++)
            this.ItemAt(k).Line = this.lines.Count;
        this.lines.Add(line);
    }

    // The advance of a tab at x from the line start to the next tab stop, given the advance of a space. A stop closer
    // than half a space is skipped, as editors do.
    private float TabAdvance(float x, float space)
    {
        var stop = space * (this.desc.TabSize > 0 ? this.desc.TabSize : 4);
        if (stop <= 0)
            return space;
        var next = (MathF.Floor(x / stop) + 1) * stop;
        if (next - x < space * 0.5f)
            next += stop;
        return next - x;
    }

    // A line ends before src[cut]. Where shaping across the cut is unsafe, shapes the segment's part at the end of
    // items and the rest from src[cut] again, each without the other as context.
    private void ReshapeAtBreak(List<Item> src, int cut, int lineItemBegin)
    {
        if (cut <= 0 || cut >= src.Count || !src[cut].UnsafeBefore || src[cut - 1].Segment != src[cut].Segment)
            return;
        var segment = src[cut].Segment;
        var cutCp = src[cut].CpBegin;
        var ctxBegin = src[cut].CtxBegin;
        var ctxEnd = src[cut].CtxEnd;
        var fresh = this.Scratch.Fresh;
        fresh.Clear();

        var lb = this.items.Count;
        while (lb > lineItemBegin && this.items[lb - 1].Segment == segment)
            lb--;
        if (lb < this.items.Count)
        {
            var lineCp = this.items[lineItemBegin].CpBegin;
            this.ShapeSegment(this.items[lb].CpBegin, cutCp, Math.Max(ctxBegin, lineCp), cutCp, fresh);
            var x = this.items[lb].X;
            this.items.RemoveRange(lb, this.items.Count - lb);
            foreach (var item in fresh)
            {
                var placed = item;
                placed.X = x;
                x += placed.Advance;
                this.items.Add(placed);
            }
        }

        var re = cut;
        while (re < src.Count && src[re].Segment == segment)
            re++;
        fresh.Clear();
        this.ShapeSegment(cutCp, src[re - 1].CpEnd, cutCp, ctxEnd, fresh);
        if (fresh.Count > 0 && re < src.Count)
        {
            var lastFresh = fresh[^1];
            this.KernBoundary(ref lastFresh, src[re]);
            fresh[^1] = lastFresh;
        }

        src.RemoveRange(cut, re - cut);
        src.InsertRange(cut, fresh);
    }
}
