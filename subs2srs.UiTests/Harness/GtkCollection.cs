using Xunit;

namespace subs2srs.UiTests.Harness
{
    /// <summary>
    /// All GTK tests share one <see cref="GtkFixture"/> (one main loop per process).
    /// Mark test classes with [Collection(GtkCollection.Name)].
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class GtkCollection : ICollectionFixture<GtkFixture>
    {
        public const string Name = "Gtk";
    }
}
