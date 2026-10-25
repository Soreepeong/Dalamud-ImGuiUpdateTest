using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Fonts;
using Dalamud.Interface.Fonts.Internal;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Utility;
using Dalamud.Utility;

using Serilog;

namespace Dalamud.Interface.ImGuiFontChooserDialog;

/// <summary>
/// A dialog for choosing a font and its size.
/// </summary>
public sealed partial class SingleFontChooserDialog : IDisposable
{
    private const float MinFontSizePt = 1;

    private const float MaxFontSizePt = 127;

    private static readonly List<IFontId> EmptyIFontList = [];

    private static readonly (string Name, float Value)[] FontSizeList =
    [
        ("9.6", 9.6f),
        ("10", 10f),
        ("12", 12f),
        ("14", 14f),
        ("16", 16f),
        ("18", 18f),
        ("18.4", 18.4f),
        ("20", 20),
        ("23", 23),
        ("34", 34),
        ("36", 36),
        ("40", 40),
        ("45", 45),
        ("46", 46),
        ("68", 68),
        ("90", 90),
    ];

    private static int counterStatic;

    private readonly int counter;
    private readonly byte[] fontPreviewText = new byte[2048];
    private readonly FontPreviews previews = new();
    private readonly Dictionary<IFontFamilyId, IFontId?> previewFonts = [];
    private readonly TaskCompletionSource<FontSpec> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string popupImGuiName;
    private string title;

    private bool firstDraw = true;
    private bool firstDrawAfterRefresh;
    private int setFocusOn = -1;

    private bool useAdvancedOptions;

    private Task<List<IFontFamilyId>>? fontFamilies;
    private List<IFontFamilyId>? appliedFamilies;
    private int selectedFamilyIndex = -1;
    private int selectedFontIndex = -1;
    private FontStyle selectedFontStyle;

    private string familySearch = string.Empty;
    private string fontSearch = string.Empty;
    private string fontSizeSearch = "12";
    private ImFontRef? requestedFont;
    private ImFontRef? shownFont;
    private FontSpec selectedFont;

    private bool popupPositionChanged;
    private bool popupSizeChanged;
    private Vector2 popupPosition = new(float.NaN);
    private Vector2 popupSize = new(float.NaN);

    /// <summary>Initializes a new instance of the <see cref="SingleFontChooserDialog"/> class.</summary>
    /// <remarks>Call <see cref="Draw"/> every frame, and <see cref="Dispose"/> once done, or use
    /// <see cref="CreateAuto"/>, which does both.</remarks>
    public SingleFontChooserDialog()
    {
        this.counter = Interlocked.Increment(ref counterStatic);
        this.title = "Choose a font...";
        this.popupImGuiName = $"{this.title}##{nameof(SingleFontChooserDialog)}[{this.counter}]";
        this.selectedFont = new()
        {
            FontId = DalamudDefaultFontAndFamilyId.Instance,
            SizePx = InterfaceManager.DefaultFontSizePx,
        };
        Encoding.UTF8.GetBytes("Font preview.\n0123456789!\n遍角次亮采之门，门上插刀、直字拐弯、天上平板、船顶漏雨。\n다람쥐 헌 쳇바퀴에 타고파", this.fontPreviewText);
    }

    /// <summary>Called when the selected font spec has changed.</summary>
    /// <remarks>It is raised from <see cref="Draw"/>, at the first call and after each change.</remarks>
    public event Action<FontSpec>? SelectedFontSpecChanged;

    /// <summary>
    /// Gets or sets the title of this font chooser dialog popup.
    /// </summary>
    public string Title
    {
        get => this.title;
        set
        {
            this.title = value;
            this.popupImGuiName = $"{this.title}##{nameof(SingleFontChooserDialog)}[{this.counter}]";
        }
    }

    /// <summary>
    /// Gets or sets the preview text. A text too long may be truncated on assignment.
    /// </summary>
    public string PreviewText
    {
        get
        {
            var n = this.fontPreviewText.AsSpan().IndexOf((byte)0);
            return n < 0
                       ? Encoding.UTF8.GetString(this.fontPreviewText)
                       : Encoding.UTF8.GetString(this.fontPreviewText, 0, n);
        }

        set
        {
            // The last byte stays 0. The encoder writes whole characters only.
            Encoding.UTF8.GetEncoder().Convert(
                value,
                this.fontPreviewText.AsSpan(0, this.fontPreviewText.Length - 1),
                true,
                out _,
                out var length,
                out _);
            this.fontPreviewText.AsSpan(length).Clear();
        }
    }

    /// <summary>
    /// Gets the task that resolves upon choosing a font or cancellation.
    /// </summary>
    /// <remarks>The task is canceled on cancel. The spec has the fallbacks of <see cref="SelectedFont"/>, and none
    /// if that was never set.</remarks>
    public Task<FontSpec> ResultTask => this.tcs.Task;

