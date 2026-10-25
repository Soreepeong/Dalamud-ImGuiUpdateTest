using System.Numerics;
using System.Text;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Fonts.Internal.Layout;
using Dalamud.Interface.Fonts.Internal.Markup;
using Dalamud.Interface.Fonts.Internal.SeStrings.Formats;
using Dalamud.Interface.Fonts.Internal.Sources;
using Lumina.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

using static Dalamud.Game.Text.BitmapFontIcon;

namespace Dalamud.Interface.Fonts.Internal.SeStrings;

/// <summary>Reads a SeString into styled text. It makes the text displayed, styled runs, inline objects for icons and
/// entities, and links.</summary>
/// <remarks>Main thread only.</remarks>
internal sealed unsafe class SeStringParser
{
    private const int SoftHyphen = 0xAD;
    private const int NoBreakSpace = 0xA0;
    private const int ObjectReplacementCharacter = 0xFFFC;

    private readonly GfdFileView gfd;
    private readonly SeStringColorStackSet colorStacks;
    private readonly TextEntityDrawDelegate drawIcon;
    private StyledText output = null!;
    private int sourceOffset;
    private ulong currentUserData;
    private ImTextStyle currentStyle;
    private int currentRunBegin;
    private bool hasRun;

    private ImFontPtr font;
    private Font? managedFont;
    private float fontSize;
    private float blockSize;
    private FontMetrics metrics;
    private ITextMarkupCallback[] callbacks = [];
    private ImTextMarkup markup;
    private int themeIndex;
    private ImTextEdge edgeTemplate;
    private Vector2 shadowOffset;
    private ImTextStyle startStyle;
    private (int Link, ImTextStyle Style, ImTextStyle LinkStyle) lastLinkStyle = (-1, default, default);
    private bool edge;
    private bool shadow;
    private bool bold;
    private bool italic;

    /// <summary>Initializes a new instance of the <see cref="SeStringParser"/> class.</summary>
    /// <param name="gfd">The bitmap font icon table.</param>
    /// <param name="colorTypes">Colors to use with <see cref="MacroCode.ColorType"/>.</param>
    /// <param name="drawIcon">Draws an icon of <paramref name="gfd"/>, whose ID is the entity data.</param>
    public SeStringParser(GfdFileView gfd, uint[,] colorTypes, TextEntityDrawDelegate drawIcon)
    {
        this.gfd = gfd;
        this.colorStacks = new(colorTypes);
        this.drawIcon = drawIcon;
    }

    /// <summary>Reads a SeString.</summary>
    /// <param name="seString">The SeString, which becomes <see cref="StyledText.Source"/>.</param>
    /// <param name="textMarkup">The markup the text is read for.</param>
    /// <param name="styledFont">The font, whose style the text starts with.</param>
    /// <param name="desc">Layout parameters.</param>
    /// <param name="chain">The callbacks, in order.</param>
    /// <param name="theme">The theme for <see cref="MacroCode.ColorType"/> and <see cref="MacroCode.EdgeColorType"/>.
    /// </param>
    /// <param name="styled">Receives the styled text, cleared.</param>
    /// <remarks>It keeps no reference to the callbacks or the styled text afterwards, so that plugins can unload.
    /// </remarks>
    public void Parse(
        byte[] seString, ImTextMarkup textMarkup, ImFontPtr styledFont, in TextLayoutDesc desc,
        ITextMarkupCallback[] chain, int theme, StyledText styled)
    {
        try
        {
            this.ParseCore(seString, textMarkup, styledFont, desc, chain, theme, styled);
        }
        finally
        {
            this.output = null!;
            this.callbacks = [];
            this.font = default;
            this.managedFont = null;
        }
    }

    // The bitmap icon to display for an icon payload, with gamepad key mapping for Icon2.
    private static BitmapFontIcon GetBitmapFontIconFor(ReadOnlySePayloadSpan payload)
    {
        if (!payload.TryGetExpression(out var icon) || !icon.TryGetInt(out var iconId))
            return None;
        if (payload.MacroCode == MacroCode.Icon2)
            iconId = SeStringGameData.RemapIcon2(iconId);
        return (BitmapFontIcon)iconId;
    }

