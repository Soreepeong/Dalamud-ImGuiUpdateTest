using System.Text;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Fonts;
using Dalamud.Interface.Fonts.Internal;
using Dalamud.Interface.Utility.Raii;

namespace Dalamud.Interface.Internal.Windows.Data.Widgets;

/// <summary>Shows the fonts made from font specs (<see cref="FontResolver"/>).</summary>
internal class FontSpecsWidget : IDataWindowWidget
{
    private const string CjkTestString = "C气氣J気K기";

    private static readonly MultiFontSpec BoldItalicSample = FontSpec.From([
        ("Segoe UI", FontStyle.Bold | FontStyle.Italic),
        "Gulim",
    ]);

    private float relativeSize = 1.5f;
    private float pixelSize = 24f;

    /// <inheritdoc/>
    public string[]? CommandShortcuts { get; init; } = ["fontspecs"];

    /// <inheritdoc/>
    public string DisplayName { get; init; } = "Font Specs";

    /// <inheritdoc/>
    public bool Ready { get; set; }

    /// <inheritdoc/>
    public void Load() => this.Ready = true;

    /// <inheritdoc/>
    public void Draw()
    {
        var resolver = Service<FontResolver>.Get();

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
            ImGui.MacroString(seString);
            if (ImGui.TryGetSeStringInteraction(out var interaction, SeStringInteractionFlags.All))
            {
                ImGui.SameLine();
                ImGui.Text(
                    $"{interaction.Flags}: {Encoding.UTF8.GetString(seString.Slice(interaction.PayloadOffset, interaction.PayloadLength))}");
            }

            using (FontSpec.From("Comic Sans MS").Push(this.pixelSize))
                StyleSamples("Comic Sans: "u8);
            using (FontSpec.From(GameFontFamily.Axis).Push(this.pixelSize))
                StyleSamples("AXIS: "u8);
            using (FontSpec.From("AXIS Basic ProN", FontStyle.Default).Push(this.pixelSize))
                StyleSamples("AXIS Basic ProN: "u8);
        }
        while (false);

        if (!ImGui.CollapsingHeader("Fonts"u8, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var entries = resolver.Snapshot();
        ImGui.Text($"{entries.Length} fonts; frame {resolver.Frame}");
        using var table = ImRaii.Table("##fonts"u8, 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY);
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Spec"u8, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("State"u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("ImFont*"u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Held"u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Unused for"u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Follows default"u8, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableHeadersRow();
        foreach (var e in entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(e.Spec.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(e.State);
            ImGui.TableNextColumn();
            ImGui.Text(e.Pointer == 0 ? "-" : $"{e.Pointer:X}");
            ImGui.TableNextColumn();
            ImGui.Text(e.Held ? "Yes" : "No");
            ImGui.TableNextColumn();
            ImGui.Text($"{e.FramesSinceUse} frames");
            ImGui.TableNextColumn();
            ImGui.Text(e.FollowsDefault ? "Yes" : "No");
        }
    }

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
