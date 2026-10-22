using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Fonts;

/// <summary>Draws an entity.</summary>
/// <param name="drawList">The draw list.</param>
/// <param name="topLeft">The top left of the entity, at the top of its line.</param>
/// <param name="context">Where it is.</param>
/// <seealso cref="TextEntity"/>
public delegate void TextEntityDrawDelegate(ImDrawListPtr drawList, Vector2 topLeft, in TextEntityContext context);
