using System.Linq;
using System.Text;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Fonts;
using Dalamud.Interface.Fonts.Internal;
using Dalamud.Interface.Utility.Raii;

namespace Dalamud.Interface.Internal.Windows.Data.Widgets;

/// <summary>Shows samples of fonts drawn with ImGui.</summary>
internal class FontSamplesImGuiWidget : IDataWindowWidget, ITextMarkupCallback, IDisposable
{
    private const uint LinkColor = 0xFFFFA64D;
    private const uint HoverLinkColor = 0xFFFFC880;
    private const uint ActiveLinkColor = 0xFF5050FF;

    private static readonly byte[] Icons = Encoding.UTF8.GetBytes(
        string.Concat(
            FontAwesomeIcon.Cog.ToIconString(),
            FontAwesomeIcon.Search.ToIconString(),
            FontAwesomeIcon.Home.ToIconString()));

    // Specs are made once, so that a spec stays one font. Load gets their fonts ahead, and the widget holds them. The
    // texts are UTF-8, so that drawing them converts nothing.
    private static readonly (byte[] Label, FontSpec Spec, byte[] Text)[] Samples =
    [
        (
            "Default"u8.ToArray(),
            FontSpec.Default,
            "The quick brown fox jumps over the lazy dog. C气氣J気K기"u8.ToArray()),
        ("Icon"u8.ToArray(), FontSpec.Icon, Icons),
        ("IconFixedWidth"u8.ToArray(), FontSpec.IconFixedWidth, Icons),
        ("Emoji"u8.ToArray(), FontSpec.Default, "😀👍🎉❤️🍣 🇯🇵 👨‍👩‍👧 ✋🏽"u8.ToArray()),
        (
            "Segoe UI Emoji 32px"u8.ToArray(),
            FontSpec.From("Segoe UI Emoji", sizePx: 32),
            "😀👍🎉❤️🍣"u8.ToArray()),
        ("Mono"u8.ToArray(), FontSpec.Mono, "0123456789 iIlL1 oO0 -> C气氣J気K기"u8.ToArray()),
        (
            "AXIS 18pt"u8.ToArray(),
            FontSpec.From(GameFontFamilyAndSize.Axis18),
            "The quick brown fox"u8.ToArray()),
        (
            "TrumpGothic 23pt"u8.ToArray(),
            FontSpec.From(GameFontFamilyAndSize.TrumpGothic23),
            "The quick brown fox"u8.ToArray()),
        (
            "Segoe UI(BI)+Gulim"u8.ToArray(),
            FontSpec.From([("Segoe UI", FontStyle.Bold | FontStyle.Italic), "Gulim"]),
            "Bold italic 굴림 C气氣J気K기"u8.ToArray()),
    ];

    // Pushed at the size of the slider. One font serves every size.
    private static readonly (byte[] Label, FontSpec Spec)[] StyleSampleFonts =
    [
        ("Comic Sans: "u8.ToArray(), FontSpec.From("Comic Sans MS")),
        ("AXIS: "u8.ToArray(), FontSpec.From(GameFontFamily.Axis)),
        ("AXIS Basic ProN: "u8.ToArray(), FontSpec.From("AXIS Basic ProN")),
    ];

    // Red to violet in ImGui's ABGR order, from the outermost band.
    private static readonly uint[] RainbowColors =
        [0xFF0000FF, 0xFF00A5FF, 0xFF00FFFF, 0xFF00FF00, 0xFFFF0000, 0xFF82004B, 0xFFEE82EE];

    // Registered on load and unregistered on dispose, as a plugin does through IFontService.
    private TextDecorationRegistration? rainbowEdge;
    private ImFontRef[]? sampleFonts;
    private ImFontRef[]? styleSampleFonts;
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
    public void Load()
    {
        this.LoadFonts();
        this.rainbowEdge ??= new(
            TextDecorationRegistry.Register(DrawRainbowEdge, TextDecorationFlags.BelowFill, RainbowColors.Length));
        this.Ready = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.rainbowEdge?.Dispose();
        this.rainbowEdge = null;
    }