    // Whether a CR at an offset of a text is followed by an LF, which ends the line instead.
    // The top of an object centered in the line, above the baseline. A line places its baseline in it as
    // FontMetrics.BaselineIn does, so the height of the line cancels out.
    private float CenteredTop(float height) =>
        MathF.Round(this.metrics.Ascent) - ((this.metrics.Ascent + this.metrics.Descent) / 2) + (height / 2);

    private void ParseCore(
        byte[] seString, ImTextMarkup textMarkup, ImFontPtr styledFont, in TextLayoutDesc desc,
        ITextMarkupCallback[] chain, int theme, StyledText styled)
    {
        this.output = styled;
        styled.Source = seString;
        this.hasRun = false;

        this.markup = textMarkup;
        this.font = styledFont;
        this.fontSize = desc.Size;
        this.managedFont = Font.FromNative(styledFont);
        this.metrics = this.managedFont?.GetMetrics(desc.Size, desc.Density) ?? default;
        this.blockSize = this.metrics.BlockSize(desc.LineHeight, desc.Size) is > 0 and var b ? b : desc.Size;
        this.callbacks = chain;
        this.themeIndex = theme;
        this.lastLinkStyle = (-1, default, default);

        // The font's style is where the text starts. Macros may turn on edges and shadows it does not have.
        ref readonly var style = ref styledFont.Style;
        var fontEdge = style.Edge;
        var fontShadow = style.Shadow;
        this.edge = fontEdge.IsOn;
        this.shadow = fontShadow.IsOn;
        this.colorStacks.Initialize(
            style.Color,
            this.edge ? fontEdge.Color : 0xFF000000,
            this.shadow ? fontShadow.Color : 0xFF000000);
        // Edges that macros turn on are the font's in their own color, or the default edge if the font has none.
        this.edgeTemplate = this.edge ? fontEdge : new(0xFF000000);
        this.shadowOffset = fontShadow.Offset != default ? fontShadow.Offset : ImTextShadow.DefaultOffset;

        // The background, lines and extra decoration stay as they are.
        this.startStyle = style;
        this.bold = style.Bold;
        this.italic = style.Italic;
        styled.DefaultStyle = this.StyleOf();

        var link = -1;
        var skipUntil = 0;
        foreach (var p in new ReadOnlySeStringSpan(seString).GetOffsetEnumerator())
        {
            if (p.Offset < skipUntil)
                continue;

            switch (p.Payload.Type)
            {
                case ReadOnlySePayloadType.Text:
                    // The same flags as the layout, so that a leading U+FEFF is kept as the layout keeps it.
                    var body = p.Payload.Body;
                    foreach (var c in UtfEnumerator.From(
                                 body,
                                 UtfEnumeratorFlags.DisrespectByteOrderMask | UtfEnumeratorFlags.Utf8))
                    {
                        var at = p.Offset + c.ByteOffset;
                        if (at < skipUntil)
                            continue;
                        this.sourceOffset = at;
                        if (this.TryAddEntity(seString, at, link, out var length))
                        {
                            skipUntil = at + length;
                            continue;
                        }

                        this.AddRune(c.EffectiveRune, link);
                    }

                    break;

                case ReadOnlySePayloadType.Macro:
                    this.sourceOffset = p.Offset;
                    if (this.TryAddEntity(seString, p.Offset, link, out var macroLength))
                    {
                        skipUntil = p.Offset + macroLength;
                        break;
                    }

                    if (!this.ShouldHandle(p.Payload) || this.HandleStyleAdjustingPayload(p.Payload))
                        break;

                    switch (p.Payload.MacroCode)
                    {
                        case MacroCode.NewLine:
                            this.AddRune(new('\n'), link);
                            break;

                        // Where lines may wrap, showing a hyphen if they do.
                        case MacroCode.SoftHyphen:
                            this.AddRune(new(SoftHyphen), link);
                            break;

                        case MacroCode.NonBreakingSpace:
                            this.AddRune(new(NoBreakSpace), link);
                            break;

                        case MacroCode.Hyphen:
                            this.AddRune(new('-'), link);
                            break;

                        case MacroCode.Link
                            when p.Payload.TryGetExpression(out var e) &&
                                 e.TryGetUInt(out var u) &&
                                 u == (uint)LinkMacroPayloadType.Terminator:
                            link = -1;
                            break;

                        case MacroCode.Link:
                        {
                            var length = p.Payload.EnvelopeByteLength;
                            link = styled.Links.Count;
                            styled.Links.Add(new(p.Offset, length, p.Offset, length));
                            break;
                        }

                        case MacroCode.Icon or MacroCode.Icon2
                            when GetBitmapFontIconFor(p.Payload) is var icon and not None &&
                                 this.gfd.TryGetEntry((uint)icon, out var gfdEntry):
                        {
                            var iconSize = gfdEntry.CalculateScaledSize(this.fontSize, out _);
                            var top = this.CenteredTop(iconSize.Y);
                            var entity = new TextEntity(p.Payload.EnvelopeByteLength, iconSize, this.drawIcon)
                            {
                                Data = gfdEntry.Id,
                            };

                            this.AddObject(
                                new(new(0, -top, MathF.Round(iconSize.X), iconSize.Y - top)),
                                link,
                                new(entity, p.Offset, default, iconSize));
                            break;
                        }

                        default:
                            break;
                    }

                    break;

                case ReadOnlySePayloadType.Invalid:
                default:
                    break;
            }
        }

        this.EndRun();
    }

