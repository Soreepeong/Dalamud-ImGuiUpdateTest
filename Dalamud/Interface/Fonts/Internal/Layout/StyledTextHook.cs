using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Interface.Utility;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>What styled text laid out for ImGui does when drawn. It draws its images and entities, and handles its
/// links.</summary>
internal sealed unsafe class StyledTextHook : ITextRenderHook
{
    private const ImGuiButtonFlags LinkButtons =
        ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle;

    private readonly ImTextMarkup markup;
    private readonly TextObjectContent[] contents;
    private readonly TextLink[] links;
    private readonly byte[] source;
    private readonly int[]? sourceToDrawn;
    private readonly ITextMarkupCallback[] callbacks;
    private readonly ManagedFont font;
    private readonly float size;
    private readonly int sourceHash;
    private ulong heldLink;

    /// <summary>Initializes a new instance of the <see cref="StyledTextHook"/> class.</summary>
    /// <param name="markup">The markup of the text.</param>
    /// <param name="styled">The styled text, whose source, objects and links are kept.</param>
    /// <param name="callbacks">The callbacks it was laid out with.</param>
    /// <param name="font">The font.</param>
    /// <param name="size">The font size.</param>
    public StyledTextHook(
        ImTextMarkup markup, StyledText styled, ITextMarkupCallback[] callbacks, ManagedFont font, float size)
    {
        this.markup = markup;
        this.contents = [.. styled.Contents];
        this.links = [.. styled.Links];
        this.source = styled.Source;
        this.sourceToDrawn = styled.SourceToDrawn;
        this.callbacks = callbacks;
        this.font = font;
        this.size = size;
        var hash = default(HashCode);
        hash.AddBytes(this.source);
        this.sourceHash = hash.ToHashCode();
    }

    /// <inheritdoc/>
    public void DrawObject(ImDrawListPtr drawList, int objectIndex, Vector2 topLeft, uint col)
    {
        ref readonly var c = ref this.contents[objectIndex];
        if (c.Entity)
        {
            c.Entity.Draw(
                drawList,
                topLeft,
                new(this.markup, this.source, c.SourceOffset, this.font.GetVariant(c.Style), this.size));
        }
        else if (c.Texture.TexData is not null || !c.Texture.TexID.IsNull)
        {
            var tint = ColorHelpers.ApplyOpacity(uint.MaxValue, (col >> 24) / 255f);
            drawList.AddImage(c.Texture, topLeft, topLeft + c.Size, c.Uv0, c.Uv1, tint);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Links react to the mouse when the text is drawn into the current window, as buttons. They do not
    /// take the mouse from items submitted before them, such as a selectable the text is the label of.</remarks>
    public bool BeginRender(
        TextLayout layout, ImDrawListPtr drawList, Vector2 pos, out ulong userData, out ImTextStyle style)
    {
        userData = 0;
        style = default;
        TextCallbackStack.SetInteraction(default);
        var window = ImGui.GetCurrentContext().CurrentWindow;
        if (this.links.Length == 0 || window.IsNull || window.DrawList.Handle != drawList.Handle)
            return false;

        // The link under the mouse, or the one held down until it is released.
        var mouse = ImGui.GetMousePos() - pos;
        var hit = layout.HitTest(mouse);
        var link = 0ul;
        var rect = default(Vector4);
        if (hit is { IsInside: true, UserData: not 0 })
        {
            foreach (var r in layout.GetRunRects(hit.RunIndex))
            {
                if (mouse.X >= r.X && mouse.Y >= r.Y && mouse.X < r.Z && mouse.Y < r.W)
                    (link, rect) = (hit.UserData, r);
            }
        }

        if (link == 0 && this.heldLink != 0 && ImGui.GetCurrentContext().ActiveId == this.IdOf(this.heldLink))
            (link, rect) = (this.heldLink, FirstRectOf(layout, this.heldLink));
        if (link == 0)
            return false;

        // Links are not items (ItemAdd), so they keep their IDs alive themselves. ImGui drops a held ID otherwise.
        var hovered = false;
        var held = false;
        var id = this.IdOf(link);
        var bb = new ImRect(pos + new Vector2(rect.X, rect.Y), pos + new Vector2(rect.Z, rect.W));
        ImGuiP.KeepAliveID(id);
        var pressed = ImGuiP.ButtonBehavior(bb, id, ref hovered, ref held, LinkButtons);
        this.heldLink = held ? link : 0;

        var flags = hovered ? TextInteractionFlags.Hover : TextInteractionFlags.None;
        if (pressed)
        {
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                flags |= TextInteractionFlags.LeftClick;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                flags |= TextInteractionFlags.RightClick;
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
                flags |= TextInteractionFlags.MiddleClick;
        }

        if (hovered && ImGui.IsMouseClicked((ImGuiMouseButton)3))
            flags |= TextInteractionFlags.X1Click;
        if (hovered && ImGui.IsMouseClicked((ImGuiMouseButton)4))
            flags |= TextInteractionFlags.X2Click;

        ref readonly var l = ref this.links[(int)(link - 1)];
        var drawnBegin = this.sourceToDrawn?[l.Offset] ?? l.Offset;
        var drawnEnd = this.sourceToDrawn?[l.Offset + l.Length] ?? l.Offset + l.Length;
        TextCallbackStack.SetInteraction(
            new(this.markup, drawnBegin, drawnEnd - drawnBegin, this.source, l, flags, held));
        if (!hovered && !held)
            return false;

        // The callbacks may draw the link with another font. By default, it gets a background.
        var runStyle = layout.GetRunStyle(hit.RunIndex >= 0 && hit.UserData == link ? hit.RunIndex : -1);
        var runFont = this.font.GetVariant(runStyle);
        var context = new TextLinkContext(
            this.markup,
            this.source,
            l.Offset,
            l.Length,
            this.source.AsSpan(l.TargetOffset, l.TargetLength),
            held ? TextLinkState.Held : TextLinkState.Hovered);
        foreach (var callback in this.callbacks)
        {
            var linkFont = callback.GetLinkFont(context, runFont);
            if (!linkFont.IsNull)
            {
                (userData, style) = (link, linkFont.Style);
                return true;
            }
        }

        userData = link;
        style = runStyle with
        {
            Background = ImGui.GetColorU32(held ? ImGuiCol.ButtonActive : ImGuiCol.ButtonHovered),
        };
        return true;
    }

    private static Vector4 FirstRectOf(TextLayout layout, ulong link)
    {
        for (var i = 0; i < layout.RunCount; i++)
        {
            if (layout.GetRunUserData(i) == link && layout.GetRunRects(i) is [var first, ..])
                return first;
        }

        return default;
    }

    private uint IdOf(ulong link) => ImGui.GetID(HashCode.Combine(this.sourceHash, link));
}
