using System;
using System.IO;

namespace subs2srs.UiTests.Harness
{
    /// <summary>
    /// Optional PNG capture of a window's render tree. Only active when the
    /// SUBS2SRS_UITEST_ARTIFACTS environment variable names a directory.
    /// Never throws and never asserts: screenshots are diagnostics, not checks.
    /// Must be called on the GTK thread.
    /// </summary>
    public static class Screenshot
    {
        public static string? ArtifactDir
        {
            get
            {
                string? dir = Environment.GetEnvironmentVariable("SUBS2SRS_UITEST_ARTIFACTS");
                return string.IsNullOrWhiteSpace(dir) ? null : dir;
            }
        }

        public static string? TrySave(Gtk.Window window, string name)
        {
            string? dir = ArtifactDir;
            if (dir == null) return null;

            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, SanitizeName(name) + ".png");

                int w = window.GetWidth();
                int h = window.GetHeight();
                if (w <= 0 || h <= 0) return null;

                var paintable = Gtk.WidgetPaintable.New(window);
                var snapshot = Gtk.Snapshot.New();
                paintable.Snapshot(snapshot, w, h);
                var node = snapshot.ToNode();
                if (node == null) return null;

                Gsk.Renderer? renderer = null;
                try { renderer = ((Gtk.Native)window).GetRenderer(); } catch { }
                bool ownRenderer = false;
                if (renderer == null)
                {
                    renderer = Gsk.CairoRenderer.New();
                    var display = Gdk.Display.GetDefault();
                    if (display == null || !renderer.RealizeForDisplay(display)) return null;
                    ownRenderer = true;
                }

                var rect = Graphene.Rect.Alloc();
                rect.Init(0, 0, w, h);
                var texture = renderer.RenderTexture(node, rect);
                bool ok = texture.SaveToPng(path);
                if (ownRenderer) renderer.Unrealize();
                return ok ? path : null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[screenshot] {name}: {ex.Message}");
                return null;
            }
        }

        private static string SanitizeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
