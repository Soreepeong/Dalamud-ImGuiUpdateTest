using System.Numerics;
using System.Runtime.CompilerServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Layout;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Logging.Internal;

namespace Dalamud.Interface.Fonts.Internal.Markup;

/// <summary>What styled text laid out for ImGui does when drawn. It draws its entities, and handles its links.
/// </summary>
/// <seealso cref="TextLayout.RenderHook"/>
internal sealed unsafe class StyledTextHook
{
    // Links are not items, so they must not become the navigation ID.
    private const ImGuiButtonFlags LinkButtons =
        ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle |
        (ImGuiButtonFlags)ImGuiButtonFlagsPrivate.NoNavFocus;

    private static readonly ModuleLog Log = new(typeof(StyledTextHook));

    private readonly ImTextMarkup markup;
    private readonly TextObjectContent[] contents;
    private readonly TextLink[] links;
    private readonly byte[] source;
    private readonly int[]? sourceToDrawn;
    private readonly ITextMarkupCallback[] callbacks;
    private readonly Font font;
    private readonly float size;
    private readonly int sourceHash;

    // The link BeginRender found hovered or held last, for HighlightStyleOf.
    private ulong highlightLink;
    private bool highlightHeld;

    // The ID and link last held, for FindHeldLink.
    private uint heldId;
    private ulong heldLink;

    /// <summary>Initializes a new instance of the <see cref="StyledTextHook"/> class.</summary>
    /// <param name="markup">The markup of the text.</param>
    /// <param name="styled">The styled text, whose source, objects and links are kept.</param>
    /// <param name="callbacks">The callbacks it was laid out with.</param>
    /// <param name="font">The font.</param>
    /// <param name="size">The font size.</param>
    public StyledTextHook(
        ImTextMarkup markup, StyledText styled, ITextMarkupCallback[] callbacks, Font font, float size)
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

    /// <summary>Draws an inline object.</summary>
    /// <param name="drawList">The draw list.</param>
    /// <param name="objectIndex">The object.</param>
    /// <param name="topLeft">The top left of its box.</param>
    /// <param name="col">The text color the layout is drawn with.</param>
    /// <remarks>An entity that throws is logged and left out, so that the rest of the text is drawn.</remarks>
    public void DrawObject(ImDrawListPtr drawList, int objectIndex, Vector2 topLeft, uint col)
    {
        ref readonly var c = ref this.contents[objectIndex];
        try
        {
            c.Entity.Draw(
                drawList,
                topLeft,
                new(
                    this.markup,
                    this.source,
                    c.SourceOffset,
                    this.font,
                    c.Style,
                    this.size,
                    c.Entity.Data,
                    c.Size,
                    col));
        }
        catch (Exception e)
        {
            ReportCallbackFailure(c.Entity.GetType(), e);
        }
    }

    /// <summary>Called before the layout is drawn.</summary>
    /// <param name="layout">The layout.</param>
    /// <param name="drawList">The draw list.</param>
    /// <param name="pos">Position of the layout origin.</param>
    /// <param name="userData">The user data of the runs to draw with <see cref="HighlightStyleOf"/>, not 0.</param>
    /// <returns>Whether to draw those runs with it.</returns>
    /// <remarks>Links react to the mouse when the text is drawn into the current window, as buttons. They do not
    /// take the mouse from items submitted before them, such as a selectable the text is the label of. Their IDs are
    /// from the ID stack and where the text is, as equal texts share this hook.</remarks>
    public bool BeginRender(TextLayout layout, ImDrawListPtr drawList, Vector2 pos, out ulong userData)
    {
        userData = 0;
        TextInteractionState.Set(default);
        var g = ImGui.GetCurrentContext();
        var window = g.CurrentWindow;
        if (this.links.Length == 0 || window.IsNull || window.DrawList.Handle != drawList.Handle)
            return false;

        // Relative to the window's contents, so that scrolling or moving the window keeps the IDs.
        var at = pos - window.DC.CursorStartPos;

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

        if (link == 0 && g.ActiveId != 0 && g.ActiveId == this.heldId && g.ActiveIdWindow.Handle == window.Handle)
            (link, rect) = this.FindHeldLink(layout, at, g.ActiveId);
        if (link == 0)
            return false;

        // Links are not items (ItemAdd), so they keep their IDs alive themselves. ImGui drops a held ID otherwise.
        var hovered = false;
        var held = false;
        var id = this.IdOf(link, at);
        var bb = new ImRect(pos + new Vector2(rect.X, rect.Y), pos + new Vector2(rect.Z, rect.W));
        ImGuiP.KeepAliveID(id);
        var pressed = ImGuiP.ButtonBehavior(bb, id, ref hovered, ref held, LinkButtons);
        if (held)
            (this.heldId, this.heldLink) = (id, link);

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

        if (hovered)
            flags |= ExtraButtonClicks(bb);

        ref readonly var l = ref this.links[(int)(link - 1)];
        var drawnBegin = this.sourceToDrawn?[l.Offset] ?? l.Offset;
        var drawnEnd = this.sourceToDrawn?[l.Offset + l.Length] ?? l.Offset + l.Length;
        TextInteractionState.Set(
            new(this.markup, drawnBegin, drawnEnd - drawnBegin, this.source, l, flags, held));
        if (!hovered && !held)
            return false;

        (userData, this.highlightLink, this.highlightHeld) = (link, link, held);
        return true;
    }

