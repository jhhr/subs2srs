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
//
//////////////////////////////////////////////////////////////////////////////

using System;
using System.IO;

namespace subs2srs
{
    /// <summary>
    /// Points GLib/GTK/GdkPixbuf at the data files shipped next to subs2srs.exe
    /// (see dist/windows/bundle-gtk.ps1). Only sets variables that are unset, so
    /// a developer running against a full MSYS2 installation keeps their setup.
    /// Must run before any GTK type is touched.
    /// </summary>
    static class WindowsRuntimeSetup
    {
        public static void Apply()
        {
            if (!OperatingSystem.IsWindows()) return;

            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string share = Path.Combine(baseDir, "share");
            string schemas = Path.Combine(share, "glib-2.0", "schemas");
            string loadersDir = Path.Combine(baseDir, "lib", "gdk-pixbuf-2.0", "2.10.0", "loaders");
            string loadersCache = Path.Combine(baseDir, "lib", "gdk-pixbuf-2.0", "2.10.0", "loaders.cache");

            // Only a bundled layout has these; a plain "dotnet run" against MSYS2 does not.
            if (Directory.Exists(share))
                SetIfUnset("XDG_DATA_DIRS", share);
            if (File.Exists(Path.Combine(schemas, "gschemas.compiled")))
                SetIfUnset("GSETTINGS_SCHEMA_DIR", schemas);
            if (File.Exists(loadersCache))
            {
                SetIfUnset("GDK_PIXBUF_MODULE_FILE", loadersCache);
                SetIfUnset("GDK_PIXBUF_MODULEDIR", loadersDir);
            }

            // Software rendering is the most predictable default on Windows
            // (RDP sessions, VMs and old GPUs have trouble with the GL/Vulkan paths).
            SetIfUnset("GSK_RENDERER", "cairo");
        }

        private static void SetIfUnset(string name, string value)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
