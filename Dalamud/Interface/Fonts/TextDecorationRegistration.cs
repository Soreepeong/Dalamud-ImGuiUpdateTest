using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal;
using Dalamud.Plugin.Services;

namespace Dalamud.Interface.Fonts;

/// <summary>A text decoration a plugin registered.</summary>
/// <seealso cref="IFontService.RegisterTextDecoration"/>
public sealed class TextDecorationRegistration : IDisposable
{
    private readonly Action<TextDecorationRegistration>? onDispose;

    /// <summary>Initializes a new instance of the <see cref="TextDecorationRegistration"/> class.</summary>
    /// <param name="id">The ID.</param>
    /// <param name="onDispose">Called once when it is disposed, after the decoration is unregistered.</param>
    internal TextDecorationRegistration(ImTextDecorationId id, Action<TextDecorationRegistration>? onDispose = null)
    {
        this.Id = id;
        this.onDispose = onDispose;
    }

    /// <summary>Gets the ID, for <see cref="ImTextDecoration"/>.</summary>
    public ImTextDecorationId Id { get; }

    /// <summary>Gets a value indicating whether it has been unregistered.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Makes a decoration of this kind.</summary>
    /// <param name="context">The context to draw it with.</param>
    /// <param name="data">The data to draw it with.</param>
    /// <returns>The decoration.</returns>
    public ImTextDecoration ToDecoration(uint context = 0, ulong data = 0) => new(this.Id, context, data);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.IsDisposed)
            return;
        this.IsDisposed = true;
        TextDecorationRegistry.Unregister(this.Id);
        this.onDispose?.Invoke(this);
    }
}