    /// <summary>
    /// Gets or sets the selected family and font.
    /// </summary>
    /// <remarks><ol>
    /// <li>The dialog edits <see cref="FontSpec.FontId"/>. A spec without one gets the first font id of its
    /// fallbacks, or the default font, and keeps its fallbacks.</li>
    /// <li>A size that follows the current font size is taken at the default font size, as the dialog lists sizes in
    /// points.</li>
    /// </ol></remarks>
    public FontSpec SelectedFont
    {
        get => this.selectedFont;
        set
        {
            if (value.SizePx <= 0)
                value = value.WithSize(FontSizes.ToGlyphSize(value, Service<FontFactory>.Get().DefaultSizePx));

            var id = value.GetFirstFontId() ?? DalamudDefaultFontAndFamilyId.Instance;
            this.selectedFont = value.FontId is null ? value with { FontId = id } : value;

            var familyName = id.Family.ToString() ?? string.Empty;
            var fontName = id.ToString() ?? string.Empty;
            this.familySearch = this.ExtractName(id.Family.Name);
            this.fontSearch = this.ExtractName(id.Name);
            if (this.fontFamilies?.IsCompletedSuccessfully is true)
                this.UpdateSelectedFamilyAndFontIndices(this.fontFamilies.Result, familyName, fontName);
            this.fontSizeSearch = $"{value.SizePt:0.##}";
            this.useAdvancedOptions |= Math.Abs(value.LineHeight - 1f) > 0.000001;
            this.useAdvancedOptions |= value.GlyphOffset != default;
            this.useAdvancedOptions |= value.LetterSpacing != 0f;

            // The next Draw loads it, and raises SelectedFontSpecChanged.
            this.requestedFont = null;
        }
    }

