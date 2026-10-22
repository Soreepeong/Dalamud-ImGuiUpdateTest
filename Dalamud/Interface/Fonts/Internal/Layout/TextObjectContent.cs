using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts.Internal.Layout;

/// <summary>What an inline object of styled text shows.</summary>
/// <param name="Texture">The image's texture, or <see langword="default"/> for none.</param>
/// <param name="Size">The size of the image or entity.</param>
/// <param name="Uv0">Top-left texture coordinates.</param>
/// <param name="Uv1">Bottom-right texture coordinates.</param>
/// <param name="Entity">The entity, if this is one.</param>
/// <param name="SourceOffset">Byte offset of the entity in the text as read.</param>
/// <param name="Style">The style at the entity.</param>
/// <seealso cref="StyledText.Contents"/>
internal readonly record struct TextObjectContent(
    ImTextureRef Texture,
    Vector2 Size,
    Vector2 Uv0,
    Vector2 Uv1,
    TextEntity Entity,
    int SourceOffset,
    ImTextStyle Style);
