using System.Text;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Fonts;
using Dalamud.Interface.Fonts.Internal;
using Dalamud.Interface.Utility.Raii;

namespace Dalamud.Interface.Internal.Windows.Data.Widgets;

/// <summary>Shows samples of fonts drawn with ImGui.</summary>
internal class FontSamplesImGuiWidget : IDataWindowWidget, ITextMarkupCallback
{
    private const string CjkTestString = "C气氣J気K기";

    private const uint LinkColor = 0xFFFFA64D;
    private const uint HoverLinkColor = 0xFFFFC880;
    private const uint ActiveLinkColor = 0xFF5050FF;

    private static readonly MultiFontSpec BoldItalicSample = FontSpec.From([
        ("Segoe UI", FontStyle.Bold | FontStyle.Italic),
        "Gulim",
    ]);

    // Red to violet in ImGui's ABGR order, from the outermost band.
    private static readonly uint[] RainbowColors =
        [0xFF0000FF, 0xFF00A5FF, 0xFF00FFFF, 0xFF00FF00, 0xFFFF0000, 0xFF82004B, 0xFFEE82EE];

    // Registered once, for as long as Dalamud runs.
    private static ImTextDecorationId rainbowEdge;

    private float relativeSize = 1.5f;
    private float pixelSize = 24f;
    private int labelClicks;

    /// <inheritdoc/>
    public string[]? CommandShortcuts { get; init; } = ["fontspecs"];

    /// <inheritdoc/>
    public string DisplayName { get; init; } = "Font: Samples(ImGui)";

    /// <inheritdoc/>
    public bool Ready { get; set; }

    /// <inheritdoc/>
    public void Load() => this.Ready = true;

