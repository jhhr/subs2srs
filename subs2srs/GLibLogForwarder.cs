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

namespace subs2srs
{
    /// <summary>
    /// Forwards GLib/GTK structured log messages (warnings, criticals, …)
    /// into <see cref="Logger"/> so they are captured even when the app runs
    /// as a WinExe with no console attached. Messages are still written to
    /// the standard streams via GLib's default writer.
    /// </summary>
    static class GLibLogForwarder
    {
        // Keep the delegate alive: GLib stores only the raw function pointer.
        private static GLib.LogWriterFunc? _writer;

        public static void Install()
        {
            if (_writer != null) return;
            _writer = Write;
            try
            {
                GLib.Functions.LogSetWriterFunc(_writer);
            }
            catch (Exception ex)
            {
                // Must not break startup if the binding is unavailable.
                Console.Error.WriteLine($"Warning: could not install GLib log writer: {ex.Message}");
                _writer = null;
            }
        }

        private static GLib.LogWriterOutput Write(GLib.LogLevelFlags level, GLib.LogField[] fields)
        {
            const GLib.LogLevelFlags forwarded =
                GLib.LogLevelFlags.LevelError
                | GLib.LogLevelFlags.LevelCritical
                | GLib.LogLevelFlags.LevelWarning;

            if ((level & forwarded) != 0)
            {
                try
                {
                    string text = GLib.Functions.LogWriterFormatFields(level, fields, false);
                    // Drop the trailing newline GLib adds.
                    text = text.TrimEnd('\r', '\n');
                    if ((level & GLib.LogLevelFlags.LevelWarning) != 0)
                        Logger.Instance.warning(text);
                    else
                        Logger.Instance.error(text);
                }
                catch
                {
                    // Never throw from inside a GLib log writer.
                }
            }

            // Let GLib also print it to the console (when there is one).
            return GLib.Functions.LogWriterDefault(level, fields, IntPtr.Zero);
        }
    }
}
