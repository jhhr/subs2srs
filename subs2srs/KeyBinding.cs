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
using System.Collections.Generic;

namespace subs2srs
{
  /// <summary>
  /// One keyboard shortcut in GTK accelerator syntax: optional modifiers in
  /// angle brackets followed by a key name, e.g. <c>&lt;Control&gt;Up</c>,
  /// <c>&lt;Shift&gt;&lt;Alt&gt;BackSpace</c>, <c>w</c>.
  ///
  /// gir.core 0.7 does not bind gtk_accelerator_parse, so the parsing is done
  /// here (GTK-free, so it is unit-testable) and the key name is resolved to a
  /// keyval by the caller (normally <c>Gdk.Functions.KeyvalFromName</c>).
  /// </summary>
  public sealed class KeyBinding
  {
    [Flags]
    public enum Mods
    {
      None = 0,
      Shift = 1,
      Control = 2,
      Alt = 4,
      Super = 8,
      Meta = 16,
      Hyper = 32,
    }

    public string KeyName { get; }
    public Mods Modifiers { get; }

    public KeyBinding(string keyName, Mods modifiers)
    {
      KeyName = keyName;
      Modifiers = modifiers;
    }

    /// <summary>Parse one accelerator; returns null when the text is not valid.</summary>
    public static KeyBinding? Parse(string accelerator)
    {
      if (string.IsNullOrWhiteSpace(accelerator)) return null;
      string s = accelerator.Trim();
      Mods mods = Mods.None;

      while (s.StartsWith("<"))
      {
        int close = s.IndexOf('>');
        if (close < 0) return null;
        string mod = s.Substring(1, close - 1).Trim().ToLowerInvariant();
        switch (mod)
        {
          case "shift": mods |= Mods.Shift; break;
          case "control": case "ctrl": case "ctl": case "primary": mods |= Mods.Control; break;
          case "alt": case "mod1": mods |= Mods.Alt; break;
          case "super": mods |= Mods.Super; break;
          case "meta": mods |= Mods.Meta; break;
          case "hyper": mods |= Mods.Hyper; break;
          default: return null;
        }
        s = s.Substring(close + 1).Trim();
      }

      if (s.Length == 0 || s.Contains(' ') || s.Contains('<')) return null;
      return new KeyBinding(s, mods);
    }

    /// <summary>Parse a whitespace-separated list of accelerators, skipping invalid ones.</summary>
    public static List<KeyBinding> ParseList(string accelerators)
    {
      var list = new List<KeyBinding>();
      if (string.IsNullOrWhiteSpace(accelerators)) return list;
      foreach (string part in accelerators.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
      {
        KeyBinding? b = Parse(part);
        if (b != null) list.Add(b);
      }
      return list;
    }

    /// <summary>
    /// Does a key press match this binding? <paramref name="keyval"/> and
    /// <paramref name="boundKeyval"/> are compared case-insensitively (the
    /// caller resolves the key name and lower-cases both), and only the
    /// modifiers listed in <paramref name="relevant"/> take part in the comparison.
    /// </summary>
    public bool Matches(uint keyval, uint boundKeyval, Mods pressed, Mods relevant = Mods.Shift | Mods.Control | Mods.Alt | Mods.Super)
    {
      if (boundKeyval == 0 || keyval != boundKeyval) return false;
      return (pressed & relevant) == (Modifiers & relevant);
    }

    public override string ToString()
    {
      string s = "";
      if ((Modifiers & Mods.Shift) != 0) s += "<Shift>";
      if ((Modifiers & Mods.Control) != 0) s += "<Control>";
      if ((Modifiers & Mods.Alt) != 0) s += "<Alt>";
      if ((Modifiers & Mods.Super) != 0) s += "<Super>";
      if ((Modifiers & Mods.Meta) != 0) s += "<Meta>";
      if ((Modifiers & Mods.Hyper) != 0) s += "<Hyper>";
      return s + KeyName;
    }
  }
}
