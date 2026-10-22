using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Data;
using Dalamud.Game.Gui;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Fonts;
using Dalamud.Interface.Fonts.Internal.SeStrings;
using Dalamud.Interface.Textures.Internal;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Internal;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Storage.Assets;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Excel.Sheets;
using Lumina.Text;
using Lumina.Text.Parse;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace Dalamud.Interface.Internal.Windows.Data.Widgets;

/// <summary>
/// Widget for displaying Addon Data.
/// </summary>
internal unsafe class SeStringRendererTestWidget : IDataWindowWidget, ISeStringCallback
{
    private static readonly string[] ThemeNames =
        ["Dark", "Light", "Classic FF", "Clear Blue", "Clear White", "Clear Green"];

    private ImVectorWrapper<byte> testStringBuffer;
    private string testString = string.Empty;
    private ReadOnlySeString? logkind;
    private ImTextStyle textStyle;
    private uint linkHoverColor;
    private uint linkActiveColor;
    private float opacity;
    private bool wrap;
    private uint edgeColor;
    private uint shadowColor;
    private bool edge;
    private bool shadow;
    private bool forceEdgeColor;
    private int? themeIndex;
    private bool interactable;
    private bool useEntity;
    private bool alignToFramePadding;

    /// <inheritdoc/>
    public string DisplayName { get; init; } = "SeStringRenderer Test";

    /// <inheritdoc/>
    public string[]? CommandShortcuts { get; init; }

    /// <inheritdoc/>
    public bool Ready { get; set; }

    /// <inheritdoc/>
    int? ISeStringCallback.ThemeIndex => this.themeIndex;

    /// <inheritdoc/>
    bool? ISeStringCallback.ShouldHandle(ReadOnlySePayloadSpan payload) =>
        this.forceEdgeColor && payload.MacroCode is MacroCode.EdgeColor or MacroCode.EdgeColorType ? false : null;

    /// <inheritdoc/>
    bool ISeStringCallback.TryGetEntity(in SeStringEntityContext context, out SeStringEntity entity)
    {
        entity = this.GetEntity(context);
        return entity;
    }

    /// <inheritdoc/>
    ImFontPtr ISeStringCallback.GetLinkFont(in SeStringLinkContext link, ImFontPtr font) =>
        font.WithStyle(font.Style with { BackgroundColor = link.Held ? this.linkActiveColor : this.linkHoverColor });

    /// <inheritdoc/>
    public void Load()
    {
        this.linkHoverColor = ImGui.GetColorU32(ImGuiCol.ButtonHovered);
        this.linkActiveColor = ImGui.GetColorU32(ImGuiCol.ButtonActive);
        this.opacity = 1;
        this.wrap = true;
        this.forceEdgeColor = false;
        this.themeIndex = null;
        this.textStyle = new()
        {
            EdgeWidth = ImFontExtensions.DefaultEdgeWidth,
            ShadowOffset = ImFontExtensions.DefaultShadowOffset,
        };
        this.edgeColor = this.shadowColor = 0xFF000000;
        this.edge = this.shadow = false;
        this.logkind = null;
        this.testString = string.Empty;
        this.interactable = this.useEntity = true;
        this.alignToFramePadding = false;
        this.Ready = true;
    }