    /// <summary>
    /// Gets or sets the font family exclusion filter predicate.
    /// </summary>
    public Predicate<IFontFamilyId>? FontFamilyExcludeFilter { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to ignore the global scale on preview text input.
    /// </summary>
    public bool IgnorePreviewGlobalScale { get; set; }

    /// <summary>Gets or sets a value indicating whether this popup should be modal, blocking everything behind from
    /// being interacted.</summary>
    /// <remarks>If <c>true</c>, then <see cref="ImGui.BeginPopupModal(ImU8String, ref bool, ImGuiWindowFlags)"/> will be
    /// used. Otherwise, <see cref="ImGui.Begin(ImU8String, ref bool, ImGuiWindowFlags)"/> will be used.</remarks>
    public bool IsModal { get; set; } = true;

    /// <summary>Gets or sets the window flags.</summary>
    public ImGuiWindowFlags WindowFlags { get; set; }

    /// <summary>Gets or sets the popup window position.</summary>
    /// <remarks>
    /// <para>Setting the position only works before the first call to <see cref="Draw"/>.</para>
    /// <para>If any of the coordinates are <see cref="float.NaN"/>, default position will be used.</para>
    /// <para>The position will be clamped into the work area of the selected monitor.</para>
    /// </remarks>
    public Vector2 PopupPosition
    {
        get => this.popupPosition;
        set
        {
            this.popupPositionChanged = true;
            this.popupPosition = value;
        }
    }

    /// <summary>Gets or sets the popup window size.</summary>
    /// <remarks>
    /// <para>Setting the size only works before the first call to <see cref="Draw"/>.</para>
    /// <para>If any of the coordinates are <see cref="float.NaN"/>, default size will be used.</para>
    /// <para>The size will be clamped into the work area of the selected monitor.</para>
    /// </remarks>
    public Vector2 PopupSize
    {
        get => this.popupSize;
        set
        {
            this.popupSizeChanged = true;
            this.popupSize = value;
        }
    }

    /// <summary>Creates a new instance of <see cref="SingleFontChooserDialog"/> that draws itself, and disposes itself
    /// once closed.</summary>
    /// <param name="uiBuilder">An instance of <see cref="IUiBuilder"/>.</param>
    /// <returns>The new instance of <see cref="SingleFontChooserDialog"/>.</returns>
    public static SingleFontChooserDialog CreateAuto(IUiBuilder uiBuilder)
    {
        var fcd = new SingleFontChooserDialog();
        uiBuilder.Draw += DrawAndDispose;
        return fcd;

        // Disposed in the frame it closed in, so that it is never drawn while disposed.
        void DrawAndDispose()
        {
            fcd.Draw();
            if (!fcd.tcs.Task.IsCompleted)
                return;
            uiBuilder.Draw -= DrawAndDispose;
            fcd.Dispose();
        }
    }

    /// <summary>Gets the default popup size before clamping to monitor work area.</summary>
    /// <returns>The default popup size.</returns>
    public static Vector2 GetDefaultPopupSizeNonClamped()
    {
        ThreadSafety.AssertMainThread();
        return new Vector2(40, 30) * ImGui.GetTextLineHeight();
    }

    /// <inheritdoc/>
    /// <remarks>Call it on the main thread, after the last <see cref="Draw"/>.</remarks>
    public void Dispose()
    {
        this.requestedFont = null;
        this.shownFont = null;
        this.previews.Clear();
    }

    /// <summary>
    /// Cancels this dialog.
    /// </summary>
    /// <remarks>It does nothing to a dialog that is already closed.</remarks>
    public void Cancel()
    {
        this.tcs.TrySetCanceled();
        ImGui.GetIO().WantCaptureKeyboard = false;
        ImGui.GetIO().WantTextInput = false;
    }

    /// <summary>Sets <see cref="PopupSize"/> and <see cref="PopupPosition"/> to be at the center of the current window
    /// being drawn.</summary>
    /// <param name="preferredPopupSize">The preferred popup size.</param>
    public void SetPopupPositionAndSizeToCurrentWindowCenter(Vector2 preferredPopupSize)
    {
        ThreadSafety.AssertMainThread();
        this.PopupSize = preferredPopupSize;
        this.PopupPosition = ImGui.GetWindowPos() + ((ImGui.GetWindowSize() - preferredPopupSize) / 2);
    }

    /// <summary>Sets <see cref="PopupSize"/> and <see cref="PopupPosition"/> to be at the center of the current window
    /// being drawn.</summary>
    public void SetPopupPositionAndSizeToCurrentWindowCenter() =>
        this.SetPopupPositionAndSizeToCurrentWindowCenter(GetDefaultPopupSizeNonClamped());

    /// <summary>
    /// Draws this dialog.
    /// </summary>
    /// <remarks>A closed dialog draws nothing.</remarks>
    public void Draw()
    {
        const float popupMinWidth = 320;
        const float popupMinHeight = 240;

        if (this.tcs.Task.IsCompleted)
            return;
        if (this.fontFamilies is { IsCompletedSuccessfully: true, Result: var families } &&
            families != this.appliedFamilies)
        {
            this.ApplyRefresh(families);
        }

        ImGui.GetIO().WantCaptureKeyboard = true;
        ImGui.GetIO().WantTextInput = true;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            this.Cancel();
            return;
        }

        if (this.firstDraw)
        {
            if (this.IsModal)
                ImGui.OpenPopup(this.popupImGuiName);
        }

        if (this.firstDraw || this.popupPositionChanged || this.popupSizeChanged)
        {
            var preferProvidedSize = !float.IsNaN(this.popupSize.X) && !float.IsNaN(this.popupSize.Y);
            var size = preferProvidedSize ? this.popupSize : GetDefaultPopupSizeNonClamped();
            size.X = Math.Max(size.X, popupMinWidth);
            size.Y = Math.Max(size.Y, popupMinHeight);

            var preferProvidedPos = !float.IsNaN(this.popupPosition.X) && !float.IsNaN(this.popupPosition.Y);
            var monitorLocatorPos = preferProvidedPos ? this.popupPosition + (size / 2) : ImGui.GetMousePos();

            var monitors = ImGui.GetPlatformIO().Monitors;
            var preferredMonitor = 0;
            var preferredDistance = GetDistanceFromMonitor(monitorLocatorPos, monitors[0]);
            for (var i = 1; i < monitors.Size; i++)
            {
                var distance = GetDistanceFromMonitor(monitorLocatorPos, monitors[i]);
                if (distance < preferredDistance)
                {
                    preferredMonitor = i;
                    preferredDistance = distance;
                }
            }

            var lt = monitors[preferredMonitor].WorkPos;
            var workSize = monitors[preferredMonitor].WorkSize;
            size.X = Math.Min(size.X, workSize.X);
            size.Y = Math.Min(size.Y, workSize.Y);
            var rb = (lt + workSize) - size;

            var pos =
                preferProvidedPos
                    ? new(Math.Clamp(this.PopupPosition.X, lt.X, rb.X), Math.Clamp(this.PopupPosition.Y, lt.Y, rb.Y))
                    : (lt + rb) / 2;

            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            this.popupPositionChanged = this.popupSizeChanged = false;
        }

        ImGui.SetNextWindowSizeConstraints(new(popupMinWidth, popupMinHeight), new(float.MaxValue));
        if (this.IsModal)
        {
            var open = true;
            if (!ImGui.BeginPopupModal(this.popupImGuiName, ref open, this.WindowFlags) || !open)
            {
                this.Cancel();
                return;
            }
        }
        else
        {
            var open = true;
            if (!ImGui.Begin(this.popupImGuiName, ref open, this.WindowFlags) || !open)
            {
                ImGui.End();
                this.Cancel();
                return;
            }
        }

        var framePad = ImGui.GetStyle().FramePadding;
        var windowPad = ImGui.GetStyle().WindowPadding;
        var baseOffset = ImGui.GetCursorPos() - windowPad;

        var actionSize = Vector2.Zero;
        actionSize = Vector2.Max(actionSize, ImGui.CalcTextSize("OK"u8));
        actionSize = Vector2.Max(actionSize, ImGui.CalcTextSize("Cancel"u8));
        actionSize = Vector2.Max(actionSize, ImGui.CalcTextSize("Refresh"u8));
        actionSize = Vector2.Max(actionSize, ImGui.CalcTextSize("Reset"u8));
        actionSize += framePad * 2;

        var bodySize = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPos(baseOffset + windowPad);
        if (ImGui.BeginChild(
                "##choicesBlock"u8,
                bodySize with { X = bodySize.X - windowPad.X - actionSize.X },
                false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            this.DrawChoices();
        }

        ImGui.EndChild();

        ImGui.SetCursorPos(baseOffset + windowPad + new Vector2(bodySize.X - actionSize.X, 0));

        if (ImGui.BeginChild("##actionsBlock"u8, bodySize with { X = actionSize.X }))
        {
            this.DrawActionButtons(actionSize);
        }

        ImGui.EndChild();

        this.popupPosition = ImGui.GetWindowPos();
        this.popupSize = ImGui.GetWindowSize();
        if (this.IsModal)
            ImGui.EndPopup();
        else
            ImGui.End();

        this.firstDraw = false;
        this.firstDrawAfterRefresh = false;
    }

    private static float GetDistanceFromMonitor(Vector2 point, ImGuiPlatformMonitor monitor)
    {
        var lt = monitor.MainPos;
        var rb = monitor.MainPos + monitor.MainSize;
        var xoff =
            point.X < lt.X
                ? lt.X - point.X
                : point.X > rb.X
                    ? point.X - rb.X
                    : 0;
        var yoff =
            point.Y < lt.Y
                ? lt.Y - point.Y
                : point.Y > rb.Y
                    ? point.Y - rb.Y
                    : 0;
        return MathF.Sqrt((xoff * xoff) + (yoff * yoff));
    }

    // The faces of a family, or none if they cannot be read, as for a family uninstalled since it was listed.
    private static IReadOnlyList<IFontId> FontsOf(IFontFamilyId family)
    {
        try
        {
            return family.Fonts;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to list the fonts of {Family}", family);
            return EmptyIFontList;
        }
    }

    // The face a family row previews in. Kept, as finding it lists and sorts the faces.
    private IFontId? PreviewFontOf(IFontFamilyId family)
    {
        ref var font = ref CollectionsMarshal.GetValueRefOrAddDefault(this.previewFonts, family, out var exists);
        if (!exists)
            font = FontsOf(family) is { Count: > 0 } fonts ? fonts[family.FindBestMatch(FontStyle.Default)] : null;
        return font;
    }

    private void DrawChoices()
    {
        var lineHeight = ImGui.GetTextLineHeight();
        var previewLineHeight = this.selectedFont.SizePx * this.selectedFont.LineHeight
                                * (this.IgnorePreviewGlobalScale ? 1f : ImGuiHelpers.GlobalScale);
        var previewHeight = (ImGui.GetFrameHeightWithSpacing() - lineHeight) +
                            Math.Max(lineHeight, MathF.Round(previewLineHeight) * 2);

        var advancedOptionsHeight = ImGui.GetFrameHeightWithSpacing() * (this.useAdvancedOptions ? 4 : 1);

        var tableSize = ImGui.GetContentRegionAvail() -
                        new Vector2(0, ImGui.GetStyle().WindowPadding.Y + previewHeight + advancedOptionsHeight);
        if (ImGui.BeginChild(
                "##tableContainer"u8,
                tableSize,
                false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
            && ImGui.BeginTable("##table"u8, 3))
        {
            ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, Vector4.Zero);
            ImGui.TableSetupColumn(
                "Font:##familyColumn"u8,
                ImGuiTableColumnFlags.WidthStretch,
                0.4f);
            ImGui.TableSetupColumn(
                "Style:##fontColumn"u8,
                ImGuiTableColumnFlags.WidthStretch,
                0.4f);
            ImGui.TableSetupColumn(
                "Size:##sizeColumn"u8,
                ImGuiTableColumnFlags.WidthStretch,
                0.2f);
            ImGui.TableHeadersRow();
            ImGui.PopStyleColor(3);

            ImGui.TableNextRow();

            var pad = (int)MathF.Round(8 * ImGuiHelpers.GlobalScale);
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(pad));
            ImGui.TableNextColumn();
            var changed = this.DrawFamilyListColumn();

            ImGui.TableNextColumn();
            changed |= this.DrawFontListColumn(changed);

            ImGui.TableNextColumn();
            changed |= this.DrawSizeListColumn();

            if (changed)
            {
                this.requestedFont = null;
            }

            ImGui.PopStyleVar();

            ImGui.EndTable();
        }

        ImGui.EndChild();

        ImGui.Checkbox("Show advanced options"u8, ref this.useAdvancedOptions);
        if (this.useAdvancedOptions)
        {
            if (this.DrawAdvancedOptions())
            {
                this.requestedFont = null;
            }
        }

        if (this.requestedFont is null)
        {
            this.requestedFont = this.selectedFont.GetFont();
            this.SelectedFontSpecChanged?.InvokeSafely(this.selectedFont);
        }

        // The font shown stays until the next one is loaded, so that sliders do not make the preview flicker.
        if (this.requestedFont.LoadTask.IsCompleted)
            this.shownFont = this.requestedFont;

        if (this.shownFont?.Entry.Exception is { } loadException)
        {
            ImGui.SetCursorPos(ImGui.GetCursorPos() + ImGui.GetStyle().FramePadding);
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.ErrorForeground);
            ImGui.Text(loadException.Message);
            ImGui.PopStyleColor();
        }
        else if (this.shownFont is null)
        {
            ImGui.SetCursorPos(ImGui.GetCursorPos() + ImGui.GetStyle().FramePadding);
            ImGui.Text("Loading font..."u8);
        }
        else
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

