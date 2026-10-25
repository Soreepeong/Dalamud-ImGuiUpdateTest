using System.Collections.Generic;
using System.Threading;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Markup;
using Dalamud.Interface.Internal;
using Dalamud.IoC;
using Dalamud.IoC.Internal;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>The plugin-scoped font service.</summary>
[PluginInterface]
[ServiceManager.ScopedService]
#pragma warning disable SA1015
[ResolveVia<IFontService>]
#pragma warning restore SA1015
internal sealed class FontServicePluginScoped : IFontService, IInternalDisposableService
{
    [ServiceManager.ServiceDependency]
    private readonly FontResolver resolver = Service<FontResolver>.Get();

    // Relays its DefaultFontChanged, so that the event comes after the default font is applied, as UiBuilder's does.
    [ServiceManager.ServiceDependency]
    private readonly InterfaceManager interfaceManager = Service<InterfaceManager>.Get();

    private readonly Lock syncRoot = new();

    // The registrations not disposed yet. Null once the service is disposed.
    private HashSet<TextDecorationRegistration>? decorations = [];

    /// <summary>Initializes a new instance of the <see cref="FontServicePluginScoped"/> class.</summary>
    [ServiceManager.ServiceConstructor]
    internal FontServicePluginScoped() => this.interfaceManager.DefaultFontChanged += this.OnDefaultFontChanged;

    /// <inheritdoc/>
    public event Action? DefaultFontChanged;

    /// <inheritdoc/>
    public ImFontRef? FindFont(ImFontPtr font) => this.resolver.Find(font);

    /// <inheritdoc/>
    public ImPopFontDisposable PushTextCallback(ITextMarkupCallback callback)
    {
        ThreadSafety.AssertMainThread();
        TextCallbackStack.Push(callback);
        return new(textCallback: true);
    }

    /// <inheritdoc/>
    public void InvalidateTextCallback(ITextMarkupCallback callback) =>
        TextCallbackStack.Invalidate(callback ?? throw new ArgumentNullException(nameof(callback)));

    /// <inheritdoc/>
    public TextDecorationRegistration RegisterTextDecoration(
        TextDecorationDrawer drawer, TextDecorationFlags flags = TextDecorationFlags.OverText, int bands = 1)
    {
        lock (this.syncRoot)
        {
            ObjectDisposedException.ThrowIf(this.decorations is null, this);
            var registration = new TextDecorationRegistration(
                TextDecorationRegistry.Register(drawer, flags, bands),
                this.OnRegistrationDisposed);
            this.decorations.Add(registration);
            return registration;
        }
    }

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        this.interfaceManager.DefaultFontChanged -= this.OnDefaultFontChanged;
        this.DefaultFontChanged = null;
        HashSet<TextDecorationRegistration>? registrations;
        lock (this.syncRoot)
            (registrations, this.decorations) = (this.decorations, null);
        if (registrations is null)
            return;
        foreach (var registration in registrations)
            registration.Dispose();
    }

    private void OnRegistrationDisposed(TextDecorationRegistration registration)
    {
        lock (this.syncRoot)
            this.decorations?.Remove(registration);
    }

    private void OnDefaultFontChanged() => this.DefaultFontChanged.InvokeSafely();
}
