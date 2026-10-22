using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Fonts.Internal.Layout;
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

    private readonly List<TextDecorationRegistration> decorations = [];

    /// <summary>Initializes a new instance of the <see cref="FontServicePluginScoped"/> class.</summary>
    [ServiceManager.ServiceConstructor]
    internal FontServicePluginScoped() => this.resolver.DefaultFontChanged += this.OnDefaultFontChanged;

    /// <inheritdoc/>
    public event Action? DefaultFontChanged;

    /// <inheritdoc/>
    public IFontSpec DefaultFont => FontSpec.Default;

    /// <inheritdoc/>
    public IFontSpec IconFont => FontSpec.Icon;

    /// <inheritdoc/>
    public IFontSpec IconFontFixedWidth => FontSpec.IconFixedWidth;

    /// <inheritdoc/>
    public IFontSpec MonoFont => FontSpec.Mono;

    /// <inheritdoc/>
    public ImPopFontDisposable Push(IFontSpec spec, float? sizePx = null) => this.resolver.Push(spec, sizePx);

    /// <inheritdoc/>
    public ImFontRef GetFont(IFontSpec spec) => this.resolver.Get(spec);

    /// <inheritdoc/>
    public Task<ImFontRef> GetFontAsync(
        IFontSpec spec, bool retry = false, CancellationToken cancellationToken = default) =>
        this.resolver.GetAsync(spec, retry, cancellationToken);

    /// <inheritdoc/>
    public ImFontRef? GetFont(ImFontPtr font) => this.resolver.Find(font);

    /// <inheritdoc/>
    public ImPopFontDisposable PushTextCallback(ITextMarkupCallback callback)
    {
        ThreadSafety.AssertMainThread();
        return TextCallbackStack.Push(callback);
    }

    /// <inheritdoc/>
    public void InvalidateTextCallback(ITextMarkupCallback callback) =>
        TextCallbackStack.Invalidate(callback ?? throw new ArgumentNullException(nameof(callback)));

    /// <inheritdoc/>
    public TextDecorationRegistration RegisterTextDecoration(
        TextDecorationDrawer drawer, TextDecorationFlags flags = default, int bands = 1)
    {
        var registration = new TextDecorationRegistration(TextDecorationRegistry.Register(drawer, flags, bands));
        lock (this.decorations)
            this.decorations.Add(registration);
        return registration;
    }

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        this.resolver.DefaultFontChanged -= this.OnDefaultFontChanged;
        this.DefaultFontChanged = null;
        lock (this.decorations)
        {
            foreach (var registration in this.decorations)
                registration.Dispose();
            this.decorations.Clear();
        }
    }

    private void OnDefaultFontChanged() => this.DefaultFontChanged.InvokeSafely();
}
