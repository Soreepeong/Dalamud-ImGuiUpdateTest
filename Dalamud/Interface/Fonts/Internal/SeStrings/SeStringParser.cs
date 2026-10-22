using System.Numerics;
using System.Text;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Fonts.Internal.Formats;
using Dalamud.Interface.Fonts.Internal.Layout;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Utility.Unicode;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Text;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

using static Dalamud.Game.Text.SeStringHandling.BitmapFontIcon;

namespace Dalamud.Interface.Fonts.Internal.SeStrings;

/// <summary>Reads a SeString into styled text. It makes the text displayed, styled runs, inline objects for icons and
/// entities, and links.</summary>
/// <remarks>Main thread only.</remarks>
internal sealed unsafe class SeStringParser
{
    private const int SoftHyphen = 0xAD;
    private const int ObjectReplacementCharacter = 0xFFFC;

    private readonly GfdFileView gfd;
    private readonly SeStringColorStackSet colorStacks;
    private StyledText output = null!;
    private int sourceOffset;
    private ulong currentUserData;
    private ImTextStyle currentStyle;
    private int currentRunBegin;
    private bool hasRun;

    private ImFontPtr font;
    private float fontSize;
    private float lineHeight;
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
    public SeStringParser(GfdFileView gfd, uint[,] colorTypes)
    {
        this.gfd = gfd;
        this.colorStacks = new(colorTypes);
    }

    // The distance from the top of a line to its baseline, as the layouter places it.
    private float LineBaseline => this.metrics.BaselineIn(this.lineHeight);

    /// <summary>Gets the theme the callbacks ask for, or that the game is set to (Dark if the game is not up).
    /// </summary>
    /// <param name="callbacks">The callbacks, in order.</param>
    /// <returns>The theme.</returns>
    public static int ThemeIndexOf(ReadOnlySpan<ITextMarkupCallback> callbacks)
    {
        foreach (var callback in callbacks)
        {
            if (callback is ISeStringCallback { ThemeIndex: { } theme })
                return theme;
        }

        var stage = AtkStage.Instance();
        return stage is null || stage->AtkUIColorHolder is null ? 0 : stage->AtkUIColorHolder->ActiveColorThemeType;
    }