    /// <inheritdoc/>
    public void Draw()
    {
        var t2 = ImGui.ColorConvertU32ToFloat4(
            this.textStyle.Color != 0 ? this.textStyle.Color : ImGui.GetColorU32(ImGuiCol.Text));
        if (ImGui.ColorEdit4("Color", ref t2))
            this.textStyle.Color = ImGui.ColorConvertFloat4ToU32(t2);

        t2 = ImGui.ColorConvertU32ToFloat4(this.edgeColor);
        if (ImGui.ColorEdit4("Edge Color", ref t2))
            this.edgeColor = ImGui.ColorConvertFloat4ToU32(t2);

        ImGui.SameLine();
        if (ImGui.Checkbox("Forced"u8, ref this.forceEdgeColor))
            SeStringCallbackStack.Invalidate(this);

        t2 = ImGui.ColorConvertU32ToFloat4(this.shadowColor);
        if (ImGui.ColorEdit4("Shadow Color"u8, ref t2))
            this.shadowColor = ImGui.ColorConvertFloat4ToU32(t2);

        t2 = ImGui.ColorConvertU32ToFloat4(this.linkHoverColor);
        if (ImGui.ColorEdit4("Link Hover Color"u8, ref t2))
            this.linkHoverColor = ImGui.ColorConvertFloat4ToU32(t2);

        t2 = ImGui.ColorConvertU32ToFloat4(this.linkActiveColor);
        if (ImGui.ColorEdit4("Link Active Color"u8, ref t2))
            this.linkActiveColor = ImGui.ColorConvertFloat4ToU32(t2);

        ImGui.DragFloat("Opacity"u8, ref this.opacity, 0.005f, 0f, 1f, "%.02f");

        ImGui.DragFloat("Edge Width (em)"u8, ref this.textStyle.EdgeWidth, 0.001f, 0f, 0.5f, "%.03f");
        ImGui.DragFloat("Edge Softness"u8, ref this.textStyle.EdgeSoftness, 0.01f, 0f, 2f, "%.02f");
        ImGui.DragFloat2("Shadow Offset (em)"u8, ref this.textStyle.ShadowOffset, 0.001f, -0.5f, 0.5f, "%.03f");

        ImGui.Checkbox("Edge"u8, ref this.edge);
        ImGui.SameLine();
        ImGui.Checkbox("Bold"u8, ref this.textStyle.Bold);
        ImGui.SameLine();
        ImGui.Checkbox("Italic"u8, ref this.textStyle.Italic);
        ImGui.SameLine();
        ImGui.Checkbox("Shadow"u8, ref this.shadow);
        this.textStyle.EdgeColor = this.edge ? this.edgeColor : 0;
        this.textStyle.ShadowColor = this.shadow ? this.shadowColor : 0;

        ImGui.SameLine();
        var t4 = this.themeIndex ?? AtkStage.Instance()->AtkUIColorHolder->ActiveColorThemeType;
        using (ImRaii.ItemWidth(ImGui.CalcTextSize("WWWWWWWWWWWWWW"u8).X))
        {
            if (ImGui.Combo("##theme", ref t4, ThemeNames))
            {
                this.themeIndex = t4;
                SeStringCallbackStack.Invalidate(this);
            }
        }

        ImGui.SameLine();
        ImGui.Checkbox("Word Wrap"u8, ref this.wrap);

        ImGui.Checkbox("Interactable"u8, ref this.interactable);

        ImGui.SameLine();
        if (ImGui.Checkbox("Use Entity Replacements"u8, ref this.useEntity))
            SeStringCallbackStack.Invalidate(this);

        ImGui.SameLine();
        ImGui.Checkbox("Align to Frame Padding"u8, ref this.alignToFramePadding);

        if (ImGui.CollapsingHeader("LogKind Preview"u8))
        {
            if (this.logkind is null)
            {
                var tt = new SeStringBuilder();
                foreach (var uc in Service<DataManager>.Get().GetExcelSheet<LogKind>())
                {
                    var ucsp = uc.Format.AsSpan();
                    if (ucsp.IsEmpty)
                        continue;

                    tt.Append($"#{uc.RowId}: ");
                    foreach (var p in ucsp.GetOffsetEnumerator())
                    {
                        if (p.Payload.Type == ReadOnlySePayloadType.Macro && p.Payload.MacroCode == MacroCode.String)
                        {
                            tt.Append("Text"u8);
                            continue;
                        }

                        tt.Append(new ReadOnlySeStringSpan(ucsp.Data.Slice(p.Offset, p.Payload.EnvelopeByteLength)));
                    }

                    tt.BeginMacro(MacroCode.NewLine).EndMacro();
                }

                this.logkind = tt.ToReadOnlySeString();
            }

            this.DrawSeString(this.logkind.Value.Data.Span, ImTextSeStringMode.Compiled);
        }

        if (ImGui.CollapsingHeader("Draw into drawlist"))
        {
            ImGuiHelpers.ScaledDummy(100);
            var clipMin = ImGui.GetItemRectMin() + ImGui.GetStyle().FramePadding;
            var clipMax = ImGui.GetItemRectMax() - ImGui.GetStyle().FramePadding;
            clipMin.Y = MathF.Max(clipMin.Y, ImGui.GetWindowPos().Y);
            clipMax.Y = MathF.Min(clipMax.Y, ImGui.GetWindowPos().Y + ImGui.GetWindowHeight());

            var dl = ImGui.GetWindowDrawList();
            dl.PushClipRect(clipMin, clipMax);
            dl.AddText(
                this.FontFor(ImTextSeStringMode.Source),
                ImGui.GetFontSize(),
                clipMin,
                ImGui.GetColorU32(ImGuiCol.Text),
                "<icon(1)>Test test<icon(1)>"u8);
            dl.PopClipRect();
        }

        if (ImGui.CollapsingHeader("Addon Table"u8))
        {
            using var table = ImRaii.Table("Addon Sheet"u8, 3);
            if (table.Success)
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Row ID"u8, ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("0000000"u8).X);
                ImGui.TableSetupColumn("Text"u8, ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(
                    "Misc"u8,
                    ImGuiTableColumnFlags.WidthFixed,
                    ImGui.CalcTextSize("AAAAAAAAAAAAAAAAA"u8).X);
                ImGui.TableHeadersRow();

                var addon = Service<DataManager>.GetNullable()?.GetExcelSheet<Addon>() ??
                            throw new InvalidOperationException("Addon sheet not loaded.");

                var clipper = ImGui.ImGuiListClipper();
                clipper.Begin(addon.Count);
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var row = addon.GetRowAt(i);

                        ImGui.TableNextRow();
                        using var pushedId = ImRaii.PushId(i);

                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text($"{row.RowId}");

                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        this.DrawSeString(row.Text.Data.Span, ImTextSeStringMode.Compiled);

                        ImGui.TableNextColumn();
                        if (ImGui.Button("Print to Chat"u8))
                            Service<ChatGui>.Get().Print(row.Text);
                    }
                }

                clipper.Destroy();
            }
        }

        if (ImGui.Button("Reset Text"u8) || this.testStringBuffer.IsDisposed)
        {
            this.testStringBuffer.Dispose();
            this.testStringBuffer = ImVectorWrapper.CreateFromSpan(
                "<icon(1)><icon(2)><icon(3)><icon(4)><icon(5)><icon(6)><icon(7)><icon(8)><icon(9)><icon(10)><icon(11)><icon(12)><icon(13)><icon(14)><icon(15)><icon(16)><icon(17)><icon(18)><icon(19)><icon(20)><icon(21)><icon(22)><icon(23)><icon(24)><icon(25)>\n\n<icon(56)>Lorem ipsum dolor <colortype(504)><edgecolortype(505)>sit<colortype(0)><edgecolortype(0)> <italic(1)>amet,<italic(0)> <colortype(500)><edgecolortype(501)>conse<->ctetur<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)><italic(1)>adipi<-><colortype(504)><edgecolortype(505)>scing<colortype(0)><edgecolortype(0)><italic(0)><colortype(0)><edgecolortype(0)> elit. <colortype(502)><edgecolortype(503)>Maece<->nas<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>digni<-><colortype(504)><edgecolortype(505)>ssim<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> <colortype(504)><edgecolortype(505)>sem<colortype(0)><edgecolortype(0)> <italic(1)>at<italic(0)> inter<->dum <colortype(500)><edgecolortype(501)>ferme<->ntum.<colortype(0)><edgecolortype(0)> Praes<->ent <colortype(500)><edgecolortype(501)>ferme<->ntum<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>conva<->llis<colortype(0)><edgecolortype(0)> velit <colortype(504)><edgecolortype(505)>sit<colortype(0)><edgecolortype(0)> <italic(1)>amet<italic(0)> <colortype(500)><edgecolortype(501)>hendr<->erit.<colortype(0)><edgecolortype(0)> <colortype(504)><edgecolortype(505)>Sed<colortype(0)><edgecolortype(0)> eu nibh <colortype(502)><edgecolortype(503)>magna.<colortype(0)><edgecolortype(0)> Integ<->er nec lacus in velit porta euism<->od <colortype(504)><edgecolortype(505)>sed<colortype(0)><edgecolortype(0)> et lacus. <colortype(504)><edgecolortype(505)>Sed<colortype(0)><edgecolortype(0)> non <colortype(502)><edgecolortype(503)>mauri<->s<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>venen<-><italic(1)>atis,<colortype(0)><edgecolortype(0)><italic(0)> <colortype(502)><edgecolortype(503)>matti<->s<colortype(0)><edgecolortype(0)> <colortype(502)><edgecolortype(503)>metus<colortype(0)><edgecolortype(0)> in, <italic(1)>aliqu<->et<italic(0)> dolor. <italic(1)>Aliqu<->am<italic(0)> erat <colortype(500)><edgecolortype(501)>volut<->pat.<colortype(0)><edgecolortype(0)> Nulla <colortype(500)><edgecolortype(501)>venen<-><italic(1)>atis<colortype(0)><edgecolortype(0)><italic(0)> velit <italic(1)>ac<italic(0)> <colortype(504)><edgecolortype(505)><colortype(516)><edgecolortype(517)>sus<colortype(0)><edgecolortype(0)>ci<->pit<colortype(0)><edgecolortype(0)> euism<->od. <colortype(500)><edgecolortype(501)><colortype(504)><edgecolortype(505)><colortype(516)><edgecolortype(517)>sus<colortype(0)><edgecolortype(0)>pe<->ndisse<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> <colortype(502)><edgecolortype(503)>maxim<->us<colortype(0)><edgecolortype(0)> viver<->ra dui id dapib<->us. Nam torto<->r dolor, <colortype(500)><edgecolortype(501)>eleme<->ntum<colortype(0)><edgecolortype(0)> quis orci id, pulvi<->nar <colortype(500)><edgecolortype(501)>fring<->illa<colortype(0)><edgecolortype(0)> quam. <colortype(500)><edgecolortype(501)>Pelle<->ntesque<colortype(0)><edgecolortype(0)> laore<->et viver<->ra torto<->r eget <colortype(502)><edgecolortype(503)>matti<-><colortype(504)><edgecolortype(505)>s.<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>Vesti<-><bold(1)>bulum<colortype(0)><edgecolortype(0)><bold(0)> eget porta <italic(1)>ante,<italic(0)> a <colortype(502)><edgecolortype(503)>molli<->s<colortype(0)><edgecolortype(0)> nulla. <colortype(500)><edgecolortype(501)>Curab<->itur<colortype(0)><edgecolortype(0)> a ligul<->a leo. <italic(1)>Aliqu<->am<italic(0)> volut<->pat <colortype(504)><edgecolortype(505)>sagit<->tis<colortype(0)><edgecolortype(0)> dapib<->us.\n\n<icon(57)>Fusce iacul<->is <italic(1)>aliqu<->am<italic(0)> <colortype(502)><edgecolortype(503)>mi,<colortype(0)><edgecolortype(0)> eget <colortype(500)><edgecolortype(501)>portt<->itor<colortype(0)><edgecolortype(0)> <italic(1)>arcu<italic(0)> <colortype(500)><edgecolortype(501)><colortype(504)><edgecolortype(505)>solli<->citudin<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>conse<->ctetur.<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)><colortype(504)><edgecolortype(505)><colortype(516)><edgecolortype(517)>sus<colortype(0)><edgecolortype(0)>pe<->ndisse<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> <italic(1)>aliqu<->am<italic(0)> commo<->do <colortype(500)><edgecolortype(501)>tinci<->dunt.<colortype(0)><edgecolortype(0)> Duis <colortype(504)><edgecolortype(505)>sed<colortype(0)><edgecolortype(0)> posue<->re tellu<-><colortype(504)><edgecolortype(505)>s.<colortype(0)><edgecolortype(0)> <colortype(504)><edgecolortype(505)>Sed<colortype(0)><edgecolortype(0)> phare<->tra ex vel torto<->r <colortype(500)><edgecolortype(501)>pelle<->ntesque,<colortype(0)><edgecolortype(0)> inter<->dum porta <colortype(504)><edgecolortype(505)>sapie<->n<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>digni<-><colortype(504)><edgecolortype(505)>ssim.<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> Queue Dun <colortype(504)><edgecolortype(505)>Scait<->h.<colortype(0)><edgecolortype(0)> Cras <italic(1)>aliqu<->et<italic(0)> <italic(1)>at<italic(0)> nulla quis <colortype(500)><edgecolortype(501)><colortype(502)><edgecolortype(503)>moles<->tie.<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>Vesti<-><bold(1)>bulum<colortype(0)><edgecolortype(0)><bold(0)> eu ligul<->a <colortype(504)><edgecolortype(505)>sapie<->n.<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>Curab<->itur<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>digni<-><colortype(504)><edgecolortype(505)>ssim<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> feugi<-><italic(1)>at<italic(0)> <colortype(500)><edgecolortype(501)>volut<->pat.<colortype(0)><edgecolortype(0)>\n\n<icon(58)><colortype(500)><edgecolortype(501)>Vesti<-><bold(1)>bulum<colortype(0)><edgecolortype(0)><bold(0)> <colortype(500)><edgecolortype(501)>condi<-><colortype(502)><edgecolortype(503)>mentum<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> laore<->et rhonc<->us. Vivam<->us et <italic(1)>accum<-><colortype(504)><edgecolortype(505)>san<italic(0)><colortype(0)><edgecolortype(0)> purus. <colortype(500)><edgecolortype(501)>Curab<->itur<colortype(0)><edgecolortype(0)> inter<->dum vel ligul<->a <italic(1)>ac<italic(0)> euism<->od. Donec <colortype(504)><edgecolortype(505)>sed<colortype(0)><edgecolortype(0)> nisl <colortype(500)><edgecolortype(501)>digni<-><colortype(504)><edgecolortype(505)>ssim<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> est <colortype(500)><edgecolortype(501)>tinci<->dunt<colortype(0)><edgecolortype(0)> iacul<->is. Praes<->ent <colortype(500)><edgecolortype(501)>hendr<->erit<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>pelle<->ntesque<colortype(0)><edgecolortype(0)> nisl, quis lacin<->ia <italic(1)>arcu<italic(0)> dictu<->m <colortype(504)><edgecolortype(505)>sit<colortype(0)><edgecolortype(0)> <italic(1)>amet.<italic(0)> <italic(1)>Aliqu<->am<italic(0)> variu<->s lectu<->s vel <colortype(502)><edgecolortype(503)>mauri<->s<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>imper<->diet<colortype(0)><edgecolortype(0)> posue<->re. Ut gravi<->da non <colortype(504)><edgecolortype(505)>sapie<->n<colortype(0)><edgecolortype(0)> <colortype(504)><edgecolortype(505)>sed<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>hendr<->erit.<colortype(0)><edgecolortype(0)>\n\n<icon(59)>Proin quis dapib<->us odio. Cras <colortype(504)><edgecolortype(505)>sagit<->tis<colortype(0)><edgecolortype(0)> non <colortype(504)><edgecolortype(505)>sem<colortype(0)><edgecolortype(0)> <colortype(504)><edgecolortype(505)>sed<colortype(0)><edgecolortype(0)> porta. Donec iacul<->is est ligul<-><italic(1)>a,<italic(0)> <colortype(500)><edgecolortype(501)>digni<-><colortype(504)><edgecolortype(505)>ssim<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> <italic(1)>aliqu<->et<italic(0)> <italic(1)>augue<italic(0)> <colortype(502)><edgecolortype(503)>matti<->s<colortype(0)><edgecolortype(0)> vitae. Duis <colortype(500)><edgecolortype(501)>ullam<->corper<colortype(0)><edgecolortype(0)> tempu<->s odio, non <colortype(500)><edgecolortype(501)>vesti<-><bold(1)>bulum<colortype(0)><edgecolortype(0)><bold(0)> est <bold(1)>biben<->dum<bold(0)> quis. In purus elit, vehic<->ula <colortype(500)><edgecolortype(501)>tinci<->dunt<colortype(0)><edgecolortype(0)> dictu<->m in, <italic(1)>aucto<->r<italic(0)> nec enim. <colortype(500)><edgecolortype(501)>Curab<->itur<colortype(0)><edgecolortype(0)> a nisi in leo <colortype(502)><edgecolortype(503)>matti<->s<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>pelle<->ntesque<colortype(0)><edgecolortype(0)> id nec <colortype(504)><edgecolortype(505)>sem.<colortype(0)><edgecolortype(0)> Nunc vel ultri<->ces nisl. Nam congu<->e <colortype(500)><edgecolortype(501)>vulpu<->tate<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)><colortype(502)><edgecolortype(503)>males<->uada.<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> <italic(1)>Aenea<->n<italic(0)> <colortype(500)><edgecolortype(501)>vesti<-><bold(1)>bulum<colortype(0)><edgecolortype(0)><bold(0)> <colortype(502)><edgecolortype(503)>mauri<->s<colortype(0)><edgecolortype(0)> leo, <colortype(504)><edgecolortype(505)>sit<colortype(0)><edgecolortype(0)> <italic(1)>amet<italic(0)> iacul<->is est <colortype(500)><edgecolortype(501)>imper<->diet<colortype(0)><edgecolortype(0)> ut. <colortype(500)><edgecolortype(501)>Phase<->llus<colortype(0)><edgecolortype(0)> nec lobor<->tis lacus, <colortype(504)><edgecolortype(505)>sit<colortype(0)><edgecolortype(0)> <italic(1)>amet<italic(0)> <colortype(500)><edgecolortype(501)><colortype(504)><edgecolortype(505)>scele<->risque<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> purus. Nam id lacin<->ia velit, euism<->od feugi<-><italic(1)>at<italic(0)> dui. Nulla <colortype(504)><edgecolortype(505)>sodal<->es<colortype(0)><edgecolortype(0)> odio ligul<-><italic(1)>a,<italic(0)> et <colortype(500)><edgecolortype(501)>hendr<->erit<colortype(0)><edgecolortype(0)> torto<->r <colortype(502)><edgecolortype(503)>maxim<->us<colortype(0)><edgecolortype(0)> eu. Donec et <colortype(504)><edgecolortype(505)>sem<colortype(0)><edgecolortype(0)> eu <colortype(502)><edgecolortype(503)>magna<colortype(0)><edgecolortype(0)> volut<->pat <italic(1)>accum<-><colortype(504)><edgecolortype(505)>san<italic(0)><colortype(0)><edgecolortype(0)> non ut lectu<-><colortype(504)><edgecolortype(505)>s.<colortype(0)><edgecolortype(0)>\n\n<icon(60)>Vivam<->us <colortype(504)><edgecolortype(505)><colortype(516)><edgecolortype(517)>sus<colortype(0)><edgecolortype(0)>ci<->pit<colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>ferme<->ntum<colortype(0)><edgecolortype(0)> gravi<->da. Cras nec <colortype(500)><edgecolortype(501)>conse<->ctetur<colortype(0)><edgecolortype(0)> <colortype(502)><edgecolortype(503)>magna.<colortype(0)><edgecolortype(0)> Vivam<->us <italic(1)>ante<italic(0)> <colortype(502)><edgecolortype(503)>massa,<colortype(0)><edgecolortype(0)> <italic(1)>accum<-><colortype(504)><edgecolortype(505)>san<italic(0)><colortype(0)><edgecolortype(0)> <colortype(504)><edgecolortype(505)>sit<colortype(0)><edgecolortype(0)> <italic(1)>amet<italic(0)> felis et, tempu<->s iacul<->is ipsum. <colortype(500)><edgecolortype(501)>Pelle<->ntesque<colortype(0)><edgecolortype(0)> vitae nisi <colortype(500)><edgecolortype(501)><italic(1)>accum<-><colortype(504)><edgecolortype(505)>san,<colortype(0)><edgecolortype(0)><italic(0)><colortype(0)><edgecolortype(0)> <colortype(500)><edgecolortype(501)>venen<-><italic(1)>atis<colortype(0)><edgecolortype(0)><italic(0)> lectu<->s <italic(1)>aucto<->r,<italic(0)> <italic(1)>aliqu<->et<italic(0)> liber<->o. Nam nec <colortype(500)><edgecolortype(501)>imper<->diet<colortype(0)><edgecolortype(0)> justo. Vivam<->us ut vehic<->ula turpi<-><colortype(504)><edgecolortype(505)>s.<colortype(0)><edgecolortype(0)> Nunc lobor<->tis <colortype(500)><edgecolortype(501)>pelle<->ntesque<colortype(0)><edgecolortype(0)> urna, <colortype(504)><edgecolortype(505)>sit<colortype(0)><edgecolortype(0)> <italic(1)>amet<italic(0)> <colortype(500)><edgecolortype(501)><colortype(504)><edgecolortype(505)>solli<->citudin<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> nibh fauci<-><bold(1)>bus<bold(0)> in. <colortype(500)><edgecolortype(501)>Curab<->itur<colortype(0)><edgecolortype(0)> eu lobor<->tis lacus. Donec eu <colortype(500)><edgecolortype(501)>hendr<->erit<colortype(0)><edgecolortype(0)> diam, vitae cursu<->s odio. Cras eget <colortype(500)><edgecolortype(501)><colortype(504)><edgecolortype(505)>scele<->risque<colortype(0)><edgecolortype(0)><colortype(0)><edgecolortype(0)> <colortype(502)><edgecolortype(503)>mi.<colortype(0)><edgecolortype(0)>\n\n· Testing aaaaa<link(0x0E,1,2,3,testlink)>link <icon(61)> aaaaa<link(0xCE)>bbbb.\n· Open <link(0x0E,0,0,0,\\[\"test\"\\, \"https://example.com/\"\\])><colortype(502)><edgecolortype(503)>example.com<colortype(0)><edgecolortype(0)><link(0xCE)>\n· Open <link(0x0E,2,2,2,\\[\"test\"\\, \"https://example.org/\"\\])><colortype(502)><edgecolortype(503)>example.org<colortype(0)><edgecolortype(0)><link(0xCE)>\n\n<icon2(1)><icon2(2)><icon2(3)><icon2(4)><icon2(5)><icon2(6)><icon2(7)><icon2(8)><icon2(9)><icon2(10)><icon2(11)><icon2(12)><icon2(13)><icon2(14)><icon2(15)><icon2(16)><icon2(17)><icon2(18)><icon2(19)><icon2(20)><icon2(21)><icon2(22)><icon2(23)><icon2(24)><icon2(25)>\n\n<edge(1)><colortype(502)><edgecolortype(503)><icon(1)>colortype502,edgecolortype503<edgecolortype(0)><colortype(0)>\n\nOpacity values are ignored:\n<color(0xFFFF0000)><edgecolor(0xFF0000FF)><icon(2)>opacity FF<edgecolor(stackcolor)><color(stackcolor)>\n<color(0x80FF0000)><edgecolor(0x800000FF)><icon(3)>opacity 80<edgecolor(stackcolor)><color(stackcolor)>\n<color(0xFF0000)><edgecolor(0xFF)><icon(4)>opacity 00<edgecolor(stackcolor)><color(stackcolor)>\n<color(0xFF0000)><edgecolor(0xFF)><colortype(502)><edgecolortype(503)><icon(6)>Test 1<edgecolortype(0)><colortype(0)><edgecolor(stackcolor)><color(stackcolor)>\n<colortype(502)><edgecolortype(503)><color(0xFF0000)><edgecolor(0xFF)><icon(6)>Test 2<edgecolortype(0)><colortype(0)><edgecolor(stackcolor)><color(stackcolor)>\n<edge(0)>Without edge<shadow(1)>Shadow<shadow(0)><edge(1)>With edge"u8,
                minCapacity: 65536);
            this.testString = string.Empty;
        }

        ImGui.SameLine();

        if (ImGui.Button("Print to Chat Log"u8))
        {
            Service<ChatGui>.Get().Print(Service<SeStringLayouter>.Get().CompileAndCache(this.testString));
        }

        ImGui.SameLine();

        if (ImGui.Button("Copy as Image"))
        {
            _ = Service<DevTextureSaveMenu>.Get().ShowTextureSaveMenuAsync(
                this.DisplayName,
                $"From {nameof(SeStringRendererTestWidget)}",
                Task.FromResult(
                    Service<TextureManager>.Get().CreateTextureFromSeString(
                        ReadOnlySeString.FromMacroString(
                            this.testString,
                            new(ExceptionMode: MacroStringParseExceptionMode.EmbedError)),
                        this.FontFor(ImTextSeStringMode.None),
                        wrapWidth: ImGui.GetContentRegionAvail().X)));
        }

        ImGuiHelpers.ScaledDummy(3);
        this.DrawSeString(
            "Optional features implemented for the following test input:<br>" +
            "· <colortype(710)>line breaks<colortype(0)> are automatically replaced to <colortype(710)>\\<br><colortype(0)>.<br>" +
            "· <colortype(710)>D<link(0xCE)>alamud<colortype(0)> will display Dalamud.<br>" +
            "· <colortype(710)>W<link(0xCE)>hite<colortype(0)> will display White.<br>" +
            "· <colortype(710)>D<link(0xCE)>efaultIcon<colortype(0)> will display DefaultIcon.<br>" +
            "· <colortype(710)>D<link(0xCE)>isabledIcon<colortype(0)> will display DisabledIcon.<br>" +
            "· <colortype(710)>O<link(0xCE)>utdatedInstallableIcon<colortype(0)> will display OutdatedInstallableIcon.<br>" +
            "· <colortype(710)>T<link(0xCE)>roubleIcon<colortype(0)> will display TroubleIcon.<br>" +
            "· <colortype(710)>D<link(0xCE)>evPluginIcon<colortype(0)> will display DevPluginIcon.<br>" +
            "· <colortype(710)>U<link(0xCE)>pdateIcon<colortype(0)> will display UpdateIcon.<br>" +
            "· <colortype(710)>I<link(0xCE)>nstalledIcon<colortype(0)> will display InstalledIcon.<br>" +
            "· <colortype(710)>T<link(0xCE)>hirdIcon<colortype(0)> will display ThirdIcon.<br>" +
            "· <colortype(710)>T<link(0xCE)>hirdInst<link(0xCE)>alledIcon<colortype(0)> will display ThirdInstalledIcon.<br>" +
            "· <colortype(710)>C<link(0xCE)>hangelogApiBumpIcon<colortype(0)> will display ChangelogApiBumpIcon.<br>" +
            "· <colortype(710)>icon<link(0xCE)>(5)<colortype(0)> will display icon(5). This is different from \\<icon<link(0xCE)>(5)>.<br>" +
            "· <colortype(710)>tex<link(0xCE)>(ui/loadingimage/-nowloading_base25_hr1.tex)<colortype(0)> will display tex(ui/loadingimage/-nowloading_base25_hr1.tex).",
            ImTextSeStringMode.Source);
        ImGuiHelpers.ScaledDummy(3);

        fixed (byte* labelPtr = "Test Input"u8)
        {
            if (ImGui.InputTextMultiline(
                    labelPtr,
                    this.testStringBuffer.StorageSpan,
                    new(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeight() * 3)))
            {
                var len = this.testStringBuffer.StorageSpan.IndexOf((byte)0);
                if (len + 4 >= this.testStringBuffer.Capacity)
                    this.testStringBuffer.EnsureCapacityExponential(len + 4);

                if (len < this.testStringBuffer.Capacity)
                {
                    this.testStringBuffer.LengthUnsafe = len;
                    this.testStringBuffer.StorageSpan[len] = 0;
                }

                this.testString = string.Empty;
            }
        }

        if (this.testString == string.Empty && this.testStringBuffer.Length != 0)
            this.testString = Encoding.UTF8.GetString(this.testStringBuffer.DataSpan);

        if (this.alignToFramePadding)
            ImGui.AlignTextToFramePadding();

        this.DrawSeString(this.testString, ImTextSeStringMode.Source);
        if (this.interactable)
        {
            ImGui.Separator();
            if (ImGui.TryGetSeStringInteraction(out var interaction, SeStringInteractionFlags.All))
            {
                var macro = Encoding.UTF8.GetBytes(this.testString)
                                    .AsSpan(interaction.PayloadOffset, interaction.PayloadLength);
                var payload = Game.Text.SeStringHandling.SeString
                                  .Parse(ReadOnlySeString.FromMacroString(macro).Data.Span).Payloads
                                  .FirstOrDefault();
                if (this.alignToFramePadding)
                    ImGui.AlignTextToFramePadding();
                ImGui.Text(
                    $"{interaction.Flags}[{interaction.PayloadOffset}]: {Encoding.UTF8.GetString(macro)}; {payload}");
                if ((interaction.Flags & SeStringInteractionFlags.LeftClick) != 0 &&
                    payload is DalamudLinkPayload { Plugin: "test" } dlp)
                {
                    Util.OpenLink(dlp.ExtraString);
                }
            }
            else
            {
                if (this.alignToFramePadding)
                    ImGui.AlignTextToFramePadding();
                this.DrawSeString("If a link is hovered, it will be displayed here.", ImTextSeStringMode.Source);
            }
        }

        ImGui.Separator();
        if (this.alignToFramePadding)
            ImGui.AlignTextToFramePadding();
        this.DrawSeString("Extra line for alignment testing.", ImTextSeStringMode.Source);
    }

    private ImFontPtr FontFor(ImTextSeStringMode mode) =>
        ImGui.Font.WithStyle(this.textStyle with { SeStringMode = mode });

    private void DrawSeString(ImU8String text, ImTextSeStringMode mode)
    {
        using var pushCallback = SeStringCallbackStack.Push(this);
        using var pushWrap = this.wrap ? ImRaii.TextWrapPos(0) : null;
        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * this.opacity);
        using var font = ImRaii.PushFont(this.FontFor(mode));
        ImGui.Text(text);
    }

    private SeStringEntity GetEntity(scoped in SeStringEntityContext context)
    {
        var span = context.Text[context.ByteOffset..];
        var fontSize = context.FontSize;
        if (!this.useEntity)
            return default;
        if (span.StartsWith("Dalamud"u8))
            return new(7, new(fontSize, fontSize), DrawDalamud);
        if (span.StartsWith("White"u8))
            return new(5, new(fontSize, fontSize), DrawWhite);
        if (span.StartsWith("DefaultIcon"u8))
            return new(11, new(fontSize, fontSize), DrawDefaultIcon);
        if (span.StartsWith("DisabledIcon"u8))
            return new(12, new(fontSize, fontSize), DrawDisabledIcon);
        if (span.StartsWith("OutdatedInstallableIcon"u8))
            return new(23, new(fontSize, fontSize), DrawOutdatedInstallableIcon);
        if (span.StartsWith("TroubleIcon"u8))
            return new(11, new(fontSize, fontSize), DrawTroubleIcon);
        if (span.StartsWith("DevPluginIcon"u8))
            return new(13, new(fontSize, fontSize), DrawDevPluginIcon);
        if (span.StartsWith("UpdateIcon"u8))
            return new(10, new(fontSize, fontSize), DrawUpdateIcon);
        if (span.StartsWith("ThirdIcon"u8))
            return new(9, new(fontSize, fontSize), DrawThirdIcon);
        if (span.StartsWith("ThirdInstalledIcon"u8))
            return new(18, new(fontSize, fontSize), DrawThirdInstalledIcon);
        if (span.StartsWith("ChangelogApiBumpIcon"u8))
            return new(20, new(fontSize, fontSize), DrawChangelogApiBumpIcon);
        if (span.StartsWith("InstalledIcon"u8))
            return new(13, new(fontSize, fontSize), DrawInstalledIcon);
        if (span.StartsWith("tex("u8))
        {
            var off = span.IndexOf((byte)')');
            var tex = Service<TextureManager>
                      .Get()
                      .Shared
                      .GetFromGame(Encoding.UTF8.GetString(span[4..off]))
                      .GetWrapOrEmpty();
            return new(off + 1, tex.Size * (fontSize / tex.Size.Y), DrawTexture);
        }

        if (span.StartsWith("icon("u8))
        {
            var off = span.IndexOf((byte)')');
            if (int.TryParse(span[5..off], out var parsed))
            {
                var tex = Service<TextureManager>
                          .Get()
                          .Shared
                          .GetFromGameIcon(parsed)
                          .GetWrapOrEmpty();
                return new(off + 1, tex.Size * (fontSize / tex.Size.Y), DrawIcon);
            }
        }

        return default;

        static void DrawTexture(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context)
        {
            var span = context.Text[context.ByteOffset..];
            var off = span.IndexOf((byte)')');
            var tex = Service<TextureManager>
                      .Get()
                      .Shared
                      .GetFromGame(Encoding.UTF8.GetString(span[4..off]))
                      .GetWrapOrEmpty();
            drawList.AddImage(tex.Handle, topLeft, topLeft + (tex.Size * (context.FontSize / tex.Size.Y)));
        }

        static void DrawIcon(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context)
        {
            var span = context.Text[context.ByteOffset..];
            var off = span.IndexOf((byte)')');
            if (!int.TryParse(span[5..off], out var parsed))
                return;
            var tex = Service<TextureManager>
                      .Get()
                      .Shared
                      .GetFromGameIcon(parsed)
                      .GetWrapOrEmpty();
            drawList.AddImage(tex.Handle, topLeft, topLeft + (tex.Size * (context.FontSize / tex.Size.Y)));
        }

        static void DrawAsset(
            ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context, DalamudAsset asset) =>
            drawList.AddImage(
                Service<DalamudAssetManager>.Get().GetDalamudTextureWrap(asset).Handle,
                topLeft,
                topLeft + new Vector2(context.FontSize));

        static void DrawDalamud(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context) =>
            DrawAsset(drawList, topLeft, context, DalamudAsset.LogoSmall);

        static void DrawWhite(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context) =>
            DrawAsset(drawList, topLeft, context, DalamudAsset.White4X4);

        static void DrawDefaultIcon(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context) =>
            DrawAsset(drawList, topLeft, context, DalamudAsset.DefaultIcon);

        static void DrawDisabledIcon(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context) =>
            DrawAsset(drawList, topLeft, context, DalamudAsset.DisabledIcon);

        static void DrawOutdatedInstallableIcon(
            ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context) =>
            DrawAsset(drawList, topLeft, context, DalamudAsset.OutdatedInstallableIcon);

        static void DrawTroubleIcon(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context) =>
            DrawAsset(drawList, topLeft, context, DalamudAsset.TroubleIcon);

        static void DrawDevPluginIcon(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context) =>
            DrawAsset(drawList, topLeft, context, DalamudAsset.DevPluginIcon);

        static void DrawUpdateIcon(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context) =>
            DrawAsset(drawList, topLeft, context, DalamudAsset.UpdateIcon);

        static void DrawInstalledIcon(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context) =>
            DrawAsset(drawList, topLeft, context, DalamudAsset.InstalledIcon);

        static void DrawThirdIcon(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context) =>
            DrawAsset(drawList, topLeft, context, DalamudAsset.ThirdIcon);

        static void DrawThirdInstalledIcon(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context) =>
            DrawAsset(drawList, topLeft, context, DalamudAsset.ThirdInstalledIcon);

        static void DrawChangelogApiBumpIcon(ImDrawListPtr drawList, Vector2 topLeft, in SeStringEntityContext context)
            => DrawAsset(drawList, topLeft, context, DalamudAsset.ChangelogApiBumpIcon);
    }
}
