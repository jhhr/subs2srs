using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace subs2srs.Cli
{
  /// <summary>
  /// Rows of cells in columns padded to their widest cell, for reading in a terminal (not a
  /// machine format). Wide East Asian characters count as two columns, so Japanese file names
  /// do not push the next column out of line.
  /// </summary>
  internal sealed class TextTable
  {
    private readonly List<string[]> rows = new();

    public TextTable(params string[] header) { rows.Add(header); }

    public void Add(params string[] cells) => rows.Add(cells);

    /// <summary>The header and every row, cells two spaces apart, no trailing spaces.</summary>
    public IEnumerable<string> Lines()
    {
      int columns = rows.Max(r => r.Length);
      var widths = new int[columns];
      foreach (string[] row in rows)
        for (int c = 0; c < row.Length; c++)
          widths[c] = Math.Max(widths[c], DisplayWidth(row[c]));
      foreach (string[] row in rows)
      {
        var sb = new StringBuilder();
        for (int c = 0; c < row.Length; c++)
        {
          if (c > 0) sb.Append("  ");
          sb.Append(row[c]);
          if (c < row.Length - 1) sb.Append(' ', widths[c] - DisplayWidth(row[c]));
        }
        yield return sb.ToString().TrimEnd();
      }
    }

    public override string ToString() => string.Join(Environment.NewLine, Lines());

    /// <summary>Terminal columns taken by <paramref name="text"/>: two for each wide East Asian character.</summary>
    public static int DisplayWidth(string text)
    {
      int width = 0;
      foreach (Rune r in text.EnumerateRunes())
        width += IsWide(r.Value) ? 2 : 1;
      return width;
    }

    // The wide blocks of Unicode's East Asian Width (W and F), coarsely.
    private static bool IsWide(int c) =>
      (c >= 0x1100 && c <= 0x115F)       // Hangul Jamo
      || (c >= 0x2E80 && c <= 0x303E)    // CJK radicals, symbols and punctuation
      || (c >= 0x3041 && c <= 0x33FF)    // kana, CJK compatibility
      || (c >= 0x3400 && c <= 0x4DBF)    // CJK extension A
      || (c >= 0x4E00 && c <= 0x9FFF)    // CJK ideographs
      || (c >= 0xA000 && c <= 0xA4CF)    // Yi
      || (c >= 0xAC00 && c <= 0xD7A3)    // Hangul syllables
      || (c >= 0xF900 && c <= 0xFAFF)    // CJK compatibility ideographs
      || (c >= 0xFE30 && c <= 0xFE4F)    // CJK compatibility forms
      || (c >= 0xFF00 && c <= 0xFF60)    // full-width forms
      || (c >= 0xFFE0 && c <= 0xFFE6)
      || (c >= 0x20000 && c <= 0x3FFFD); // CJK extensions B and later
  }
}
