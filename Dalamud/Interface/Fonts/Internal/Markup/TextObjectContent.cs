using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts.Internal.Markup;

/// <summary>What an inline object of styled text shows.</summary>
/// <param name="Entity">The entity.</param>
/// <param name="SourceOffset">Byte offset of the entity in the text as read.</param>
/// <param name="Style">The style of the font given to the entity. Default for the font as it is.</param>
/// <param name="Size">The size of the entity in pixels, with a height relative to the line resolved.</param>
/// <seealso cref="StyledText.Contents"/>
internal readonly record struct TextObjectContent(TextEntity Entity, int SourceOffset, ImTextStyle Style, Vector2 Size);
