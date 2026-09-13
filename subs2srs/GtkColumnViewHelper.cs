//  Copyright (C) 2026 fkzys and contributors
//
//  This file is part of subs2srs.
//
//  subs2srs is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  subs2srs is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with subs2srs.  If not, see <http://www.gnu.org/licenses/>.

using System;

namespace subs2srs
{
    /// <summary>
    /// Helpers for GTK4 ColumnViewColumn sizing, widget tree traversal,
    /// and CSS injection, built on the managed gir.core bindings.
    ///
    /// Earlier versions P/Invoked "gtk-4" directly. That library name only
    /// resolves on Linux (Windows ships libgtk-4-1.dll), so everything here
    /// now goes through gir.core, which already knows the per-platform
    /// library names.
    ///
    /// Important: never combine SetExpand(true) with SetFixedWidth on
    /// the same column — GTK4 layout engine will fight the drag-resize,
    /// causing the cursor to drift and wrong columns to move.
    /// Use either fixed_width (for resizable columns) or expand (for
    /// the last column that absorbs remaining space), not both.
    /// </summary>
    public static class GtkColumnViewHelper
    {
        // ── ColumnViewColumn properties ────────────────────────────────

        /// <summary>
        /// Enable or disable drag-resize handle on a ColumnViewColumn.
        /// </summary>
        public static void SetResizable(Gtk.ColumnViewColumn column, bool resizable)
        {
            column.SetResizable(resizable);
        }

        /// <summary>
        /// Set fixed width in pixels. Pass -1 to unset.
        /// Do not use together with SetExpand(true) on the same column.
        /// </summary>
        public static void SetFixedWidth(Gtk.ColumnViewColumn column, int width)
        {
            column.SetFixedWidth(width);
        }

        /// <summary>
        /// Set whether this column expands to fill remaining space.
        /// Do not use together with SetFixedWidth on the same column.
        /// </summary>
        public static void SetExpand(Gtk.ColumnViewColumn column, bool expand)
        {
            column.SetExpand(expand);
        }

        // ── Inline CSS on a single widget via CssProvider ────────────
        // GTK4 does not have gtk_widget_set_style(); instead we create
        // a per-widget CssProvider and add it to the widget's own
        // StyleContext (display-level provider would need a selector).

        /// <summary>
        /// Apply inline CSS to a single widget. Creates a CssProvider,
        /// loads the CSS wrapped in a wildcard selector, and attaches it
        /// to the widget's own StyleContext at priority 900 (above theme).
        /// </summary>
        public static void ApplyInlineCss(Gtk.Widget? widget, string css)
        {
            if (widget == null) return;
            var provider = Gtk.CssProvider.New();
            // Wrap in "* { ... }" so it matches the widget itself
            provider.LoadFromString("* { " + css + " }");
#pragma warning disable CS0612, CS0618 // GetStyleContext/AddProvider: deprecated in GTK 4.10, still functional
            var ctx = widget.GetStyleContext();
            ctx?.AddProvider(provider, 900);
#pragma warning restore CS0612, CS0618
        }

        /// <summary>
        /// Walk the ColumnView widget tree to find header row buttons
        /// and apply inline CSS to each one. GTK4 ColumnView internal
        /// structure: ColumnView → first child is the header listbox,
        /// header listbox → children are GtkColumnViewRowWidget,
        /// each row widget → children are button widgets for each column.
        ///
        /// Call this after the ColumnView has been mapped (shown),
        /// or defer with GLib.Functions.IdleAdd so the widget tree
        /// is fully built.
        /// </summary>
        public static void StyleColumnViewHeaders(Gtk.ColumnView columnView, string css)
        {
            // The header is the first child of the ColumnView
            var header = columnView.GetFirstChild();
            if (header == null) return;

            // The header contains row widgets; iterate their children (buttons)
            var rowWidget = header.GetFirstChild();
            while (rowWidget != null)
            {
                // Each button inside the row widget is a column header
                var button = rowWidget.GetFirstChild();
                while (button != null)
                {
                    ApplyInlineCss(button, css);
                    button = button.GetNextSibling();
                }
                rowWidget = rowWidget.GetNextSibling();
            }
        }

        // ── Global CSS ───────────────────────────────────────────────

        /// <summary>
        /// Register a global CSS stylesheet for the default display.
        /// Priority 800 overrides theme defaults (600) but not user
        /// stylesheets (GTK_STYLE_PROVIDER_PRIORITY_USER = 800 is equal,
        /// so we use 800 to match user-level priority).
        /// </summary>
        public static void ApplyGlobalCss(string css)
        {
            var provider = Gtk.CssProvider.New();
            provider.LoadFromString(css);

            var display = Gdk.Display.GetDefault();
            if (display != null)
            {
                Gtk.StyleContext.AddProviderForDisplay(display, provider, 800);
            }
        }
    }
}
