using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal;
using Dalamud.Plugin.Services;

namespace Dalamud.Interface.Fonts;

/// <summary>A text decoration a plugin registered.</summary>
/// <remarks>When disposed, text styles that still name it are drawn without it, and its ID does not name a
/// decoration registered later.</remarks>
/// <seealso cref="IFontService.RegisterTextDecoration"/>
public sealed class TextDecorationRegistration : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="TextDecorationRegistration"/> class.</summary>
    /// <param name="id">The ID.</param>
    internal TextDecorationRegistration(ImTextDecorationId id) => this.Id = id;

    /// <summary>Gets the ID, for <see cref="ImTextDecoration"/>.</summary>
    public ImTextDecorationId Id { get; }

    /// <summary>Gets a value indicating whether it has been unregistered.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Makes a decoration of this kind.</summary>
    /// <param name="context">The context to draw it with.</param>
    /// <returns>The decoration.</returns>
    public ImTextDecoration WithContext(uint context) => new(this.Id, context);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.IsDisposed)
            return;
        this.IsDisposed = true;
        TextDecorationRegistry.Unregister(this.Id);
    }
}