    /// <summary>Gets the style to draw a run of the link found by <see cref="BeginRender"/> with.</summary>
    /// <param name="runStyle">The style of the run.</param>
    /// <returns>The style. Its bold, italic and markup do not apply.</returns>
    /// <remarks>The callbacks may draw the link in another style. By default, it gets a background.</remarks>
    public ImTextStyle HighlightStyleOf(in ImTextStyle runStyle)
    {
        ref readonly var l = ref this.links[(int)(this.highlightLink - 1)];
        var context = new TextLinkContext(
            this.markup,
            this.source,
            l.Offset,
            l.Length,
            this.source.AsSpan(l.TargetOffset, l.TargetLength),
            this.highlightHeld ? TextLinkState.Held : TextLinkState.Hovered);
        foreach (var callback in this.callbacks)
        {
            if (TryGetLinkStyle(callback, context, runStyle, out var linkStyle))
                return linkStyle;
        }

        return runStyle with
        {
            Background = ImGui.GetColorU32(this.highlightHeld ? ImGuiCol.ButtonActive : ImGuiCol.ButtonHovered),
        };
    }

    // A callback that throws has no say. Only called for a link under the mouse or held.
    private static bool TryGetLinkStyle(
        ITextMarkupCallback callback, in TextLinkContext context, in ImTextStyle runStyle, out ImTextStyle linkStyle)
    {
        try
        {
            return callback.TryGetLinkStyle(context, runStyle, out linkStyle);
        }
        catch (Exception e)
        {
            ReportCallbackFailure(callback.GetType(), e);
            linkStyle = default;
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReportCallbackFailure(Type type, Exception e) =>
        Log.Error(e, "A text callback of {Type} failed, and was left out", type);

    private static Vector4 FirstRectOf(TextLayout layout, ulong link)
    {
        for (var i = 0; i < layout.RunCount; i++)
        {
            if (layout.GetRunUserData(i) == link && layout.GetRunRects(i) is [var first, ..])
                return first;
        }

        return default;
    }

    // ImGui buttons cannot take the extra mouse buttons. As for the others, a click is a press and a release on the
    // link.
    private static TextInteractionFlags ExtraButtonClicks(in ImRect bb)
    {
        var flags = TextInteractionFlags.None;
        var clickedPos = ImGui.GetIO().MouseClickedPos;
        if (ImGui.IsMouseReleased((ImGuiMouseButton)3) && IsIn(bb, clickedPos[3]))
            flags |= TextInteractionFlags.X1Click;
        if (ImGui.IsMouseReleased((ImGuiMouseButton)4) && IsIn(bb, clickedPos[4]))
            flags |= TextInteractionFlags.X2Click;
        return flags;

        static bool IsIn(in ImRect bb, Vector2 p) =>
            p.X >= bb.Min.X && p.Y >= bb.Min.Y && p.X < bb.Max.X && p.Y < bb.Max.Y;
    }

    // The link of this text drawn at a place whose ID is active, and its first rectangle. Only the link held last can
    // be it, as the active ID is taken by a press on the link.
    private (ulong Link, Vector4 Rect) FindHeldLink(TextLayout layout, Vector2 at, uint activeId) =>
        this.heldLink != 0 && this.IdOf(this.heldLink, at) == activeId
            ? (this.heldLink, FirstRectOf(layout, this.heldLink))
            : default;

    private uint IdOf(ulong link, Vector2 at) => ImGui.GetID(HashCode.Combine(this.sourceHash, link, at.X, at.Y));
}