            var shownSizePx = this.shownFont.Spec.SizePx;
            var unscaled = this.IgnorePreviewGlobalScale && shownSizePx > 0;
            using (this.shownFont.Push(
                       unscaled ? shownSizePx / Math.Max(ImGuiHelpers.GlobalScale, float.Epsilon) : null))
            {
                ImGui.InputTextMultiline(
                    "##fontPreviewText"u8,
                    this.fontPreviewText,
                    ImGui.GetContentRegionAvail());
            }
        }
    }

    private bool DrawFamilyListColumn()
    {
        if (this.fontFamilies?.IsCompleted is not true)
        {
            ImGui.SetScrollY(0);
            ImGui.Text("Loading..."u8);
            return false;
        }

        if (!this.fontFamilies.IsCompletedSuccessfully)
        {
            ImGui.SetScrollY(0);
            ImGui.Text("Error: " + this.fontFamilies.Exception);
            return false;
        }

        // The indices are of the applied list. A list that completed during this frame is applied in the next.
        var families = this.appliedFamilies ?? this.fontFamilies.Result;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

        if (this.setFocusOn == 0)
        {
            this.setFocusOn = -1;
            ImGui.SetKeyboardFocusHere();
        }

        var changed = false;
        if (ImGui.InputText(
                "##familySearch"u8,
                ref this.familySearch,
                255,
                ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.CallbackHistory,
                (ref data) =>
                {
                    if (families.Count == 0)
                        return 0;

                    var baseIndex = this.selectedFamilyIndex;
                    if (data.SelectionStart == 0 && data.SelectionEnd == data.BufTextLen)
                    {
                        switch (data.EventKey)
                        {
                            case ImGuiKey.DownArrow:
                                this.selectedFamilyIndex = (this.selectedFamilyIndex + 1) % families.Count;
                                changed = true;
                                break;
                            case ImGuiKey.UpArrow:
                                this.selectedFamilyIndex =
                                    (this.selectedFamilyIndex + families.Count - 1) % families.Count;
                                changed = true;
                                break;
                        }

                        if (changed)
                        {
                            ImGuiHelpers.SetTextFromCallback(
                                ref data,
                                this.ExtractName(families[this.selectedFamilyIndex].Name));
                        }
                    }
                    else
                    {
                        switch (data.EventKey)
                        {
                            case ImGuiKey.DownArrow:
                                this.selectedFamilyIndex = families.FindIndex(
                                    baseIndex + 1,
                                    x => this.TestName(x.Name, this.familySearch));
                                if (this.selectedFamilyIndex < 0)
                                {
                                    this.selectedFamilyIndex = families.FindIndex(
                                        0,
                                        baseIndex + 1,
                                        x => this.TestName(x.Name, this.familySearch));
                                }

                                changed = true;
                                break;
                            case ImGuiKey.UpArrow:
                                if (baseIndex > 0)
                                {
                                    this.selectedFamilyIndex = families.FindLastIndex(
                                        baseIndex - 1,
                                        x => this.TestName(x.Name, this.familySearch));
                                }

                                if (this.selectedFamilyIndex < 0)
                                {
                                    if (baseIndex < 0)
                                        baseIndex = 0;
                                    this.selectedFamilyIndex = families.FindLastIndex(
                                        families.Count - 1,
                                        families.Count - baseIndex,
                                        x => this.TestName(x.Name, this.familySearch));
                                }

                                changed = true;
                                break;
                        }
                    }

                    return 0;
                }))
        {
            if (!string.IsNullOrWhiteSpace(this.familySearch) && !changed)
            {
                this.selectedFamilyIndex = families.FindIndex(x => this.TestName(x.Name, this.familySearch));
                changed = true;
            }
        }

        if (ImGui.BeginChild("##familyList"u8, ImGui.GetContentRegionAvail()))
        {
            var clipper = ImGui.ImGuiListClipper();
            var lineHeight = ImGui.GetTextLineHeightWithSpacing();

            if ((changed || this.firstDrawAfterRefresh) && this.selectedFamilyIndex != -1)
            {
                ImGui.SetScrollY(
                    (lineHeight * this.selectedFamilyIndex) -
                    ((ImGui.GetContentRegionAvail().Y - lineHeight) / 2));
            }

            clipper.Begin(families.Count, lineHeight);
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    if (i < 0)
                    {
                        ImGui.Text(" "u8);
                        continue;
                    }

                    var selected = this.selectedFamilyIndex == i;
                    var preview = this.PreviewFontOf(families[i]);
                    if (this.previews.Selectable(i, preview, this.ExtractName(families[i].Name), ref selected))
                    {
                        this.selectedFamilyIndex = i;
                        this.familySearch = this.ExtractName(families[i].Name);
                        this.setFocusOn = 0;
                        changed = true;
                    }
                }
            }

            clipper.Destroy();
        }

        if (changed && this.selectedFamilyIndex >= 0)
        {
            var family = families[this.selectedFamilyIndex];
            if (FontsOf(family) is { Count: > 0 } familyFonts)
            {
                this.selectedFontIndex = family.FindBestMatch(this.selectedFontStyle);
                this.selectedFont = this.selectedFont with { FontId = familyFonts[this.selectedFontIndex] };
            }
            else
            {
                this.selectedFontIndex = -1;
            }
        }

        ImGui.EndChild();
        return changed;
    }

    private bool DrawFontListColumn(bool changed)
    {
        if (this.fontFamilies?.IsCompleted is not true)
        {
            ImGui.Text("Loading..."u8);
            return changed;
        }

        if (!this.fontFamilies.IsCompletedSuccessfully)
        {
            ImGui.Text("Error: " + this.fontFamilies.Exception);
            return changed;
        }

        // The indices are of the applied list. A list that completed during this frame is applied in the next.
        var families = this.appliedFamilies ?? this.fontFamilies.Result;
        var family = this.selectedFamilyIndex >= 0
                     && this.selectedFamilyIndex < families.Count
                         ? families[this.selectedFamilyIndex]
                         : null;
        var fonts = family is null ? EmptyIFontList : FontsOf(family);

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

        if (this.setFocusOn == 1)
        {
            this.setFocusOn = -1;
            ImGui.SetKeyboardFocusHere();
        }

        if (ImGui.InputText(
                "##fontSearch"u8,
                ref this.fontSearch,
                255,
                ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.CallbackHistory,
                (ref data) =>
                {
                    if (fonts.Count == 0)
                        return 0;

                    var baseIndex = this.selectedFontIndex;
                    if (data.SelectionStart == 0 && data.SelectionEnd == data.BufTextLen)
                    {
                        switch (data.EventKey)
                        {
                            case ImGuiKey.DownArrow:
                                this.selectedFontIndex = (this.selectedFontIndex + 1) % fonts.Count;
                                changed = true;
                                break;
                            case ImGuiKey.UpArrow:
                                this.selectedFontIndex = (this.selectedFontIndex + fonts.Count - 1) % fonts.Count;
                                changed = true;
                                break;
                        }

                        if (changed)
                        {
                            ImGuiHelpers.SetTextFromCallback(
                                ref data,
                                this.ExtractName(fonts[this.selectedFontIndex].Name));
                        }
                    }
                    else
                    {
                        switch (data.EventKey)
                        {
                            case ImGuiKey.DownArrow:
                                this.selectedFontIndex = fonts.FindIndex(
                                    baseIndex + 1,
                                    x => this.TestName(x.Name, this.fontSearch));
                                if (this.selectedFontIndex < 0)
                                {
                                    this.selectedFontIndex = fonts.FindIndex(
                                        0,
                                        baseIndex + 1,
                                        x => this.TestName(x.Name, this.fontSearch));
                                }

                                changed = true;
                                break;
                            case ImGuiKey.UpArrow:
                                if (baseIndex > 0)
                                {
                                    this.selectedFontIndex = fonts.FindLastIndex(
                                        baseIndex - 1,
                                        x => this.TestName(x.Name, this.fontSearch));
                                }

                                if (this.selectedFontIndex < 0)
                                {
                                    if (baseIndex < 0)
                                        baseIndex = 0;
                                    this.selectedFontIndex = fonts.FindLastIndex(
                                        fonts.Count - 1,
                                        fonts.Count - baseIndex,
                                        x => this.TestName(x.Name, this.fontSearch));
                                }

                                changed = true;
                                break;
                        }
                    }

                    return 0;
                }))
        {
            if (!string.IsNullOrWhiteSpace(this.fontSearch) && !changed)
            {
                this.selectedFontIndex = fonts.FindIndex(x => this.TestName(x.Name, this.fontSearch));
                changed = true;
            }
        }

        if (ImGui.BeginChild("##fontList"u8))
        {
            var clipper = ImGui.ImGuiListClipper();
            var lineHeight = ImGui.GetTextLineHeightWithSpacing();

            if ((changed || this.firstDrawAfterRefresh) && this.selectedFontIndex != -1)
            {
                ImGui.SetScrollY(
                    (lineHeight * this.selectedFontIndex) -
                    ((ImGui.GetContentRegionAvail().Y - lineHeight) / 2));
            }

            clipper.Begin(fonts.Count, lineHeight);
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    if (i < 0)
                    {
                        ImGui.Text(" "u8);
                        continue;
                    }

                    var selected = this.selectedFontIndex == i;
                    if (this.previews.Selectable(i, fonts[i], this.ExtractName(fonts[i].Name), ref selected))
                    {
                        this.selectedFontIndex = i;
                        this.fontSearch = this.ExtractName(fonts[i].Name);
                        this.setFocusOn = 1;
                        changed = true;
                    }
                }
            }

            clipper.Destroy();
        }

        ImGui.EndChild();

        if (changed && family is not null && this.selectedFontIndex >= 0 && this.selectedFontIndex < fonts.Count)
        {
            var font = fonts[this.selectedFontIndex];
            this.selectedFontStyle = font.Style;

            // The first face of a collection. The locale of the spec picks the glyph forms, such as of the faces of
            // Noto Sans CJK for each language.
            this.selectedFont = this.selectedFont with { FontId = font, FontNo = 0 };
        }

        return changed;
    }

    private bool DrawSizeListColumn()
    {
        var changed = false;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

        if (this.setFocusOn == 2)
        {
            this.setFocusOn = -1;
            ImGui.SetKeyboardFocusHere();
        }

        if (ImGui.InputText(
                "##fontSizeSearch"u8,
                ref this.fontSizeSearch,
                255,
                ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.CallbackHistory |
                ImGuiInputTextFlags.CharsDecimal,
                (ref data) =>
                {
                    switch (data.EventKey)
                    {
                        case ImGuiKey.DownArrow:
                            this.selectedFont = this.selectedFont with
                            {
                                SizePt = Math.Min(MaxFontSizePt, MathF.Floor(this.selectedFont.SizePt) + 1),
                            };
                            changed = true;
                            break;
                        case ImGuiKey.UpArrow:
                            this.selectedFont = this.selectedFont with
                            {
                                SizePt = Math.Max(MinFontSizePt, MathF.Ceiling(this.selectedFont.SizePt) - 1),
                            };
                            changed = true;
                            break;
                    }

                    if (changed)
                        ImGuiHelpers.SetTextFromCallback(ref data, $"{this.selectedFont.SizePt:0.##}");

                    return 0;
                }))
        {
            if (float.TryParse(this.fontSizeSearch, out var fontSizePt1))
            {
                this.selectedFont = this.selectedFont with { SizePt = fontSizePt1 };
                changed = true;
            }
        }

        if (ImGui.BeginChild("##fontSizeList"u8))
        {
            var clipper = ImGui.ImGuiListClipper();
            var lineHeight = ImGui.GetTextLineHeightWithSpacing();

            if (changed && this.selectedFontIndex != -1)
            {
                ImGui.SetScrollY(
                    (lineHeight * this.selectedFontIndex) -
                    ((ImGui.GetContentRegionAvail().Y - lineHeight) / 2));
            }

            clipper.Begin(FontSizeList.Length, lineHeight);
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    if (i < 0)
                    {
                        ImGui.Text(" "u8);
                        continue;
                    }

                    var selected = Equals(FontSizeList[i].Value, this.selectedFont.SizePt);
                    if (ImGui.Selectable(
                            FontSizeList[i].Name,
                            ref selected,
                            ImGuiSelectableFlags.NoAutoClosePopups))
                    {
                        this.selectedFont = this.selectedFont with { SizePt = FontSizeList[i].Value };
                        this.setFocusOn = 2;
                        changed = true;
                    }
                }
            }

            clipper.Destroy();
        }

        ImGui.EndChild();

        if (this.selectedFont.SizePt < MinFontSizePt)
        {
            this.selectedFont = this.selectedFont with { SizePt = MinFontSizePt };
            changed = true;
        }

        if (this.selectedFont.SizePt > MaxFontSizePt)
        {
            this.selectedFont = this.selectedFont with { SizePt = MaxFontSizePt };
            changed = true;
        }

        if (changed)
            this.fontSizeSearch = $"{this.selectedFont.SizePt:0.##}";

        return changed;
    }

    private bool DrawAdvancedOptions()
    {
        var changed = false;

        if (!ImGui.BeginTable("##advancedOptions"u8, 4))
            return false;

        var labelWidth = ImGui.CalcTextSize("Letter Spacing:"u8).X;
        labelWidth = Math.Max(labelWidth, ImGui.CalcTextSize("Offset:"u8).X);
        labelWidth = Math.Max(labelWidth, ImGui.CalcTextSize("Line Height:"u8).X);
        labelWidth += ImGui.GetStyle().FramePadding.X;

        var inputWidth = ImGui.CalcTextSize("-000.00 px"u8).X * 2;
        ImGui.TableSetupColumn(
            "##inputLabelColumn"u8,
            ImGuiTableColumnFlags.WidthFixed,
            labelWidth);
        ImGui.TableSetupColumn(
            "##input1Column"u8,
            ImGuiTableColumnFlags.WidthFixed,
            inputWidth);
        ImGui.TableSetupColumn(
            "##input2Column"u8,
            ImGuiTableColumnFlags.WidthFixed,
            inputWidth);
        ImGui.TableSetupColumn(
            "##fillerColumn"u8,
            ImGuiTableColumnFlags.WidthStretch,
            1f);

        // The size is always in pixels, as the setter of SelectedFont and the size list make it.
        var em = this.selectedFont.SizePx;
        const string lengthFormat = "%.1f px";

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Offset:"u8);

        ImGui.TableNextColumn();
        var offset = this.selectedFont.GlyphOffset;
        if (Slider("##glyphOffsetX"u8, ref offset.X, -0.5f * em, 0.5f * em, lengthFormat))
        {
            changed = true;
            this.selectedFont = this.selectedFont with { GlyphOffset = offset };
        }

        ImGui.TableNextColumn();
        if (Slider("##glyphOffsetY"u8, ref offset.Y, -0.5f * em, 0.5f * em, lengthFormat))
        {
            changed = true;
            this.selectedFont = this.selectedFont with { GlyphOffset = offset };
        }

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Letter Spacing:"u8);

        ImGui.TableNextColumn();
        var letterSpacing = this.selectedFont.LetterSpacing;
        if (Slider("##letterSpacing"u8, ref letterSpacing, -0.25f * em, 0.5f * em, lengthFormat))
        {
            changed = true;
            this.selectedFont = this.selectedFont with { LetterSpacing = letterSpacing };
        }

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Line Height:"u8);

        ImGui.TableNextColumn();
        var lineHeight = this.selectedFont.LineHeight;
        if (Slider("##lineHeight"u8, ref lineHeight, 0.8f, 2f, "%.2fx"))
        {
            changed = true;
            this.selectedFont = this.selectedFont with { LineHeight = Math.Clamp(lineHeight, 0.5f, 3f) };
        }

        ImGui.EndTable();
        return changed;

        static bool Slider(ImU8String label, ref float value, float min, float max, ImU8String format)
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            return ImGui.SliderFloat(label, ref value, min, max, format);
        }
    }

    private void DrawActionButtons(Vector2 buttonSize)
    {
        if (this.requestedFont is not { LoadTask.IsCompletedSuccessfully: true }
            || this.FontFamilyExcludeFilter?.Invoke(this.selectedFont.FontId!.Family) is true)
        {
            ImGui.BeginDisabled();
            ImGui.Button("OK"u8, buttonSize);
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("OK"u8, buttonSize))
        {
            this.tcs.TrySetResult(this.selectedFont);
        }

        if (ImGui.Button("Cancel"u8, buttonSize))
        {
            this.Cancel();
        }

        var doRefresh = false;
        var isFirst = false;
        if (this.fontFamilies?.IsCompleted is not true)
        {
            isFirst = doRefresh = this.fontFamilies is null;
            ImGui.BeginDisabled();
            ImGui.Button("Refresh"u8, buttonSize);
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("Refresh"u8, buttonSize))
        {
            doRefresh = true;
        }

        if (doRefresh)
        {
            // The list is made in the background. Draw applies it to the selection.
            this.fontFamilies =
                this.fontFamilies?.ContinueWith(_ => RefreshBody())
                ?? Task.Run(RefreshBody);

            List<IFontFamilyId> RefreshBody()
            {
                var newFonts = new List<IFontFamilyId> { DalamudDefaultFontAndFamilyId.Instance };
                newFonts.AddRange(IFontFamilyId.ListDalamudFonts());
                newFonts.AddRange(IFontFamilyId.ListGameFonts());
                var systemFonts = IFontFamilyId.ListSystemFonts(!isFirst);
                systemFonts.Sort(
                    (a, b) => string.Compare(
                        this.ExtractName(a.Name),
                        this.ExtractName(b.Name),
                        StringComparison.CurrentCultureIgnoreCase));
                newFonts.AddRange(systemFonts);
                if (this.FontFamilyExcludeFilter is not null)
                    newFonts.RemoveAll(this.FontFamilyExcludeFilter);
                return newFonts;
            }
        }

        if (this.useAdvancedOptions)
        {
            if (ImGui.Button("Reset"u8, buttonSize))
            {
                this.selectedFont = this.selectedFont with
                {
                    LineHeight = 1f,
                    GlyphOffset = Vector2.Zero,
                    LetterSpacing = 0,
                };

                this.requestedFont = null;
            }
        }
    }

    // Applies a refreshed list of families to the selection, on the main thread.
    private void ApplyRefresh(List<IFontFamilyId> families)
    {
        this.appliedFamilies = families;
        this.previewFonts.Clear();
        var id = this.selectedFont.FontId!;
        this.UpdateSelectedFamilyAndFontIndices(
            families,
            id.Family.ToString() ?? string.Empty,
            id.ToString() ?? string.Empty);
        if (!Equals(this.selectedFont.FontId, id))
            this.requestedFont = null;
        this.firstDrawAfterRefresh = true;
    }

    private void UpdateSelectedFamilyAndFontIndices(
        List<IFontFamilyId> fonts,
        string familyName,
        string fontName)
    {
        this.selectedFontIndex = -1;
        this.selectedFamilyIndex = fonts.FindIndex(x => x.ToString() == familyName);
        if (this.selectedFamilyIndex == -1)
            return;

        var familyFonts = FontsOf(fonts[this.selectedFamilyIndex]);
        if (familyFonts.Count == 0)
            return;
        for (var i = 0; i < familyFonts.Count; i++)
        {
            if (familyFonts[i].ToString() == fontName)
            {
                this.selectedFontIndex = i;
                break;
            }
        }

        if (this.selectedFontIndex == -1)
            this.selectedFontIndex = 0;
        this.selectedFont = this.selectedFont with { FontId = familyFonts[this.selectedFontIndex] };
    }

    private string ExtractName(LocalizedName name) => name.Get(Service<FontFactory>.Get().InferredLocale);

    private bool TestName(LocalizedName name, string search) =>
        this.ExtractName(name).Contains(search, StringComparison.CurrentCultureIgnoreCase);
}