    /// <inheritdoc/>
    public void Draw()
    {
        if (ImGui.CollapsingHeader("Samples"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (this.sampleFonts is not { } fonts)
            {
                ImGui.Text("Loading fonts..."u8);
            }
            else
            {
                for (var i = 0; i < Samples.Length; i++)
                    Sample(Samples[i].Label, fonts[i], Samples[i].Text);

                // A negative size is a ratio of the current size. Pushing at a size keeps one font for all sizes.
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
                ImGui.SliderFloat("Relative size"u8, ref this.relativeSize, 0.5f, 3f, "%.2fx");
                using (fonts[0].Push(-this.relativeSize))
                    ImGui.Text("Default, pushed at a ratio of the current size"u8);
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
                ImGui.SliderFloat("Pixel size"u8, ref this.pixelSize, 8f, 64f, "%.0f px");
                using (fonts[0].Push(this.pixelSize))
                    ImGui.Text("Default, pushed at a size (same font, other size)"u8);
            }
        }

        bool open;
        using (ImGui.PushTextMacroString())
        {
            open = ImGui.CollapsingHeader(
                "<edge(1)><colortype(501)><edgecolortype(502)>T<edgecolortype(0)><colortype(0)><edge(0)>"u8 +
                "<bold(1)><color(0xFFFF8888)>e<color(stackcolor)><bold(0)>"u8 +
                "<italic(1)><color(0xFF88FF88)>x<color(stackcolor)><italic(0)>"u8 +
                "<color(0xFF8888FF)>t<color(stackcolor)>"u8 +
                " styles"u8,
                ImGuiTreeNodeFlags.DefaultOpen);
        }

        if (!open)
            return;

        using (ImGui.PushTextColor(ImGuiColors.TankBlue))
            ImGui.Text("PushTextColor"u8);
        using (ImGui.PushTextEdge(ImGuiColors.HealerGreen))
            ImGui.Text("PushTextEdge"u8);
        using (ImGui.PushTextEdge(ImGuiColors.DPSRed, 0.2f, 1))
            ImGui.Text("PushTextEdge, 0.2 em and soft"u8);
        using (ImGui.PushTextShadow(0xFF000000))
            ImGui.Text("PushTextShadow"u8);
        using (ImGui.PushTextShadow(0xC0000000, new(0.1f, 0.1f)))
            ImGui.Text("PushTextShadow, 0.1 em to the bottom right"u8);
        using (ImGui.PushTextStrikethrough(0xFF4040FF))
            ImGui.Text("PushTextStrikethrough"u8);
        using (ImGui.PushTextUnderline(0xFFFFFFFF))
            ImGui.Text("PushTextUnderline"u8);
        using (ImGui.PushTextBackground(0x804080FF))
            ImGui.Text("PushTextBackground"u8);
        using (ImGui.PushTextOverline())
            ImGui.Text("PushTextOverline"u8);
        using (ImGui.PushTextLines(ImTextLinePosition.Under, color: 0xFF40C0FF, count: 2))
            ImGui.Text("Double underline"u8);
        using (ImGui.PushTextLines(ImTextLinePosition.Under, ImTextLineStyle.Dotted))
            ImGui.Text("Dotted underline"u8);
        using (ImGui.PushTextLines(ImTextLinePosition.Under | ImTextLinePosition.Over, ImTextLineStyle.Dashed))
            ImGui.Text("Dashed underline and overline"u8);
        using (ImGui.PushTextLines(ImTextLinePosition.Under, ImTextLineStyle.Wavy, 0xFF4040FF))
            ImGui.Text("Wavy underline"u8);
        using (ImGui.PushTextLines(ImTextLinePosition.Through, ImTextLineStyle.Wavy, count: 3))
            ImGui.Text("Three wavy strikethroughs"u8);
        // The built-in decorations again as ExtraDecoration, on top of Lines and Background.
        var wavy = new ImTextLineDecoration(ImTextLinePosition.Under, ImTextLineStyle.Wavy, 0xFF4040FF);
        using (ImGui.PushTextUnderline())
        using (ImGui.PushTextStrikethrough())
        using (ImGui.PushTextDecoration(ImTextDecorationId.Line, wavy.Color, wavy.ToDecoration().Data))
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

        ReadOnlySpan<byte> ranged = "Ranges: highlighted, then squiggled"u8;
        var highlighted = "highlighted"u8;
        var squiggled = "squiggled"u8;
        ImGui.TextStyledRanges(
            ranged,
            [
                new(ranged.IndexOf(highlighted), highlighted.Length, ImGui.TextStyle with { Color = 0xFF40C0FF }),
                new(ranged.IndexOf(squiggled), squiggled.Length, ImGui.TextStyle with { ExtraDecoration = wavy }),
            ]);
        if (this.rainbowEdge is { } rainbow)
        {
            using (ImGui.PushTextDecoration(rainbow.Id))
                ImGui.Text("Rainbow edge, a decoration drawing the glyphs 7 times under their fill"u8);
        }

        using (ImGui.PushTextBold())
            ImGui.Text("PushTextBold C气氣J気K기"u8);
        using (ImGui.PushTextItalic())
            ImGui.Text("PushTextItalic C气氣J気K기"u8);
        using (ImGui.PushTextBold())
        using (ImGui.PushTextItalic())
        using (ImGui.PushTextEdge(0xFF000000))
        using (ImGui.PushTextShadow(0xFF000000))
            ImGui.Text("Bold, italic, edge and shadow, nested"u8);

        var macroString =
            "Macro string: <colortype(500)>color<colortype(0)> <icon(56)> <italic(1)>italic<italic(0)> "u8 +
            @"<edge(1)>edge<edge(0)> <link(0x0E,1,2,3,test)>a link<link(0xCE)> \<escaped"u8;
        ImGui.TextMacroString(macroString, this);
        if (ImGui.TryGetTextInteraction(out var interaction, TextInteractionFlags.All))
        {
            if ((interaction.Flags & TextInteractionFlags.Hover) != 0)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SameLine();
            var link = macroString.Slice(interaction.DrawnOffset, interaction.DrawnLength);
            ImGui.Text($"{interaction.Flags}: {link}, target of {interaction.Target.Length} bytes");
        }

        // A label with an explicit ID shows "##" in its text as it is, instead of taking it as the ID.
        using (ImGui.PushTextMacroString())
        {
            if (ImGui.Button(new("<colortype(500)>##<colortype(0)> in a macro string label"u8, "macroLabel"u8)))
                this.labelClicks++;
            ImGui.SameLine();

            // Without the text in the ID, the ID stays as the text changes, so the node stays open.
            using var node = ImRaii.TreeNode(
                new($"<italic(1)>##{this.labelClicks}<italic(0)> clicks", "macroLabelNode"u8, appendId: false));
            if (node)
                ImGui.Text("C# and #1 ## #2 show as they are."u8);
        }

        if (this.styleSampleFonts is { } styleFonts)
        {
            for (var i = 0; i < StyleSampleFonts.Length; i++)
            {
                using (styleFonts[i].Push(this.pixelSize))
                    StyleSamples(StyleSampleFonts[i].Label);
            }
        }
    }

    /// <inheritdoc/>
    bool ITextMarkupCallback.TryGetLinkStyle(in TextLinkContext link, in ImTextStyle style, out ImTextStyle result)
    {
        result = style;
        result.Lines = new(ImTextLinePosition.Under);
        result.Color = link.State switch
        {
            TextLinkState.Hovered => HoverLinkColor,
            TextLinkState.Held => ActiveLinkColor,
            _ => LinkColor,
        };
        if (link.Held && ImGui.IsMouseDown(ImGuiMouseButton.Right))
            result.Background = ImGui.GetColorU32(ImGuiCol.TextSelectedBg);
        return true;
    }

    // A band of the rainbow, a pixel narrower than the one before.
    private static void DrawRainbowEdge(in TextDecorationSpan span) =>
        span.DrawGlyphs(RainbowColors[span.Band], RainbowColors.Length - span.Band);

    private static void StyleSamples(byte[] label)
    {
        ImGui.Text(label);
        ImGui.SameLine(0, 0);
        using (ImGui.PushTextBold())
            ImGui.Text("Bold "u8);
        ImGui.SameLine(0, 0);
        using (ImGui.PushTextBold())
        using (ImGui.PushTextItalic())
            ImGui.Text("and "u8);
        ImGui.SameLine(0, 0);
        using (ImGui.PushTextStrikethrough())
            ImGui.Text("Brash "u8);
        ImGui.SameLine(0, 0);
        using (ImGui.PushTextItalic())
            ImGui.Text("Italic "u8);
    }

    private static void Sample(byte[] label, ImFontRef font, byte[] text)
    {
        ImGui.Text(label);
        ImGui.SameLine(ImGui.GetFontSize() * 10);

        // A font that could not be loaded pushes as the current font, which would look like a working sample.
        if (font.LoadTask.IsFaulted)
        {
            var e = font.LoadTask.Exception!.InnerException!;
            ImGui.TextColored(ImGuiColors.DalamudRed, e.InnerException?.Message ?? e.Message);
            return;
        }

        using (font.Push())
            ImGui.Text(text);
    }

    // Starts loading the fonts ahead, and holds them, so that they are not released.
    private void LoadFonts()
    {
        this.styleSampleFonts = [.. StyleSampleFonts.Select(x => x.Spec.GetFont())];
        this.sampleFonts = [.. Samples.Select(x => x.Spec.GetFont())];
    }
}
