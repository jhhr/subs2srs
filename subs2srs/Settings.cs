//  Copyright (C) 2009-2016 Christopher Brochtrup
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
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace subs2srs
{
  public static class PrefDefaults
  {
    public const int MaxParallelTasks = 0; // 0 = auto (Environment.ProcessorCount)
    public const string ToolsDir = ""; // "" = search PATH only
    public const int MainWindowWidth = 614;
    public const int MainWindowHeight = 630;
    public const bool DefaultEnableAudioClipGeneration = true;
    public const bool DefaultEnableSnapshotsGeneration = true;
    public const bool DefaultEnableVideoClipsGeneration = false;
    public const string VideoPlayer = "";
    public const string VideoPlayerArgs = "";
    public const bool ReencodeBeforeSplittingAudio = false;
    public const bool EnableLogging = true;
    public const string AudioNormalizeArgs = "/f /q /r /k";
    public const int LongClipWarningSeconds = 10;
    public const int DefaultAudioClipBitrate = 128;
    public const string DefaultAudioFormat = "Opus";
    public static readonly string[] AudioFormats = { "Opus", "MP3" };
    public const bool DefaultAudioNormalize = false;
    public const int DefaultVideoClipVideoBitrate = 800;
    public const int DefaultVideoClipAudioBitrate = 128;
    public const int DefaultSnapshotJpegQuality = 3;
    public const bool DefaultIphoneSupport = false;
    public const string DefaultEncodingSubs1 = "utf-8";
    public const string DefaultEncodingSubs2 = "utf-8";
    public const int DefaultContextNumLeading = 0;
    public const int DefaultContextNumTrailing = 0;
    public const int DefaultContextLeadingRange = 15;
    public const int DefaultContextTrailingRange = 15;
    public const string DefaultFileBrowserStartDir = "";
    public const string DefaultOutputDir = "";
    public const bool DefaultRemoveStyledLinesSubs1 = true;
    public const bool DefaultRemoveStyledLinesSubs2 = true;
    public const bool DefaultRemoveNoCounterpartSubs1 = true;
    public const bool DefaultRemoveNoCounterpartSubs2 = true;
    public const string DefaultIncludeTextSubs1 = "";
    public const string DefaultIncludeTextSubs2 = "";
    public const string DefaultExcludeTextSubs1 = "";
    public const string DefaultExcludeTextSubs2 = "";
    public const bool DefaultExcludeDuplicateLinesSubs1 = false;
    public const bool DefaultExcludeDuplicateLinesSubs2 = false;
    public const bool DefaultExcludeLinesFewerThanCharsSubs1 = false;
    public const bool DefaultExcludeLinesFewerThanCharsSubs2 = false;
    public const int DefaultExcludeLinesFewerThanCharsNumSubs1 = 8;
    public const int DefaultExcludeLinesFewerThanCharsNumSubs2 = 8;
    public const bool DefaultExcludeLinesShorterThanMsSubs1 = false;
    public const bool DefaultExcludeLinesShorterThanMsSubs2 = false;
    public const int DefaultExcludeLinesShorterThanMsNumSubs1 = 800;
    public const int DefaultExcludeLinesShorterThanMsNumSubs2 = 800;
    public const bool DefaultExcludeLinesLongerThanMsSubs1 = false;
    public const bool DefaultExcludeLinesLongerThanMsSubs2 = false;
    public const int DefaultExcludeLinesLongerThanMsNumSubs1 = 5000;
    public const int DefaultExcludeLinesLongerThanMsNumSubs2 = 5000;
    public const bool DefaultJoinSentencesSubs1 = true;
    public const bool DefaultJoinSentencesSubs2 = true;
    public const string DefaultJoinSentencesCharListSubs1 = ",、→";
    public const string DefaultJoinSentencesCharListSubs2 = ",、→";
    public const string SrsFilenameFormat = "${deck_name}.tsv";
    public const string SrsDelimiter = "\t";
    public const string SrsTagFormat = "${deck_name}_${0:episode_num}";
    public const string SrsSequenceMarkerFormat = "${0:episode_num}_${0:sequence_num}_${0:s_total_hour}.${0:s_min}.${0:s_sec}.${0:s_msec}";
    public const string SrsAudioFilenamePrefix = "[sound:";
    public const string SrsAudioFilenameSuffix = "]";
    public const string SrsSnapshotFilenamePrefix = "<img src=\"";
    public const string SrsSnapshotFilenameSuffix = "\">";
    public const string SrsVideoFilenamePrefix = "[sound:";
    public const string SrsVideoFilenameSuffix = "]";
    public const string SrsSubs1Format = "${subs1_line}";
    public const string SrsSubs2Format = "${subs2_line}";
    public const string SrsVobsubFilenamePrefix = "<img src=\"";
    public const string SrsVobsubFilenameFormat = "${deck_name}_${0:episode_num}_Stream_${0:stream_num}_${0:s_total_hour}.${0:s_min}.${0:s_sec}.${0:s_msec}-${0:e_total_hour}.${0:e_min}.${0:e_sec}.${0:e_msec}.png";
    public const string SrsVobsubFilenameSuffix = "\">";
    public const string AudioFilenameFormat = "${deck_name}_${0:episode_num}_${0:s_total_hour}.${0:s_min}.${0:s_sec}.${0:s_msec}-${0:e_total_hour}.${0:e_min}.${0:e_sec}.${0:e_msec}.mp3";
    public const string SnapshotFilenameFormat = "${deck_name}_${0:episode_num}_${0:m_total_hour}.${0:m_min}.${0:m_sec}.${0:m_msec}.jpg";
    public const string VideoFilenameFormat = "${deck_name}_${0:episode_num}_${0:s_total_hour}.${0:s_min}.${0:s_sec}.${0:s_msec}-${0:e_total_hour}.${0:e_min}.${0:e_sec}.${0:e_msec}";
    public const string AnimatedSnapshotFilenameFormat = "${deck_name}_${0:episode_num}_${0:s_total_hour}.${0:s_min}.${0:s_sec}.${0:s_msec}-${0:e_total_hour}.${0:e_min}.${0:e_sec}.${0:e_msec}"; // extension follows the format
    public const string VobsubFilenameFormat = "${deck_name}_${0:episode_num}_Stream_${0:stream_num}_${0:s_total_hour}.${0:s_min}.${0:s_sec}.${0:s_msec}-${0:e_total_hour}.${0:e_min}.${0:e_sec}.${0:e_msec}.png";
    public const string AudioId3Artist = "${deck_name}";
    public const string AudioId3Album = "${deck_name}_${0:episode_num}";
    public const string AudioId3Title = "${deck_name}_${0:episode_num}_${0:s_total_hour}.${0:s_min}.${0:s_sec}.${0:s_msec}-${0:e_total_hour}.${0:e_min}.${0:e_sec}.${0:e_msec}";
    public const string AudioId3Genre = "Speech";
    public const string AudioId3Lyrics = "${subs1_line} ${subs2_line}";
    public const string ExtractMediaAudioFilenameFormat = "${deck_name}_${0:episode_num}_${0:s_total_hour}.${0:s_min}.${0:s_sec}.${0:s_msec}-${0:e_total_hour}.${0:e_min}.${0:e_sec}.${0:e_msec}.mp3";
    public const string ExtractMediaLyricsSubs1Format = "[${2:s_total_min}:${2:s_sec}.${2:s_hsec}] ${subs1_line}";
    public const string ExtractMediaLyricsSubs2Format = "[${2:s_total_min}:${2:s_sec}.${2:s_hsec}] ${subs2_line}";
    public const string DuelingSubtitleFilenameFormat = "${deck_name}_${0:episode_num}.ass";
    public const string DuelingQuickRefFilenameFormat = "${deck_name}_${0:episode_num}.txt";
    public const string DuelingQuickRefSubs1Format = "[${0:s_total_hour}:${0:s_min}:${0:s_sec}.${0:s_hsec}]  ${subs1_line}";
    public const string DuelingQuickRefSubs2Format = "[${0:s_total_hour}:${0:s_min}:${0:s_sec}.${0:s_hsec}]  ${subs2_line}\n";

    // ── Snippets ──
    public const string ValidationDir = ""; // "" = <OutputDir>/validation
    // Key bindings are GTK accelerator names, several separated by spaces.
    public const string GroupingKeyAttachAbove = "<Control>Up w";
    public const string GroupingKeyAttachBelow = "<Control>Down s";
    public const string GroupingKeyDetach = "<Control>BackSpace BackSpace x";

    /// <summary>The detach default before plain BackSpace was added; saved preferences holding it are upgraded.</summary>
    public const string OldGroupingKeyDetach = "<Control>BackSpace x";

    // ── AI grouping ──
    // API keys live in preferences.json; the environment variables ANTHROPIC_API_KEY,
    // OPENAI_API_KEY and GEMINI_API_KEY override them. Keys are never logged.
    public const string AnthropicApiKey = "";
    public const string OpenAiApiKey = "";
    public const string GeminiApiKey = "";
    // Pacing is response-driven (rate-limit answers install a per-model cooldown); these only bound it.
    public const int AiMaxConcurrentRequests = 0; // 0 = auto (AiBulkRunner.AutoConcurrency)
    public const int AiMaxRetries = 5;
    public const int AiMaxRetryWaitSeconds = 120;
    public const int AiRequestTimeoutSeconds = 600;

    /// <summary>The request timeout default before streamed answers; saved preferences holding it are upgraded.</summary>
    public const int OldAiRequestTimeoutSeconds = 120;
    public const string AiCacheDir = ""; // "" = <LocalApplicationData>/subs2srs/ai-cache
    public const bool AiGroupingOnGo = false;
    // A "terminal-claude-..." model runs through the claude command line on the user's Claude
    // subscription instead of the Anthropic API; a process costs far more than a connection, so it
    // has its own, much smaller cap. Empty path = find "claude" on PATH.
    public const string ClaudeCliPath = "";
    public const int ClaudeCliMaxConcurrentProcesses = 4;
  }


  // Procedure for creating a new preference that can be set in the Preferences dialog:
  // 1) Create a default for the setting in PrefDefaults (above).
  // 2) Add property to PreferencesData.cs with default from PrefDefaults.
  // 3) Add delegating property to ConstantSettings (below).
  // 4) Add to DialogPref.BuildPropTable + DialogPref.SavePreferences.
  // 5) Add to Logger.writeSettingsToLog.
  // 6) For GUI settings (ones that map to Settings.Instance), add to Settings.Reset().

  public static class ConstantSettings
  {
    // ── External tool lookup ───────────────────────────────────────────

    /// <summary>
    /// Candidate file names for a tool: the bare name, plus on Windows the
    /// name with each PATHEXT extension (ffmpeg → ffmpeg, ffmpeg.EXE, …).
    /// </summary>
    private static IEnumerable<string> ToolCandidateNames(string name)
    {
      yield return name;
      if (!OperatingSystem.IsWindows() || Path.HasExtension(name))
        yield break;
      string pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.COM;.BAT;.CMD";
      foreach (var ext in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        yield return name + ext;
    }

    private static string? FindToolInDir(string dir, string name)
    {
      if (string.IsNullOrWhiteSpace(dir)) return null;
      try
      {
        foreach (var candidate in ToolCandidateNames(name))
        {
          string full = Path.Combine(dir, candidate);
          if (File.Exists(full)) return full;
        }
      }
      catch (ArgumentException)
      {
        // Malformed PATH entry (illegal characters); ignore it.
      }
      return null;
    }

    /// <summary>
    /// Search PATH for a tool. Returns the full path, or null when not found.
    /// Honours PATHEXT on Windows and skips malformed PATH entries.
    /// </summary>
    internal static string? FindInPath(string name)
    {
      string path = Environment.GetEnvironmentVariable("PATH") ?? "";
      foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
      {
        string? found = FindToolInDir(dir.Trim().Trim('"'), name);
        if (found != null) return found;
      }
      return null;
    }

    /// <summary>
    /// Resolve an external tool: the Tools Directory preference first,
    /// then PATH. Returns null when the tool cannot be found.
    /// </summary>
    public static string? ResolveTool(string name)
    {
      return FindToolInDir(ToolsDir, name) ?? FindInPath(name);
    }

    /// <summary>
    /// Like <see cref="ResolveTool"/> but falls back to the bare name so
    /// callers can still hand it to Process.Start.
    /// </summary>
    public static string ResolveToolOrName(string name)
    {
      return ResolveTool(name) ?? name;
    }

    public static bool IsFFmpegAvailable => ResolveTool(ExeFFmpeg) != null;

    /// <summary>
    /// User-facing hint shown when ffmpeg cannot be found.
    /// </summary>
    public static string FFmpegMissingMessage
    {
      get
      {
        string hint = OperatingSystem.IsWindows()
          ? "Install it with:\n    winget install Gyan.FFmpeg\n"
            + "or download a build from https://ffmpeg.org/download.html and either add its bin folder to PATH "
            + "or set \"Tools Directory\" in Preferences."
          : OperatingSystem.IsMacOS()
            ? "Install it with Homebrew (brew install ffmpeg) or set \"Tools Directory\" in Preferences."
            : "Install it with your package manager (e.g. sudo apt install ffmpeg) or set \"Tools Directory\" in Preferences.";
        return "ffmpeg was not found. subs2srs needs ffmpeg to generate audio, snapshots and video clips.\n\n" + hint;
      }
    }

    // ── Immutable (no setter) ──────────────────────────────────────────

    public static string SaveExt { get; } = "s2s";
    public static string HelpPage { get; } = "http://subs2srs.sourceforge.net/";
    public static string LogDir { get; internal set; } = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "subs2srs", "Logs") + Path.DirectorySeparatorChar;
    public static int MaxLogFiles { get; } = 10;

    public static string ExeFFmpeg { get; } = "ffmpeg";
    public static string PathFFmpegFullExe => ResolveToolOrName(ExeFFmpeg);
    public static string PathFFmpegExe => ResolveToolOrName(ExeFFmpeg);
    public static string PathFFmpegPresetsFull { get; } = Path.Combine(
      UtilsCommon.getAppDir(true), "presets");

    public static string TempImageFilename { get; } = $"subs2srs_temp_{Guid.NewGuid()}.jpg";
    public static string TempVideoFilename { get; } = $"subs2srs_temp_{Guid.NewGuid()}";
    public static string TempAudioFilename { get; set; } = $"subs2srs_temp_{Guid.NewGuid()}.{PrefDefaults.DefaultAudioFormat.ToLower()}";

    public static string AudioFilenameFormatWithExt { get; private set; } = PrefDefaults.AudioFilenameFormat;
    public static string ExtractMediaAudioFilenameFormatWithExt { get; private set; } = PrefDefaults.ExtractMediaAudioFilenameFormat;

    public static void UpdateAudioFilenameFormats()
    {
        string ext = Settings.Instance.AudioClips.AudioFormat?.ToLower() ?? "mp3";
        TempAudioFilename = $"subs2srs_temp_{Guid.NewGuid()}.{ext}";
        AudioFilenameFormatWithExt = PrefDefaults.AudioFilenameFormat.Replace(".mp3", $".{ext}");
        ExtractMediaAudioFilenameFormatWithExt = PrefDefaults.ExtractMediaAudioFilenameFormat.Replace(".mp3", $".{ext}");
    }

    public static string TempAudioPreviewFilename { get; } = $"subs2srs_temp_{Guid.NewGuid()}.wav";
    public static string TempPreviewDirName { get; } = $"subs2srs_preview_{Guid.NewGuid()}";
    public static string TempMkvExtractSubs1Filename { get; } = $"subs2srs_mkv_extract_subs1_{Guid.NewGuid()}";
    public static string TempMkvExtractSubs2Filename { get; } = $"subs2srs_mkv_extract_subs2_{Guid.NewGuid()}";

    public static string NormalizeAudioExe { get; } = "mp3gain";
    public static string PathNormalizeAudioExeRel { get; } = "mp3gain";
    public static string PathNormalizeAudioExeFull => ResolveToolOrName(NormalizeAudioExe);
    public static string PathSubsReTimerFull => ResolveToolOrName("SubsReTimer");

    public static string ExeMkvInfo { get; } = "mkvinfo";
    public static string PathMkvDirRel { get; } = "";
    public static string PathMkvDirFull { get; } = "";
    public static string PathMkvInfoExeRel { get; } = "mkvinfo";
    public static string PathMkvInfoExeFull => ResolveToolOrName(ExeMkvInfo);

    public static string ExeMkvExtract { get; } = "mkvextract";
    public static string PathMkvExtractExeRel { get; } = "mkvextract";
    public static string PathMkvExtractExeFull => ResolveToolOrName(ExeMkvExtract);

    public static string SettingsFilename { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "subs2srs", "preferences.txt");

    // ── Backing store for preferences (populated by PrefIO.read) ─────
    internal static PreferencesData Prefs { get; set; } = new();

    // ── Mutable — delegate to Prefs ──────────────────────────────────

    public static int MaxParallelTasks
    {
        get => Prefs.MaxParallelTasks;
        set => Prefs.MaxParallelTasks = value;
    }

    /// <summary>
    /// Optional directory searched before PATH for ffmpeg, ffprobe, ffplay,
    /// mkvinfo, mkvextract and mp3gain. Empty = PATH only.
    /// </summary>
    public static string ToolsDir
    {
        get => Prefs.ToolsDir ?? "";
        set => Prefs.ToolsDir = value ?? "";
    }

    public static int EffectiveParallelism => MaxParallelTasks > 0
        ? MaxParallelTasks
        : Environment.ProcessorCount;

    public static int MainWindowWidth
    {
        get => Prefs.MainWindowWidth;
        set => Prefs.MainWindowWidth = value;
    }

    public static int MainWindowHeight
    {
        get => Prefs.MainWindowHeight;
        set => Prefs.MainWindowHeight = value;
    }

    public static bool DefaultEnableAudioClipGeneration
    {
        get => Prefs.DefaultEnableAudioClipGeneration;
        set => Prefs.DefaultEnableAudioClipGeneration = value;
    }

    public static bool DefaultEnableSnapshotsGeneration
    {
        get => Prefs.DefaultEnableSnapshotsGeneration;
        set => Prefs.DefaultEnableSnapshotsGeneration = value;
    }

    public static bool DefaultEnableVideoClipsGeneration
    {
        get => Prefs.DefaultEnableVideoClipsGeneration;
        set => Prefs.DefaultEnableVideoClipsGeneration = value;
    }

    public static string VideoPlayer
    {
        get => Prefs.VideoPlayer;
        set => Prefs.VideoPlayer = value;
    }

    public static string VideoPlayerArgs
    {
        get => Prefs.VideoPlayerArgs;
        set => Prefs.VideoPlayerArgs = value;
    }

    public static bool ReencodeBeforeSplittingAudio
    {
        get => Prefs.ReencodeBeforeSplittingAudio;
        set => Prefs.ReencodeBeforeSplittingAudio = value;
    }

    public static bool EnableLogging
    {
        get => Prefs.EnableLogging;
        set => Prefs.EnableLogging = value;
    }

    public static string AudioNormalizeArgs
    {
        get => Prefs.AudioNormalizeArgs;
        set => Prefs.AudioNormalizeArgs = value;
    }

    public static int LongClipWarningSeconds
    {
        get => Prefs.LongClipWarningSeconds;
        set => Prefs.LongClipWarningSeconds = value;
    }

    public static int DefaultAudioClipBitrate
    {
        get => Prefs.DefaultAudioClipBitrate;
        set => Prefs.DefaultAudioClipBitrate = value;
    }

    public static string AudioFormat
    {
        get => Prefs.AudioFormat;
        set { Prefs.AudioFormat = value; UpdateAudioFilenameFormats(); }
    }

    public static bool DefaultAudioNormalize
    {
        get => Prefs.DefaultAudioNormalize;
        set => Prefs.DefaultAudioNormalize = value;
    }

    public static int DefaultVideoClipVideoBitrate
    {
        get => Prefs.DefaultVideoClipVideoBitrate;
        set => Prefs.DefaultVideoClipVideoBitrate = value;
    }

    public static int DefaultVideoClipAudioBitrate
    {
        get => Prefs.DefaultVideoClipAudioBitrate;
        set => Prefs.DefaultVideoClipAudioBitrate = value;
    }

    public static int DefaultSnapshotJpegQuality
    {
        get => Prefs.DefaultSnapshotJpegQuality;
        set => Prefs.DefaultSnapshotJpegQuality = value;
    }

    public static bool DefaultIphoneSupport
    {
        get => Prefs.DefaultIphoneSupport;
        set => Prefs.DefaultIphoneSupport = value;
    }

    public static string DefaultEncodingSubs1
    {
        get => Prefs.DefaultEncodingSubs1;
        set => Prefs.DefaultEncodingSubs1 = value;
    }

    public static string DefaultEncodingSubs2
    {
        get => Prefs.DefaultEncodingSubs2;
        set => Prefs.DefaultEncodingSubs2 = value;
    }

    public static int DefaultContextNumLeading
    {
        get => Prefs.DefaultContextNumLeading;
        set => Prefs.DefaultContextNumLeading = value;
    }

    public static int DefaultContextNumTrailing
    {
        get => Prefs.DefaultContextNumTrailing;
        set => Prefs.DefaultContextNumTrailing = value;
    }

    public static int DefaultContextLeadingRange
    {
        get => Prefs.DefaultContextLeadingRange;
        set => Prefs.DefaultContextLeadingRange = value;
    }

    public static int DefaultContextTrailingRange
    {
        get => Prefs.DefaultContextTrailingRange;
        set => Prefs.DefaultContextTrailingRange = value;
    }

    public static string DefaultFileBrowserStartDir
    {
        get => Prefs.DefaultFileBrowserStartDir;
        set => Prefs.DefaultFileBrowserStartDir = value;
    }

    public static string DefaultOutputDir
    {
        get => Prefs.DefaultOutputDir;
        set => Prefs.DefaultOutputDir = value;
    }

    public static bool DefaultRemoveStyledLinesSubs1
    {
        get => Prefs.DefaultRemoveStyledLinesSubs1;
        set => Prefs.DefaultRemoveStyledLinesSubs1 = value;
    }

    public static bool DefaultRemoveStyledLinesSubs2
    {
        get => Prefs.DefaultRemoveStyledLinesSubs2;
        set => Prefs.DefaultRemoveStyledLinesSubs2 = value;
    }

    public static bool DefaultRemoveNoCounterpartSubs1
    {
        get => Prefs.DefaultRemoveNoCounterpartSubs1;
        set => Prefs.DefaultRemoveNoCounterpartSubs1 = value;
    }

    public static bool DefaultRemoveNoCounterpartSubs2
    {
        get => Prefs.DefaultRemoveNoCounterpartSubs2;
        set => Prefs.DefaultRemoveNoCounterpartSubs2 = value;
    }

    public static string DefaultIncludeTextSubs1
    {
        get => Prefs.DefaultIncludeTextSubs1;
        set => Prefs.DefaultIncludeTextSubs1 = value;
    }

    public static string DefaultIncludeTextSubs2
    {
        get => Prefs.DefaultIncludeTextSubs2;
        set => Prefs.DefaultIncludeTextSubs2 = value;
    }

    public static string DefaultExcludeTextSubs1
    {
        get => Prefs.DefaultExcludeTextSubs1;
        set => Prefs.DefaultExcludeTextSubs1 = value;
    }

    public static string DefaultExcludeTextSubs2
    {
        get => Prefs.DefaultExcludeTextSubs2;
        set => Prefs.DefaultExcludeTextSubs2 = value;
    }

    public static bool DefaultExcludeDuplicateLinesSubs1
    {
        get => Prefs.DefaultExcludeDuplicateLinesSubs1;
        set => Prefs.DefaultExcludeDuplicateLinesSubs1 = value;
    }

    public static bool DefaultExcludeDuplicateLinesSubs2
    {
        get => Prefs.DefaultExcludeDuplicateLinesSubs2;
        set => Prefs.DefaultExcludeDuplicateLinesSubs2 = value;
    }

    public static bool DefaultExcludeLinesFewerThanCharsSubs1
    {
        get => Prefs.DefaultExcludeLinesFewerThanCharsSubs1;
        set => Prefs.DefaultExcludeLinesFewerThanCharsSubs1 = value;
    }

    public static bool DefaultExcludeLinesFewerThanCharsSubs2
    {
        get => Prefs.DefaultExcludeLinesFewerThanCharsSubs2;
        set => Prefs.DefaultExcludeLinesFewerThanCharsSubs2 = value;
    }

    public static int DefaultExcludeLinesFewerThanCharsNumSubs1
    {
        get => Prefs.DefaultExcludeLinesFewerThanCharsNumSubs1;
        set => Prefs.DefaultExcludeLinesFewerThanCharsNumSubs1 = value;
    }

    public static int DefaultExcludeLinesFewerThanCharsNumSubs2
    {
        get => Prefs.DefaultExcludeLinesFewerThanCharsNumSubs2;
        set => Prefs.DefaultExcludeLinesFewerThanCharsNumSubs2 = value;
    }

    public static bool DefaultExcludeLinesShorterThanMsSubs1
    {
        get => Prefs.DefaultExcludeLinesShorterThanMsSubs1;
        set => Prefs.DefaultExcludeLinesShorterThanMsSubs1 = value;
    }

    public static bool DefaultExcludeLinesShorterThanMsSubs2
    {
        get => Prefs.DefaultExcludeLinesShorterThanMsSubs2;
        set => Prefs.DefaultExcludeLinesShorterThanMsSubs2 = value;
    }

    public static int DefaultExcludeLinesShorterThanMsNumSubs1
    {
        get => Prefs.DefaultExcludeLinesShorterThanMsNumSubs1;
        set => Prefs.DefaultExcludeLinesShorterThanMsNumSubs1 = value;
    }

    public static int DefaultExcludeLinesShorterThanMsNumSubs2
    {
        get => Prefs.DefaultExcludeLinesShorterThanMsNumSubs2;
        set => Prefs.DefaultExcludeLinesShorterThanMsNumSubs2 = value;
    }

    public static bool DefaultExcludeLinesLongerThanMsSubs1
    {
        get => Prefs.DefaultExcludeLinesLongerThanMsSubs1;
        set => Prefs.DefaultExcludeLinesLongerThanMsSubs1 = value;
    }

    public static bool DefaultExcludeLinesLongerThanMsSubs2
    {
        get => Prefs.DefaultExcludeLinesLongerThanMsSubs2;
        set => Prefs.DefaultExcludeLinesLongerThanMsSubs2 = value;
    }

    public static int DefaultExcludeLinesLongerThanMsNumSubs1
    {
        get => Prefs.DefaultExcludeLinesLongerThanMsNumSubs1;
        set => Prefs.DefaultExcludeLinesLongerThanMsNumSubs1 = value;
    }

    public static int DefaultExcludeLinesLongerThanMsNumSubs2
    {
        get => Prefs.DefaultExcludeLinesLongerThanMsNumSubs2;
        set => Prefs.DefaultExcludeLinesLongerThanMsNumSubs2 = value;
    }

    public static bool DefaultJoinSentencesSubs1
    {
        get => Prefs.DefaultJoinSentencesSubs1;
        set => Prefs.DefaultJoinSentencesSubs1 = value;
    }

    public static bool DefaultJoinSentencesSubs2
    {
        get => Prefs.DefaultJoinSentencesSubs2;
        set => Prefs.DefaultJoinSentencesSubs2 = value;
    }

    public static string DefaultJoinSentencesCharListSubs1
    {
        get => Prefs.DefaultJoinSentencesCharListSubs1;
        set => Prefs.DefaultJoinSentencesCharListSubs1 = value;
    }

    public static string DefaultJoinSentencesCharListSubs2
    {
        get => Prefs.DefaultJoinSentencesCharListSubs2;
        set => Prefs.DefaultJoinSentencesCharListSubs2 = value;
    }

    public static string SrsFilenameFormat
    {
        get => Prefs.SrsFilenameFormat;
        set => Prefs.SrsFilenameFormat = value;
    }

    public static string SrsDelimiter
    {
        get => Prefs.SrsDelimiter;
        set => Prefs.SrsDelimiter = value;
    }

    public static string SrsTagFormat
    {
        get => Prefs.SrsTagFormat;
        set => Prefs.SrsTagFormat = value;
    }

    public static string SrsSequenceMarkerFormat
    {
        get => Prefs.SrsSequenceMarkerFormat;
        set => Prefs.SrsSequenceMarkerFormat = value;
    }

    public static string SrsAudioFilenamePrefix
    {
        get => Prefs.SrsAudioFilenamePrefix;
        set => Prefs.SrsAudioFilenamePrefix = value;
    }

    public static string SrsAudioFilenameSuffix
    {
        get => Prefs.SrsAudioFilenameSuffix;
        set => Prefs.SrsAudioFilenameSuffix = value;
    }

    public static string AudioFilenameFormat
    {
        get => Prefs.AudioFilenameFormat;
        set => Prefs.AudioFilenameFormat = value;
    }

    public static string AudioId3Artist
    {
        get => Prefs.AudioId3Artist;
        set => Prefs.AudioId3Artist = value;
    }

    public static string AudioId3Album
    {
        get => Prefs.AudioId3Album;
        set => Prefs.AudioId3Album = value;
    }

    public static string AudioId3Title
    {
        get => Prefs.AudioId3Title;
        set => Prefs.AudioId3Title = value;
    }

    public static string AudioId3Genre
    {
        get => Prefs.AudioId3Genre;
        set => Prefs.AudioId3Genre = value;
    }

    public static string AudioId3Lyrics
    {
        get => Prefs.AudioId3Lyrics;
        set => Prefs.AudioId3Lyrics = value;
    }

    public static string SrsSnapshotFilenamePrefix
    {
        get => Prefs.SrsSnapshotFilenamePrefix;
        set => Prefs.SrsSnapshotFilenamePrefix = value;
    }

    public static string SrsSnapshotFilenameSuffix
    {
        get => Prefs.SrsSnapshotFilenameSuffix;
        set => Prefs.SrsSnapshotFilenameSuffix = value;
    }

    public static string SnapshotFilenameFormat
    {
        get => Prefs.SnapshotFilenameFormat;
        set => Prefs.SnapshotFilenameFormat = value;
    }

    public static string SrsVideoFilenamePrefix
    {
        get => Prefs.SrsVideoFilenamePrefix;
        set => Prefs.SrsVideoFilenamePrefix = value;
    }

    public static string SrsVideoFilenameSuffix
    {
        get => Prefs.SrsVideoFilenameSuffix;
        set => Prefs.SrsVideoFilenameSuffix = value;
    }

    public static string VideoFilenameFormat
    {
        get => Prefs.VideoFilenameFormat;
        set => Prefs.VideoFilenameFormat = value;
    }

    public static string AnimatedSnapshotFilenameFormat
    {
        get => Prefs.AnimatedSnapshotFilenameFormat;
        set => Prefs.AnimatedSnapshotFilenameFormat = value;
    }

    public static string SrsSubs1Format
    {
        get => Prefs.SrsSubs1Format;
        set => Prefs.SrsSubs1Format = value;
    }

    public static string SrsSubs2Format
    {
        get => Prefs.SrsSubs2Format;
        set => Prefs.SrsSubs2Format = value;
    }

    public static string SrsVobsubFilenamePrefix
    {
        get => Prefs.SrsVobsubFilenamePrefix;
        set => Prefs.SrsVobsubFilenamePrefix = value;
    }

    public static string SrsVobsubFilenameSuffix
    {
        get => Prefs.SrsVobsubFilenameSuffix;
        set => Prefs.SrsVobsubFilenameSuffix = value;
    }

    public static string VobsubFilenameFormat
    {
        get => Prefs.VobsubFilenameFormat;
        set => Prefs.VobsubFilenameFormat = value;
    }

    public static string ExtractMediaAudioFilenameFormat
    {
        get => Prefs.ExtractMediaAudioFilenameFormat;
        set => Prefs.ExtractMediaAudioFilenameFormat = value;
    }

    public static string ExtractMediaLyricsSubs1Format
    {
        get => Prefs.ExtractMediaLyricsSubs1Format;
        set => Prefs.ExtractMediaLyricsSubs1Format = value;
    }

    public static string ExtractMediaLyricsSubs2Format
    {
        get => Prefs.ExtractMediaLyricsSubs2Format;
        set => Prefs.ExtractMediaLyricsSubs2Format = value;
    }

    public static string DuelingSubtitleFilenameFormat
    {
        get => Prefs.DuelingSubtitleFilenameFormat;
        set => Prefs.DuelingSubtitleFilenameFormat = value;
    }

    public static string DuelingQuickRefFilenameFormat
    {
        get => Prefs.DuelingQuickRefFilenameFormat;
        set => Prefs.DuelingQuickRefFilenameFormat = value;
    }

    public static string DuelingQuickRefSubs1Format
    {
        get => Prefs.DuelingQuickRefSubs1Format;
        set => Prefs.DuelingQuickRefSubs1Format = value;
    }

    public static string DuelingQuickRefSubs2Format
    {
        get => Prefs.DuelingQuickRefSubs2Format;
        set => Prefs.DuelingQuickRefSubs2Format = value;
    }

    /// <summary>Where "Save as validation" writes grouping files. Empty = &lt;OutputDir&gt;/validation.</summary>
    public static string ValidationDir
    {
        get => Prefs.ValidationDir ?? "";
        set => Prefs.ValidationDir = value ?? "";
    }

    public static string GroupingKeyAttachAbove
    {
        get => Prefs.GroupingKeyAttachAbove ?? "";
        set => Prefs.GroupingKeyAttachAbove = value ?? "";
    }

    public static string GroupingKeyAttachBelow
    {
        get => Prefs.GroupingKeyAttachBelow ?? "";
        set => Prefs.GroupingKeyAttachBelow = value ?? "";
    }

    public static string GroupingKeyDetach
    {
        get => Prefs.GroupingKeyDetach ?? "";
        set => Prefs.GroupingKeyDetach = value ?? "";
    }

    // ── AI grouping (keys are secrets: never log them) ──

    public static string AnthropicApiKey
    {
        get => Prefs.AnthropicApiKey ?? "";
        set => Prefs.AnthropicApiKey = value ?? "";
    }

    public static string OpenAiApiKey
    {
        get => Prefs.OpenAiApiKey ?? "";
        set => Prefs.OpenAiApiKey = value ?? "";
    }

    public static string GeminiApiKey
    {
        get => Prefs.GeminiApiKey ?? "";
        set => Prefs.GeminiApiKey = value ?? "";
    }

    /// <summary>Requests in flight at once for a bulk AI run; 0 = auto.</summary>
    public static int AiMaxConcurrentRequests
    {
        get => Prefs.AiMaxConcurrentRequests;
        set => Prefs.AiMaxConcurrentRequests = value;
    }

    /// <summary>Retries after the first send of a request.</summary>
    public static int AiMaxRetries
    {
        get => Prefs.AiMaxRetries;
        set => Prefs.AiMaxRetries = value;
    }

    /// <summary>A provider wait hint above this gives up instead of blocking the run.</summary>
    public static int AiMaxRetryWaitSeconds
    {
        get => Prefs.AiMaxRetryWaitSeconds;
        set => Prefs.AiMaxRetryWaitSeconds = value;
    }

    public static int AiRequestTimeoutSeconds
    {
        get => Prefs.AiRequestTimeoutSeconds;
        set => Prefs.AiRequestTimeoutSeconds = value;
    }

    /// <summary>Where AI grouping answers are cached. Empty = &lt;LocalApplicationData&gt;/subs2srs/ai-cache.</summary>
    public static string AiCacheDir
    {
        get => Prefs.AiCacheDir ?? "";
        set => Prefs.AiCacheDir = value ?? "";
    }

    /// <summary>Resolved cache directory.</summary>
    public static string AiCacheDirFull => string.IsNullOrWhiteSpace(AiCacheDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "subs2srs", "ai-cache")
        : AiCacheDir;

    /// <summary>When true, Go runs the AI grouping pass itself if the preview has not produced a grouping.</summary>
    public static bool AiGroupingOnGo
    {
        get => Prefs.AiGroupingOnGo;
        set => Prefs.AiGroupingOnGo = value;
    }

    /// <summary>The claude executable for "terminal-" models. Empty = find "claude" on PATH.</summary>
    public static string ClaudeCliPath
    {
        get => Prefs.ClaudeCliPath ?? "";
        set => Prefs.ClaudeCliPath = value ?? "";
    }

    /// <summary>How many claude processes may run at once; each costs seconds of startup and a few hundred MB.</summary>
    public static int ClaudeCliMaxConcurrentProcesses
    {
        get => Prefs.ClaudeCliMaxConcurrentProcesses;
        set => Prefs.ClaudeCliMaxConcurrentProcesses = value;
    }
  }

  /// <summary>
  /// Cascading time shift rule: from a given episode number onward,
  /// apply the specified shift in milliseconds.
  /// Rules are sorted by FromEpisode; lookup finds the last rule where FromEpisode ≤ episode.
  /// </summary>
  public class TimeShiftRule
  {
    public int FromEpisode { get; set; }
    public int ShiftMs { get; set; }

    public TimeShiftRule() { }
    public TimeShiftRule(int from, int shift) { FromEpisode = from; ShiftMs = shift; }
  }

  public class SubSettings
  {
    public string FilePattern { get; set; } = "";

    [JsonIgnore]
    public string[] Files { get; set; } = Array.Empty<string>();

    public InfoStream VobsubStream { get; set; }
    public bool TimingsEnabled { get; set; }
    public int TimeShift { get; set; }
    public string[] IncludedWords { get; set; } = Array.Empty<string>();
    public string[] ExcludedWords { get; set; } = Array.Empty<string>();
    public bool RemoveNoCounterpart { get; set; } = true;
    public bool RemoveStyledLines { get; set; } = true;
    public bool ExcludeDuplicateLinesEnabled { get; set; }
    public bool ExcludeFewerEnabled { get; set; }
    public int ExcludeFewerCount { get; set; } = 8;
    public bool ExcludeShorterThanTimeEnabled { get; set; }
    public int ExcludeShorterThanTime { get; set; } = 800;
    public bool ExcludeLongerThanTimeEnabled { get; set; }
    public int ExcludeLongerThanTime { get; set; } = 5000;
    public bool JoinSentencesEnabled { get; set; } = true;
    public string JoinSentencesCharList { get; set; } = ",、→";
    public bool ActorsEnabled { get; set; }
    public string Encoding { get; set; } = "utf-8";
    /// <summary>
    /// Per-episode time shift overrides (cascading rules).
    /// When non-empty, overrides the global TimeShift for matched episodes.
    /// Sorted by FromEpisode ascending.
    /// </summary>
    public List<TimeShiftRule> TimeShiftRules { get; set; } = new();

    /// <summary>
    /// Get the effective time shift for a given episode number.
    /// Cascading: last rule where FromEpisode ≤ episode wins.
    /// Falls back to global TimeShift when no rules are defined.
    /// </summary>
    public int GetEffectiveTimeShift(int episodeNumber)
    {
      if (TimeShiftRules == null || TimeShiftRules.Count == 0)
        return TimeShift;

      int shift = TimeShift;
      foreach (var rule in TimeShiftRules)
      {
        if (rule.FromEpisode <= episodeNumber)
          shift = rule.ShiftMs;
        else
          break;
      }
      return shift;
    }
  }


  public class ImageSize
  {
    public int Width { get; set; }
    public int Height { get; set; }

    public ImageSize() { }

    public ImageSize(int width, int height)
    {
      Width = width;
      Height = height;
    }
  }


  public class ImageCrop
  {
    public int Top { get; set; }
    public int Bottom { get; set; }
    public int Left { get; set; }
    public int Right { get; set; }

    public ImageCrop() { }

    public ImageCrop(int top, int bottom, int left, int right)
    {
      Top = top;
      Bottom = bottom;
      Left = left;
      Right = right;
    }
  }


  public class VideoClips
  {
    public bool Enabled { get; set; }
    public string FilePattern { get; set; } = "";
    public InfoStream AudioStream { get; set; }

    [JsonIgnore]
    public string[] Files { get; set; } = Array.Empty<string>();

    public ImageSize Size { get; set; } = new ImageSize(240, 160);
    public int BitrateVideo { get; set; } = 700;
    public int BitrateAudio { get; set; } = 128;
    public bool PadEnabled { get; set; }
    public int PadStart { get; set; } = 250;
    public int PadEnd { get; set; } = 250;
    public ImageCrop Crop { get; set; } = new ImageCrop();
    public bool IPodSupport { get; set; }
  }


  public class AudioClips
  {
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("filePattern")]
    public string FilePattern { get; set; } = "";

    [JsonIgnore]
    public string[] Files { get; set; } = Array.Empty<string>();

    public string AudioFormat { get; set; } = PrefDefaults.DefaultAudioFormat;
    public bool PadEnabled { get; set; }
    public int PadStart { get; set; } = 250;
    public int PadEnd { get; set; } = 250;
    public int Bitrate { get; set; } = 128;
    public bool UseAudioFromVideo { get; set; } = true;
    public bool UseExistingAudio { get; set; }
    public bool Normalize { get; set; }
  }


  public class Snapshots
  {
    public bool Enabled { get; set; } = true;
    public ImageSize Size { get; set; } = new ImageSize(240, 160);
    public ImageCrop Crop { get; set; } = new ImageCrop();

    /// <summary>
    /// JPEG quality for ffmpeg -q:v flag (1=best, 31=worst).
    /// Ignored when output format is PNG.
    /// </summary>
    public int Quality { get; set; } = 3;
  }


  /// <summary>Container/codec of an animated snapshot.</summary>
  [JsonConverter(typeof(JsonStringEnumConverter))]
  public enum AnimatedSnapshotFormat
  {
    /// <summary>Animated WebP (libwebp). Rendered by every Anki client.</summary>
    Webp,
    /// <summary>Animated AVIF (AV1). Smaller, but needs a recent Anki.</summary>
    Avif
  }


  /// <summary>
  /// Short animated clip of the line, cut straight from the source video with no
  /// audio (what mpvacious calls an animated snapshot). Its own output type next
  /// to Snapshots; the TSV gets one more column after the snapshot.
  /// </summary>
  public class AnimatedSnapshots
  {
    public bool Enabled { get; set; }
    public AnimatedSnapshotFormat Format { get; set; } = AnimatedSnapshotFormat.Webp;

    /// <summary>Frames per second of the animation.</summary>
    public int Fps { get; set; } = 10;

    /// <summary>Output height in pixels; the width follows the aspect ratio. Never upscales.</summary>
    public int Height { get; set; } = 350;

    /// <summary>
    /// 0–100, higher is better and larger (libwebp's -quality scale; mapped onto
    /// the AV1 crf scale for avif).
    /// </summary>
    public int Quality { get; set; } = 20;

    /// <summary>Pixels cut off each edge of the source frame before scaling.</summary>
    public ImageCrop Crop { get; set; } = new ImageCrop();
  }


