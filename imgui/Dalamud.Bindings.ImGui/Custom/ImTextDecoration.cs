namespace Dalamud.Bindings.ImGui;

/// <summary>A decoration of text.</summary>
/// <param name="Id">The kind of decoration.</param>
/// <param name="Context">What the decoration is drawn with.</param>
/// <param name="Data">Extra context.</param>
/// <remarks>The meaning of both <paramref name="Context"/> and <paramref name="Data"/> is decoration-dependent.
/// </remarks>
/// <seealso cref="ImTextStyle.ExtraDecoration"/>
public readonly record struct ImTextDecoration(ImTextDecorationId Id, uint Context = 0, ulong Data = 0)
{
    /// <summary>Gets a value indicating whether there is no decoration.</summary>
    public bool IsNone => this.Id.IsNone;
}