    /// <summary>Reads a SeString.</summary>
    /// <param name="seString">The SeString, which becomes <see cref="StyledText.Source"/>.</param>
    /// <param name="markup">The markup the text is read for.</param>
    /// <param name="styledFont">The font, whose style the text starts with.</param>
    /// <param name="size">The font size in pixels.</param>
    /// <param name="density">Device pixels per logical pixel.</param>
    /// <param name="chain">The callbacks, in order.</param>
    /// <param name="styled">Receives the styled text, cleared.</param>
    public void Parse(
        byte[] seString, ImTextMarkup markup, ImFontPtr styledFont, float size, float density,
        ITextMarkupCallback[] chain, StyledText styled)
    {
        this.output = styled;
        styled.Source = seString;
        this.hasRun = false;

        this.markup = markup;
        this.font = styledFont;
        this.fontSize = size;
        this.metrics = ManagedFont.FromNative(styledFont)?.GetMetrics(size, density) ?? default;
        this.lineHeight = this.metrics.LineHeight > 0 ? this.metrics.LineHeight : size;
        this.callbacks = chain;
        this.themeIndex = ThemeIndexOf(chain);
        this.lastLinkStyle = (-1, default, default);

        // The font's style is where the text starts. Macros may turn on edges and shadows it does not have.
        ref readonly var style = ref styledFont.Style;
        var edge = style.Edge;
        var shadow = style.Shadow;
        this.edge = edge.IsOn;
        this.shadow = shadow.IsOn;
        this.colorStacks.Initialize(
            style.Color,
            this.edge ? edge.Color : 0xFF000000,
            this.shadow ? shadow.Color : 0xFF000000);
        // Edges that macros turn on are the font's in their own color, or the default edge if the font has none.
        this.edgeTemplate = this.edge ? edge : new(0xFF000000);
        this.shadowOffset = shadow.Offset != default ? shadow.Offset : ImTextShadow.DefaultOffset;

        // The background, lines and extra decoration stay as they are.
        this.startStyle = style;
        this.bold = style.Bold;
        this.italic = style.Italic;
        styled.DefaultStyle = this.StyleOf();

        // This might temporarily return 0 while logging in.
        var gfdTextureSrv = GetGfdTextureSrv();
        var link = -1;
        var skipUntil = 0;
        foreach (var p in new ReadOnlySeStringSpan(seString).GetOffsetEnumerator())
        {
            if (p.Offset < skipUntil)
                continue;

            switch (p.Payload.Type)
            {
                case ReadOnlySePayloadType.Text:
                    foreach (var c in UtfEnumerator.From(p.Payload.Body, UtfEnumeratorFlags.Utf8))
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
                            var iconSize = gfdEntry.CalculateScaledSize(this.fontSize, out var useHq);
                            var top = this.LineBaseline - ((this.lineHeight - iconSize.Y) / 2);
                            this.AddObject(
                                new(new(0, -top, MathF.Round(iconSize.X), iconSize.Y - top)),
                                link,
                                new(
                                    gfdTextureSrv == 0 ? default : new ImTextureRef(new(gfdTextureSrv)),
                                    iconSize,
                                    useHq ? gfdEntry.HqUv0 : gfdEntry.Uv0,
                                    useHq ? gfdEntry.HqUv1 : gfdEntry.Uv1,
                                    default,
                                    0,
                                    default));
                            break;
                        }
                    }

                    break;
            }
        }

        this.EndRun();
    }

    private static nint GetGfdTextureSrv()
    {
        var uim = UIModule.Instance();
        if (uim is null)
            return 0;
        var ram = uim->GetRaptureAtkModule();
        if (ram is null)
            return 0;
        var gfd = ram->AtkModule.AtkFontManager.Gfd;
        if (gfd is null)
            return 0;
        var tex = gfd->Texture;
        return tex is null ? 0 : (nint)tex->D3D11ShaderResourceView;
    }

    // The bitmap icon to display for an icon payload, with gamepad key mapping for Icon2.
    private static BitmapFontIcon GetBitmapFontIconFor(ReadOnlySePayloadSpan payload)
    {
        if (!payload.TryGetExpression(out var icon) || !icon.TryGetInt(out var iconId))
            return None;
        if (payload.MacroCode == MacroCode.Icon2 && RaptureAtkModule.Instance() is var ram && ram is not null)
        {
            ref var iconMapping = ref ram->AtkFontManager.Icon2RemapTable;
            for (var i = 0; i < 30; i++)
            {
                if (iconMapping[i].IconId == iconId)
                    return (BitmapFontIcon)iconMapping[i].RemappedIconId;
            }
        }

        return (BitmapFontIcon)iconId;
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
    // shadows that are off are left at their defaults, so that equal styles are equal.
    private ImTextStyle StyleOf()
    {
        var stacks = this.colorStacks;
        var color = stacks.Color;
        return new()
        {
            Color = color == 0 || color >= 0x1000000 ? color : 0x00FFFFFFu,
            Background = this.startStyle.Background,
            Lines = this.startStyle.Lines,
            ExtraDecoration = this.startStyle.ExtraDecoration,
            Edge = (this.edge || stacks.HasAdditionalEdgeColor) && stacks.EdgeColor >= 0x1000000
                       ? this.edgeTemplate with { Color = stacks.EdgeColor }
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
        var context = new TextEntityContext(
            this.markup, seString, byteOffset, this.font.WithStyleForFrame(style), this.fontSize);
        foreach (var callback in this.callbacks)
        {
            if (!callback.TryGetEntity(context, out var entity) || !entity)
                continue;

            // Top at the top of the line.
            var top = this.LineBaseline;
            this.AddObject(
                new(new(0, -top, entity.Size.X, entity.Size.Y - top)),
                link,
                new(default, entity.Size, default, default, entity, byteOffset, style));
            byteLength = entity.Length;
            return true;
        }

        return false;
    }

    private void AddRune(Rune rune, int link)
    {
        switch (rune.Value)
        {
            case 0 or char.MaxValue or '\r':
                return;
            case ObjectReplacementCharacter:
                rune = Rune.ReplacementChar;
                break;
            default:
                if (UnicodeData.LineBreak[rune.Value] is UnicodeLineBreakClass.BK or UnicodeLineBreakClass.LF
                    or UnicodeLineBreakClass.NL)
                {
                    rune = new('\n');
                }

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
        var font = this.font.WithStyleForFrame(style);
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
            var linkFont = callback.GetLinkFont(context, font);
            if (!linkFont.IsNull)
            {
                linkStyle = linkFont.Style with { Markup = style.Markup };
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
        this.output.Runs.Add(new(this.currentRunBegin, end, null, 0, this.currentStyle, this.currentUserData));
        this.currentRunBegin = end;
    }
}