#if ENABLE_VOBSUB
  public class VobSubColors
  {
    public bool Enabled { get; set; }
    public System.Drawing.Color[] Colors { get; set; }
    public bool[] TransparencyEnabled { get; set; }

    public VobSubColors()
    {
      Colors = new System.Drawing.Color[4];
      Colors[0] = System.Drawing.Color.FromArgb(253, 253, 253);
      Colors[1] = System.Drawing.Color.FromArgb(189, 189, 189);
      Colors[2] = System.Drawing.Color.FromArgb(126, 126, 126);
      Colors[3] = System.Drawing.Color.FromArgb(29, 29, 29);
      TransparencyEnabled = new bool[4];
      TransparencyEnabled[0] = true;
    }
  }
#else
  public class VobSubColors
  {
    public bool Enabled { get; set; }
    public bool[] TransparencyEnabled { get; set; } = new bool[4];
  }
#endif


  public class LanguageSpecific
  {
    public bool KanjiLinesOnly { get; set; }
  }


  /// <summary>How cards are grouped from several subtitle lines.</summary>
  [JsonConverter(typeof(JsonStringEnumConverter))]
  public enum SnippetMode
  {
    /// <summary>One card per line (the classic behaviour).</summary>
    Off,
    /// <summary>Deterministic grouper: small gaps plus a cue (question mark, speaker change, ...).</summary>
    Rules,
    /// <summary>Language model decides (AI grouper); the rules are the fallback for failed requests.</summary>
    AI
  }


  /// <summary>
  /// Multi-line snippets: grouping mode, the limits every grouping must respect,
  /// dead-space removal, and the rule-based grouper's knobs.
  /// </summary>
  public class SnippetSettings
  {
    public SnippetMode Mode { get; set; } = SnippetMode.Off;

    /// <summary>Maximum trimmed duration of one card, in seconds.</summary>
    public int MaxSnippetSeconds { get; set; } = 15;

    /// <summary>Shorten silences inside a multi-line card when cutting media.</summary>
    public bool GapRemovalEnabled { get; set; } = true;

    /// <summary>A gap longer than this (ms) is shortened to this length; shorter gaps are untouched.</summary>
    public int GapKeepMs { get; set; } = 500;

    /// <summary>Put between the texts of the lines that make up a card.</summary>
    public string Separator { get; set; } = "<br>";

    public int RulesMaxJoinGapMs { get; set; } = 1500;
    public bool RulesRequireCue { get; set; } = true;
    public string RulesCueChars { get; set; } = "?？…→、,";
    public bool RulesJoinOnActorChange { get; set; } = true;

    // ── AI grouper (Mode = AI) ──

    /// <summary>Model ID; the prefix picks the provider (claude…, gpt…/o1…/o3…/o4…, gemini…).</summary>
    public string AiModel { get; set; } = "claude-sonnet-5";

    /// <summary>Target lines per request; chunks split at gaps of at least MaxSnippetSeconds. 0 = whole episode.</summary>
    public int ChunkTargetLines { get; set; } = 200;

    /// <summary>Free text appended to the system prompt, e.g. "the show is a workplace comedy".</summary>
    public string AiExtraInstructions { get; set; } = "";
  }


  public sealed class Settings
  {
    private static readonly Settings instance = CreateDefaults();

    [JsonPropertyName("subs")]
    public SubSettings[] Subs { get; set; }

    [JsonPropertyName("videoClips")]
    public VideoClips VideoClips { get; set; }

    [JsonPropertyName("audioClips")]
    public AudioClips AudioClips { get; set; }

    [JsonPropertyName("snapshots")]
    public Snapshots Snapshots { get; set; }

    [JsonPropertyName("animatedSnapshots")]
    public AnimatedSnapshots AnimatedSnapshots { get; set; }

    [JsonPropertyName("vobSubColors")]
    public VobSubColors VobSubColors { get; set; }

    // NOTE: typo "langauge" preserved for .s2s backward compatibility
    [JsonPropertyName("langaugeSpecific")]
    public LanguageSpecific LanguageSpecific { get; set; }

    [JsonPropertyName("snippets")]
    public SnippetSettings Snippets { get; set; }

    [JsonPropertyName("outputDir")]
    public string OutputDir { get; set; }

    [JsonPropertyName("timeShiftEnabled")]
    public bool TimeShiftEnabled { get; set; }

    [JsonPropertyName("spanEnabled")]
    public bool SpanEnabled { get; set; }

    [JsonPropertyName("spanStart")]
    public TimeSpan SpanStart { get; set; }

    [JsonPropertyName("spanEnd")]
    public TimeSpan SpanEnd { get; set; }

    private string _deckName = "";

    [JsonPropertyName("deckName")]
    public string DeckName
    {
      get => _deckName;
      set => _deckName = value.Trim().Replace(" ", "_");
    }

    [JsonPropertyName("episodeStartNumber")]
    public int EpisodeStartNumber { get; set; }

    /// <summary>
    /// Last episode number to process (inclusive).
    /// 0 means no limit — process all matched files.
    /// </summary>
    [JsonPropertyName("episodeEndNumber")]
    public int EpisodeEndNumber { get; set; }

    [JsonPropertyName("actorList")]
    public List<string> ActorList { get; set; }

    [JsonPropertyName("contextLeadingCount")]
    public int ContextLeadingCount { get; set; }

    [JsonPropertyName("contextTrailingCount")]
    public int ContextTrailingCount { get; set; }

    [JsonPropertyName("contextLeadingIncludeSnapshots")]
    public bool ContextLeadingIncludeSnapshots { get; set; }

    [JsonPropertyName("contextLeadingIncludeAudioClips")]
    public bool ContextLeadingIncludeAudioClips { get; set; }

    [JsonPropertyName("contextLeadingIncludeVideoClips")]
    public bool ContextLeadingIncludeVideoClips { get; set; }

    [JsonPropertyName("contextLeadingRange")]
    public int ContextLeadingRange { get; set; }

    [JsonPropertyName("contextTrailingIncludeSnapshots")]
    public bool ContextTrailingIncludeSnapshots { get; set; }

    [JsonPropertyName("contextTrailingIncludeAudioClips")]
    public bool ContextTrailingIncludeAudioClips { get; set; }

    [JsonPropertyName("contextTrailingIncludeVideoClips")]
    public bool ContextTrailingIncludeVideoClips { get; set; }

    [JsonPropertyName("contextTrailingRange")]
    public int ContextTrailingRange { get; set; }

    public static Settings Instance => instance;

    /// <summary>
    /// Path to the currently loaded/saved project file.
    /// Empty when no project is loaded. Not serialized — runtime only.
    /// </summary>
    [JsonIgnore]
    public string ProjectPath { get; set; } = "";

    // Public parameterless constructor required by ObjectCopier (JSON round-trip).
    public Settings() { }

    // Explicit static ctor preserves lazy initialization semantics.
    static Settings() { }

    /// <summary>
    /// Create a new <see cref="Settings"/> instance with all defaults applied.
    /// </summary>
    public static Settings CreateDefaults()
    {
      var s = new Settings();
      s.Reset();
      return s;
    }

    /// <summary>
    /// Deep-clone the current state for later restore via <see cref="RestoreFrom"/>.
    /// </summary>
    public Settings Snapshot() => ObjectCopier.Clone(this);

    /// <summary>
    /// Overwrite all mutable properties from <paramref name="other"/>.
    /// Transient arrays (Files) are reset to empty.
    /// </summary>
    public void RestoreFrom(Settings other)
    {
      Subs = other.Subs;
      Subs[0].Files = Array.Empty<string>();
      Subs[1].Files = Array.Empty<string>();
      VideoClips = other.VideoClips;
      VideoClips.Files = Array.Empty<string>();
      AudioClips = other.AudioClips;
      AudioClips.Files = Array.Empty<string>();
      Snapshots = other.Snapshots;
      AnimatedSnapshots = other.AnimatedSnapshots ?? new AnimatedSnapshots();
      VobSubColors = other.VobSubColors;
      LanguageSpecific = other.LanguageSpecific;
      Snippets = other.Snippets ?? new SnippetSettings();

      OutputDir = other.OutputDir;

      TimeShiftEnabled = other.TimeShiftEnabled;

      SpanEnabled = other.SpanEnabled;
      SpanStart = other.SpanStart;
      SpanEnd = other.SpanEnd;

      DeckName = other.DeckName;
      EpisodeStartNumber = other.EpisodeStartNumber;
      EpisodeEndNumber = other.EpisodeEndNumber;

      ActorList = other.ActorList;

      ContextLeadingCount = other.ContextLeadingCount;
      ContextLeadingIncludeSnapshots = other.ContextLeadingIncludeSnapshots;
      ContextLeadingIncludeAudioClips = other.ContextLeadingIncludeAudioClips;
      ContextLeadingIncludeVideoClips = other.ContextLeadingIncludeVideoClips;
      ContextLeadingRange = other.ContextLeadingRange;

      ContextTrailingCount = other.ContextTrailingCount;
      ContextTrailingIncludeSnapshots = other.ContextTrailingIncludeSnapshots;
      ContextTrailingIncludeAudioClips = other.ContextTrailingIncludeAudioClips;
      ContextTrailingIncludeVideoClips = other.ContextTrailingIncludeVideoClips;
      ContextTrailingRange = other.ContextTrailingRange;
    }

    /// <summary>
    /// Set all properties to their defaults (synced from ConstantSettings / PrefDefaults).
    /// </summary>
    public void Reset()
    {
      Subs = new SubSettings[2];
      Subs[0] = new SubSettings();
      Subs[1] = new SubSettings();
      Subs[0].ActorsEnabled = true;
      Subs[0].TimingsEnabled = true;

      // Sync preference defaults → SubSettings
      Subs[0].Encoding = ConstantSettings.DefaultEncodingSubs1;
      Subs[1].Encoding = ConstantSettings.DefaultEncodingSubs2;
      Subs[0].RemoveStyledLines = ConstantSettings.DefaultRemoveStyledLinesSubs1;
      Subs[1].RemoveStyledLines = ConstantSettings.DefaultRemoveStyledLinesSubs2;
      Subs[0].RemoveNoCounterpart = ConstantSettings.DefaultRemoveNoCounterpartSubs1;
      Subs[1].RemoveNoCounterpart = ConstantSettings.DefaultRemoveNoCounterpartSubs2;
      Subs[0].IncludedWords = UtilsCommon.removeExtraSpaces(
        ConstantSettings.DefaultIncludeTextSubs1.Split(';', StringSplitOptions.RemoveEmptyEntries));
      Subs[1].IncludedWords = UtilsCommon.removeExtraSpaces(
        ConstantSettings.DefaultIncludeTextSubs2.Split(';', StringSplitOptions.RemoveEmptyEntries));
      Subs[0].ExcludedWords = UtilsCommon.removeExtraSpaces(
        ConstantSettings.DefaultExcludeTextSubs1.Split(';', StringSplitOptions.RemoveEmptyEntries));
      Subs[1].ExcludedWords = UtilsCommon.removeExtraSpaces(
        ConstantSettings.DefaultExcludeTextSubs2.Split(';', StringSplitOptions.RemoveEmptyEntries));
      Subs[0].ExcludeDuplicateLinesEnabled = ConstantSettings.DefaultExcludeDuplicateLinesSubs1;
      Subs[1].ExcludeDuplicateLinesEnabled = ConstantSettings.DefaultExcludeDuplicateLinesSubs2;
      Subs[0].ExcludeFewerEnabled = ConstantSettings.DefaultExcludeLinesFewerThanCharsSubs1;
      Subs[1].ExcludeFewerEnabled = ConstantSettings.DefaultExcludeLinesFewerThanCharsSubs2;
      Subs[0].ExcludeFewerCount = ConstantSettings.DefaultExcludeLinesFewerThanCharsNumSubs1;
      Subs[1].ExcludeFewerCount = ConstantSettings.DefaultExcludeLinesFewerThanCharsNumSubs2;
      Subs[0].ExcludeShorterThanTimeEnabled = ConstantSettings.DefaultExcludeLinesShorterThanMsSubs1;
      Subs[1].ExcludeShorterThanTimeEnabled = ConstantSettings.DefaultExcludeLinesShorterThanMsSubs2;
      Subs[0].ExcludeShorterThanTime = ConstantSettings.DefaultExcludeLinesShorterThanMsNumSubs1;
      Subs[1].ExcludeShorterThanTime = ConstantSettings.DefaultExcludeLinesShorterThanMsNumSubs2;
      Subs[0].ExcludeLongerThanTimeEnabled = ConstantSettings.DefaultExcludeLinesLongerThanMsSubs1;
      Subs[1].ExcludeLongerThanTimeEnabled = ConstantSettings.DefaultExcludeLinesLongerThanMsSubs2;
      Subs[0].ExcludeLongerThanTime = ConstantSettings.DefaultExcludeLinesLongerThanMsNumSubs1;
      Subs[1].ExcludeLongerThanTime = ConstantSettings.DefaultExcludeLinesLongerThanMsNumSubs2;
      Subs[0].JoinSentencesEnabled = ConstantSettings.DefaultJoinSentencesSubs1;
      Subs[1].JoinSentencesEnabled = ConstantSettings.DefaultJoinSentencesSubs2;
      Subs[0].JoinSentencesCharList = ConstantSettings.DefaultJoinSentencesCharListSubs1;
      Subs[1].JoinSentencesCharList = ConstantSettings.DefaultJoinSentencesCharListSubs2;

      // Sync preference defaults → media settings
      AudioClips = new AudioClips();
      AudioClips.Enabled = ConstantSettings.DefaultEnableAudioClipGeneration;
      AudioClips.Bitrate = ConstantSettings.DefaultAudioClipBitrate;
      AudioClips.AudioFormat = ConstantSettings.AudioFormat;
      AudioClips.Normalize = ConstantSettings.DefaultAudioNormalize;

      Snapshots = new Snapshots();
      Snapshots.Enabled = ConstantSettings.DefaultEnableSnapshotsGeneration;
      Snapshots.Quality = ConstantSettings.DefaultSnapshotJpegQuality;

      AnimatedSnapshots = new AnimatedSnapshots();

      VideoClips = new VideoClips();
      VideoClips.Enabled = ConstantSettings.DefaultEnableVideoClipsGeneration;
      VideoClips.BitrateVideo = ConstantSettings.DefaultVideoClipVideoBitrate;
      VideoClips.BitrateAudio = ConstantSettings.DefaultVideoClipAudioBitrate;
      VideoClips.IPodSupport = ConstantSettings.DefaultIphoneSupport;

      VobSubColors = new VobSubColors();
      LanguageSpecific = new LanguageSpecific();
      Snippets = new SnippetSettings();
      OutputDir = ConstantSettings.DefaultOutputDir;
      TimeShiftEnabled = false;
      SpanEnabled = false;
      SpanStart = TimeSpan.FromMilliseconds(90_000);
      SpanEnd = TimeSpan.FromMilliseconds(1_350_000);
      DeckName = "";
      EpisodeStartNumber = 1;
      EpisodeEndNumber = 0;

      ActorList = new List<string>();

      ContextLeadingCount = ConstantSettings.DefaultContextNumLeading;
      ContextLeadingIncludeSnapshots = false;
      ContextLeadingIncludeAudioClips = false;
      ContextLeadingIncludeVideoClips = false;
      ContextLeadingRange = ConstantSettings.DefaultContextLeadingRange;

      ContextTrailingCount = ConstantSettings.DefaultContextNumTrailing;
      ContextTrailingIncludeSnapshots = false;
      ContextTrailingIncludeAudioClips = false;
      ContextTrailingIncludeVideoClips = false;
      ContextTrailingRange = ConstantSettings.DefaultContextTrailingRange;
    }

    public void reset()
    {
      Reset();
    }
  }
}
