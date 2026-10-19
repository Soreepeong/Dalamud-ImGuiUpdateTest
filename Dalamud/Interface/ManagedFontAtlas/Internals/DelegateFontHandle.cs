using System.Collections.Generic;
using System.Threading;

namespace Dalamud.Interface.ManagedFontAtlas.Internals;

/// <summary>
/// A font handle representing a user-callback generated font.
/// </summary>
internal sealed class DelegateFontHandle : FontHandle
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DelegateFontHandle"/> class.
    /// </summary>
    /// <param name="manager">An instance of <see cref="IFontHandleManager"/>.</param>
    /// <param name="callOnBuildStepChange">Callback for <see cref="IFontAtlas.BuildStepChange"/>.</param>
    public DelegateFontHandle(IFontHandleManager manager, FontAtlasBuildStepDelegate callOnBuildStepChange)
        : base(manager)
    {
        this.CallOnBuildStepChange = callOnBuildStepChange;
    }

    /// <summary>
    /// Gets the function to be called on build step changes.
    /// </summary>
    public FontAtlasBuildStepDelegate CallOnBuildStepChange { get; }

    /// <summary>
    /// Manager for <see cref="DelegateFontHandle"/>s.
    /// </summary>
    internal sealed class HandleManager : IFontHandleManager
    {
        private readonly HashSet<DelegateFontHandle> handles = [];
        private readonly Lock syncRoot = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="HandleManager"/> class.
        /// </summary>
        /// <param name="atlasName">The name of the owner atlas.</param>
        public HandleManager(string atlasName) => this.Name = $"{atlasName}:{nameof(DelegateFontHandle)}:Manager";

        /// <inheritdoc/>
        public string Name { get; }

        /// <inheritdoc/>
        public void Dispose()
        {
            using (this.syncRoot.EnterScope())
                this.handles.Clear();
        }

        /// <inheritdoc cref="IFontAtlas.NewDelegateFontHandle"/>
        public IFontHandle NewFontHandle(FontAtlasBuildStepDelegate buildStepDelegate)
        {
            var key = new DelegateFontHandle(this, buildStepDelegate);
            using (this.syncRoot.EnterScope())
                this.handles.Add(key);
            return key;
        }

        /// <inheritdoc/>
        public void FreeFontHandle(IFontHandle handle)
        {
            if (handle is not DelegateFontHandle cgfh)
                return;

            using (this.syncRoot.EnterScope())
                this.handles.Remove(cgfh);
        }
    }
}
