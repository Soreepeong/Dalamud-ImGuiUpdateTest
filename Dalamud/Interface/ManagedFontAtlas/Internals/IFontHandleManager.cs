namespace Dalamud.Interface.ManagedFontAtlas.Internals;

/// <summary>
/// Manager for <see cref="IFontHandle"/>.
/// </summary>
internal interface IFontHandleManager : IDisposable
{
    /// <summary>
    /// Gets the name of the font handle manager. For logging and debugging purposes.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Decrease font reference counter.
    /// </summary>
    /// <param name="handle">Handle being released.</param>
    void FreeFontHandle(IFontHandle handle);
}