    private bool ShouldHandle(ReadOnlySePayloadSpan payload)
    {
        foreach (var callback in this.callbacks)
        {
            if (callback is ISeStringCallback seStringCallback && seStringCallback.ShouldHandle(payload) is { } answer)
                return answer;
        }

        return true;
    }

    private bool HandleStyleAdjustingPayload(ReadOnlySePayloadSpan payload)
    {
        if (this.colorStacks.TryHandle(payload, this.themeIndex))
            return true;
        switch (payload.MacroCode)
        {
            case MacroCode.Bold when payload.TryGetExpression(out var e) && e.TryGetUInt(out var u):
                this.bold = u != 0;
                return true;
            case MacroCode.Italic when payload.TryGetExpression(out var e) && e.TryGetUInt(out var u):
                this.italic = u != 0;
                return true;
            case MacroCode.Edge when payload.TryGetExpression(out var e) && e.TryGetUInt(out var u):
                this.edge = u != 0;
                return true;
            case MacroCode.Shadow when payload.TryGetExpression(out var e) && e.TryGetUInt(out var u):
                this.shadow = u != 0;
                return true;
            default:
                return false;
        }
    }

    // The style of text now. Color 0 is the color the text is drawn with. Transparent colors hide the text. Edges and
    // shadows that are off are left at their defaults, so that equal styles are equal. Edge colors of payloads have no
    // alpha, so edges take the alpha of the edge the text started with.
    private ImTextStyle StyleOf()
    {
        var stacks = this.colorStacks;
        var color = stacks.Color;
        return new()
        {
            Color = color is 0 or >= 0x1000000 ? color : 0x00FFFFFFu,
            Background = this.startStyle.Background,
            Lines = this.startStyle.Lines,
            ExtraDecoration = this.startStyle.ExtraDecoration,
            Edge = (this.edge || stacks.HasAdditionalEdgeColor) && stacks.EdgeColor >= 0x1000000
                       ? this.edgeTemplate with
                       {
                           Color = (stacks.EdgeColor & 0x00FFFFFFu) | (this.edgeTemplate.Color & 0xFF000000u),
                       }
                       : default,
            Shadow = this.shadow && stacks.ShadowColor >= 0x1000000
                         ? new(stacks.ShadowColor, this.shadowOffset)
                         : default,
            Bold = this.bold,
            Italic = this.italic,
        };
    }

