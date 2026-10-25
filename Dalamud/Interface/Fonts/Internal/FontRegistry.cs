using System.Collections.Generic;
using System.Linq;

using Dalamud.Interface.Fonts.Internal.Markup;
using Dalamud.Interface.Fonts.Internal.Sources;
using Dalamud.Logging.Internal;

namespace Dalamud.Interface.Fonts.Internal;

/// <summary>The font sources and markup stylers of the font engine.</summary>
/// <remarks>It is looked up when fonts are made, never per frame.</remarks>
internal sealed class FontRegistry : IDisposable
{
    private static readonly ModuleLog Log = ModuleLog.Create<FontRegistry>();

    private readonly IFontSourceProvider[] providers;
    private readonly TextStyler[] stylers;

    /// <summary>Initializes a new instance of the <see cref="FontRegistry"/> class, with the font sources and stylers
    /// of Dalamud.</summary>
    /// <remarks>It makes one of each non-abstract <see cref="IFontSourceProvider"/> class and <see cref="TextStyler"/>
    /// subclass in Dalamud, so that deleting the folder of a font unit removes it. A type whose constructor throws is
    /// logged and left out.</remarks>
    public FontRegistry()
        : this(CreateAll<IFontSourceProvider>(), CreateAll<TextStyler>())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FontRegistry"/> class, with the given font sources and
    /// stylers.</summary>
    /// <param name="providers">The font sources, asked in order.</param>
    /// <param name="stylers">The stylers, set in <see cref="TextStylers"/> at once.</param>
    public FontRegistry(IEnumerable<IFontSourceProvider> providers, IEnumerable<TextStyler> stylers)
    {
        this.providers = [.. providers];
        this.stylers = [.. stylers];
        foreach (var styler in this.stylers)
            TextStylers.Register(styler);
    }

    /// <summary>Gets the font of a font id from the first source that serves it.</summary>
    /// <param name="id">The font id.</param>
    /// <param name="fontNo">Index of the face in a collection.</param>
    /// <param name="locale">The locale to draw text in.</param>
    /// <returns>The font, or null if no source serves the id.</returns>
    /// <remarks>The sources serve different ids, so that the order does not matter.</remarks>
    public Font? GetSourceFont(IFontId id, int fontNo, string locale)
    {
        foreach (var provider in this.providers)
        {
            if (provider.TryGetFont(id, fontNo, locale, out var font))
                return font;
        }

        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var styler in this.stylers)
            TextStylers.Unregister(styler);

        for (var i = this.providers.Length - 1; i >= 0; i--)
        {
            if (this.providers[i] is not IDisposable disposable)
                continue;
            try
            {
                disposable.Dispose();
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to dispose {Object}", disposable);
            }
        }
    }

    // The order is by name, so that it does not depend on how the compiler lays out the types.
    private static T[] CreateAll<T>()
        where T : class =>
    [
        .. typeof(FontRegistry).Assembly
            .GetTypes()
            .Where(x => x is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } &&
                        x.IsAssignableTo(typeof(T)))
            .OrderBy(x => x.FullName, StringComparer.Ordinal)
            .Select(CreateOrNull<T>)
            .OfType<T>(),
    ];

    private static T? CreateOrNull<T>(Type type)
        where T : class
    {
        try
        {
            return (T)Activator.CreateInstance(type, nonPublic: true)!;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to make the font engine type {Type}; ignoring", type.FullName);
            return null;
        }
    }
}
