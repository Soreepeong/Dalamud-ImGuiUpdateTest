using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace Dalamud.Interface.Fonts;

/// <summary>Decides how text in fonts with <see cref="ImTextStyle.Markup"/> is drawn.</summary>
/// <remarks>
/// <para>Callbacks are asked from the most recently pushed one down, and the first with an answer decides. The defaults
/// do nothing with it. A markup also asks the callbacks of its own interface, such as
/// <see cref="ISeStringCallback"/>.</para>
/// <para>Answers are part of the layout, which is cached, except those of <see cref="GetLinkFont"/> for hovered and
/// held links, which are asked whenever the text is drawn. Give the same answers for the same text, and call
/// <see cref="IFontService.InvalidateTextCallback"/> when they change. Asked on the main thread.</para>
/// </remarks>
/// <seealso cref="IFontService.PushTextCallback"/>
public interface ITextMarkupCallback
{
    /// <summary>Gets something to draw instead of the text at a byte offset.</summary>
    /// <param name="context">Where it is.</param>
    /// <param name="entity">The entity.</param>
    /// <returns><see langword="true"/> if there is one, or <see langword="false"/> to pass.</returns>
    bool TryGetEntity(in TextEntityContext context, out TextEntity entity)
    {
        entity = default;
        return false;
    }

    /// <summary>Gets the font to draw a link with.</summary>
    /// <param name="link">The link, and what is happening to it.</param>
    /// <param name="font">The font the link is drawn with otherwise. For <see cref="TextLinkState.Hovered"/> and
    /// <see cref="TextLinkState.Held"/>, it is the one given for <see cref="TextLinkState.Normal"/>.</param>
    /// <returns>The font (<paramref name="font"/> to draw the link as it is), or <see cref="ImFontPtr.Null"/> to
    /// pass.</returns>
    /// <remarks>
    /// <para><see cref="TextLinkState.Normal"/> is asked as the layout is made, for each run of the link, and is part
    /// of the layout. Its whole style applies but <see cref="ImTextStyle.Markup"/>. Links are drawn as the text around
    /// them if no callback answers.</para>
    /// <para><see cref="TextLinkState.Hovered"/> and <see cref="TextLinkState.Held"/> are asked whenever the text is
    /// drawn. Only the color and the decorations of their style apply. If no callback answers, the link gets a
    /// background of <see cref="ImGuiCol.ButtonHovered"/>, or <see cref="ImGuiCol.ButtonActive"/> while held.</para>
    /// </remarks>
    ImFontPtr GetLinkFont(in TextLinkContext link, ImFontPtr font) => ImFontPtr.Null;
}