    // Asks the callbacks for an entity at a byte offset, and adds it.
    private bool TryAddEntity(ReadOnlySpan<byte> seString, int byteOffset, int link, out int byteLength)
    {
        byteLength = 0;
        if (this.callbacks.Length == 0)
            return false;
        var style = this.StyleOf();

        // The styled font is made only if a callback reads it.
        var context = this.managedFont is { } managed
                          ? new TextEntityContext(
                              this.markup, seString, byteOffset, managed, style, this.fontSize, 0, default, 0)
                          : new TextEntityContext(
                              this.markup, seString, byteOffset, this.font, this.fontSize, 0, default, 0);
        foreach (var callback in this.callbacks)
        {
            if (!callback.TryGetEntity(context, out var entity) || entity.Length == 0)
                continue;

            var size = entity.Size.Y < 0 ? entity.Size with { Y = -entity.Size.Y * this.blockSize } : entity.Size;

            // How far its top is above the baseline.
            var top = entity.Alignment switch
            {
                TextEntityAlignment.TextTop => this.metrics.Ascent,
                TextEntityAlignment.Baseline => size.Y,
                TextEntityAlignment.TextBottom => size.Y - this.metrics.Descent,
                _ => this.CenteredTop(size.Y),
            };
            this.AddObject(new(new(0, -top, size.X, size.Y - top)), link, new(entity, byteOffset, style, size));
            byteLength = entity.Length;
            return true;
        }

        return false;
    }

    private void AddRune(Rune rune, int link)
    {
        switch (rune.Value)
        {
            case 0 or char.MaxValue:
                return;

            case ObjectReplacementCharacter:
                rune = Rune.ReplacementChar;
                break;
        }

        this.StartRun(link);
        Span<byte> buf = stackalloc byte[4];
        this.output.Append(buf[..rune.EncodeToUtf8(buf)], this.sourceOffset);
    }

    private void AddObject(InlineObject obj, int link, TextObjectContent content)
    {
        this.StartRun(link);
        this.output.Objects.Add(obj with { UserData = (ulong)(link + 1) });
        this.output.Contents.Add(content);
        this.output.Append("\uFFFC"u8, this.sourceOffset);
    }

    private void StartRun(int link)
    {
        var style = link >= 0 ? this.LinkStyleOf(link, this.StyleOf()) : this.StyleOf();
        var userData = (ulong)(link + 1);
        if (this.hasRun && userData == this.currentUserData && style == this.currentStyle)
            return;
        this.EndRun();
        this.hasRun = true;
        this.currentStyle = style;
        this.currentUserData = userData;
        this.currentRunBegin = this.output.Text.Length;
    }

    // The style of a link in the style of the text around it, as the callbacks say (TextLinkState.Normal).
    private ImTextStyle LinkStyleOf(int link, ImTextStyle style)
    {
        if (this.callbacks.Length == 0)
            return style;
        if (this.lastLinkStyle.Link == link && this.lastLinkStyle.Style == style)
            return this.lastLinkStyle.LinkStyle;

        var linkStyle = style;
        var l = this.output.Links[link];
        var source = this.output.Source.AsSpan();
        var context = new TextLinkContext(
            this.markup,
            source,
            l.Offset,
            l.Length,
            source.Slice(l.TargetOffset, l.TargetLength),
            TextLinkState.Normal);
        foreach (var callback in this.callbacks)
        {
            if (callback.TryGetLinkStyle(context, style, out var s))
            {
                linkStyle = s with { Markup = style.Markup };
                break;
            }
        }

        this.lastLinkStyle = (link, style, linkStyle);
        return linkStyle;
    }

    private void EndRun()
    {
        var end = this.output.Text.Length;
        if (!this.hasRun || this.currentRunBegin == end)
            return;
        this.output.Runs.Add(new(this.currentRunBegin, end, this.currentStyle, this.currentUserData));
        this.currentRunBegin = end;
    }
}