    /// <inheritdoc/>
    public void Draw()
    {
        if (ImGui.CollapsingHeader("Samples"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            Sample("Default"u8, FontSpec.Default, $"The quick brown fox jumps over the lazy dog. {CjkTestString}");
            Sample(
                "Icon"u8,
                FontSpec.Icon,
                $"{FontAwesomeIcon.Cog.ToIconString()}{FontAwesomeIcon.Search.ToIconString()}{FontAwesomeIcon.Home.ToIconString()}");
            Sample(
                "IconFixedWidth"u8,
                FontSpec.IconFixedWidth,
                $"{FontAwesomeIcon.Cog.ToIconString()}{FontAwesomeIcon.Search.ToIconString()}{FontAwesomeIcon.Home.ToIconString()}");
            Sample("Emoji"u8, FontSpec.Default, "😀👍🎉❤️🍣 🇯🇵 👨‍👩‍👧 ✋🏽");
            Sample("Segoe UI Emoji 32px"u8, FontSpec.From("Segoe UI Emoji", sizePx: 32), "😀👍🎉❤️🍣");
            Sample("Mono"u8, FontSpec.Mono, $"0123456789 iIlL1 oO0 -> {CjkTestString}");
            Sample("AXIS 18pt"u8, FontSpec.From(GameFontFamily.Axis) with { SizePt = 18 }, "The quick brown fox");
            Sample(
                "TrumpGothic 23pt"u8,
                FontSpec.From(GameFontFamily.TrumpGothic) with { SizePt = 23 },
                "The quick brown fox");
            Sample("Segoe UI(BI)+Gulim"u8, BoldItalicSample, $"Bold italic 굴림 {CjkTestString}");

            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
            ImGui.SliderFloat("Relative size"u8, ref this.relativeSize, 0.5f, 3f, "%.2fx");
            Sample(
                "Default, relative"u8,
                FontSpec.Default.WithRelativeSize(this.relativeSize),
                "Follows the current font size");
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
            ImGui.SliderFloat("Pixel size"u8, ref this.pixelSize, 8f, 64f, "%.0f px");
            using (FontSpec.Default.Push(this.pixelSize))
                ImGui.Text("Default, pushed at a size (same font, other size)"u8);
        }

        do
        {
            using (ImGui.PushMacroString())
            {
                if (!ImGui.CollapsingHeader(
                        "<edge(1)><colortype(501)><edgecolortype(502)>T<edgecolortype(0)><colortype(0)><edge(0)>"u8 +
                        "<bold(1)><color(0xFFFF8888)>e<color(stackcolor)><bold(0)>"u8 +
                        "<italic(1)><color(0xFF88FF88)>x<color(stackcolor)><italic(0)>"u8 +
                        "<color(0xFF8888FF)>t<color(stackcolor)>"u8 +
                        " styles"u8,
                        ImGuiTreeNodeFlags.DefaultOpen))
                    break;
            }

            using (ImGui.PushTextColor(ImGuiColors.TankBlue))
                ImGui.Text("PushTextColor"u8);
            using (ImGui.PushTextEdge(ImGuiColors.HealerGreen))
                ImGui.Text("PushTextEdge"u8);
            using (ImGui.PushTextEdge(ImGuiColors.DPSRed, 0.2f, 1))
                ImGui.Text("PushTextEdge, 0.2 em and soft"u8);
            using (ImGui.PushShadow(0xFF000000))
                ImGui.Text("PushShadow"u8);
            using (ImGui.PushShadow(0xC0000000, new(0.1f, 0.1f)))
                ImGui.Text("PushShadow, 0.1 em to the bottom right"u8);
            using (ImGui.PushStrikethrough(0xFF4040FF))
                ImGui.Text("PushStrikethrough"u8);
            using (ImGui.PushUnderline(0xFFFFFFFF))
                ImGui.Text("PushUnderline"u8);
            using (ImGui.PushBackground(0x804080FF))
                ImGui.Text("PushBackground"u8);
            using (ImGui.PushOverline())
                ImGui.Text("PushOverline"u8);
            using (ImGui.PushLines(ImTextLinePosition.Under, color: 0xFF40C0FF, count: 2))
                ImGui.Text("Double underline"u8);
            using (ImGui.PushLines(ImTextLinePosition.Under, ImTextLineStyle.Dotted))
                ImGui.Text("Dotted underline"u8);
            using (ImGui.PushLines(ImTextLinePosition.Under | ImTextLinePosition.Over, ImTextLineStyle.Dashed))
                ImGui.Text("Dashed underline and overline"u8);
            using (ImGui.PushLines(ImTextLinePosition.Under, ImTextLineStyle.Wavy, 0xFF4040FF))
                ImGui.Text("Wavy underline"u8);
            using (ImGui.PushLines(ImTextLinePosition.Through, ImTextLineStyle.Wavy, count: 3))
                ImGui.Text("Three wavy strikethroughs"u8);
            // The built-in decorations again as ExtraDecoration, on top of Lines and Background.
            var wavy = new ImTextLineDecoration(ImTextLinePosition.Under, ImTextLineStyle.Wavy, 0xFF4040FF);
            using (ImGui.PushUnderline())
            using (ImGui.PushStrikethrough())
            using (ImGui.PushDecoration(ImTextDecorationId.Line, wavy.Color, wavy.ToDecoration().Data))
                ImGui.Text("Underline and strikethrough, and a wavy underline as ExtraDecoration"u8);
            using (ImGui.PushTextStyle(
                       ImGui.TextStyle with
                       {
                           Background = 0x804080FF,
                           ExtraDecoration = new(ImTextDecorationId.Background, 0x40FFFFFF),
                       }))
            {
                ImGui.Text("Background, and another over it as ExtraDecoration"u8);
            }

            ImGui.TextStyledRanges(
                "Ranges: highlighted, then squiggled"u8,
                [
                    new(8, 11, ImGui.TextStyle with { Color = 0xFF40C0FF }),
                    new(26, 9, ImGui.TextStyle with { ExtraDecoration = wavy }),
                ]);
            if (rainbowEdge.IsNone)
            {
                rainbowEdge = TextDecorationRegistry.Register(
                    DrawRainbowEdge,
                    TextDecorationFlags.BelowFill,
                    RainbowColors.Length);
            }

            using (ImGui.PushDecoration(rainbowEdge))
                ImGui.Text("Rainbow edge, a decoration drawing the glyphs 7 times under their fill"u8);
            using (ImGui.PushBold())
                ImGui.Text($"PushBold {CjkTestString}");
            using (ImGui.PushItalic())
                ImGui.Text($"PushItalic {CjkTestString}");
            using (ImGui.PushBold())
            using (ImGui.PushItalic())
            using (ImGui.PushTextEdge(0xFF000000))
            using (ImGui.PushShadow(0xFF000000))
                ImGui.Text("Bold, italic, edge and shadow, nested"u8);

            var seString =
                "SeString: <colortype(500)>color<colortype(0)> <icon(56)> <italic(1)>italic<italic(0)> "u8 +
                @"<edge(1)>edge<edge(0)> <link(0x0E,1,2,3,test)>a link<link(0xCE)> \#\#escaped"u8;
            ImGui.MacroString(seString, this);
            if (ImGui.TryGetTextInteraction(out var interaction, TextInteractionFlags.All))
            {
                if ((interaction.Flags & TextInteractionFlags.Hover) != 0)
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SameLine();
                var link = seString.Slice(interaction.Offset, interaction.Length);
                ImGui.Text($"{interaction.Flags}: {Encoding.UTF8.GetString(link)}");
            }

            // A label with an explicit ID shows "##" in its text as it is, instead of taking it as the ID.
            using (ImGui.PushMacroString())
            {
                if (ImGui.Button(new("<colortype(500)>##<colortype(0)> in a macro string label"u8, "macroLabel"u8)))
                    this.labelClicks++;
                ImGui.SameLine();

                // "###" keeps the ID as the text changes, so the node stays open.
                using var node = ImRaii.TreeNode(
                    new($"<italic(1)>##{this.labelClicks}<italic(0)> clicks", "###macroLabelNode"u8));
                if (node)
                    ImGui.Text("C# and #1 ## #2 show as they are."u8);
            }

            using (FontSpec.From("Comic Sans MS").Push(this.pixelSize))
                StyleSamples("Comic Sans: "u8);
            using (FontSpec.From(GameFontFamily.Axis).Push(this.pixelSize))
                StyleSamples("AXIS: "u8);
            using (FontSpec.From("AXIS Basic ProN", FontStyle.Default).Push(this.pixelSize))
                StyleSamples("AXIS Basic ProN: "u8);
        }
        while (false);
    }

    /// <inheritdoc/>
    ImFontPtr ITextMarkupCallback.GetLinkFont(in TextLinkContext link, ImFontPtr font)
    {
        var style = font.Style;
        style.Lines = new(ImTextLinePosition.Under);
        style.Color = link.State switch
        {
            TextLinkState.Hovered => HoverLinkColor,
            TextLinkState.Held => ActiveLinkColor,
            _ => LinkColor,
        };
        if (link.Held && ImGui.IsMouseDown(ImGuiMouseButton.Right))
            style.Background = ImGui.GetColorU32(ImGuiCol.TextSelectedBg);
        return font.WithStyleForFrame(style);
    }

    // A band of the rainbow, a pixel narrower than the one before.
    private static void DrawRainbowEdge(in TextDecorationSpan span) =>
        span.DrawGlyphs(RainbowColors[span.Band], RainbowColors.Length - span.Band);

    private static void StyleSamples(ReadOnlySpan<byte> label)
    {
        ImGui.Text(label);
        ImGui.SameLine(0, 0);
        using (ImGui.PushBold())
            ImGui.Text("Bold "u8);
        ImGui.SameLine(0, 0);
        using (ImGui.PushBold())
        using (ImGui.PushItalic())
            ImGui.Text("and "u8);
        ImGui.SameLine(0, 0);
        using (ImGui.PushStrikethrough())
            ImGui.Text("Brash"u8);
        ImGui.SameLine(0, 0);
        using (ImGui.PushItalic())
            ImGui.Text("Italic "u8);
    }

    private static void Sample(ReadOnlySpan<byte> label, IFontSpec spec, string text)
    {
        ImGui.Text(label);
        ImGui.SameLine(ImGui.GetFontSize() * 10);
        using (spec.Push())
            ImGui.Text(text);
    }
}
