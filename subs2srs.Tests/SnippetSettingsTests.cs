//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using System.Text;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
    /// <summary>Snippet settings live in the project file and default sensibly when absent.</summary>
    public class SnippetSettingsTests
    {
        [Fact]
        public void Defaults()
        {
            var s = Settings.CreateDefaults().Snippets;
            Assert.Equal(SnippetMode.Off, s.Mode);
            Assert.Equal(15, s.MaxSnippetSeconds);
            Assert.True(s.GapRemovalEnabled);
            Assert.Equal(500, s.GapKeepMs);
            Assert.Equal("<br>", s.Separator);
            Assert.Equal(1500, s.RulesMaxJoinGapMs);
            Assert.True(s.RulesRequireCue);
            Assert.True(s.RulesJoinOnActorChange);
        }

        [Fact]
        public void ProjectFile_RoundTripsSnippetSettings_AsStrings()
        {
            using var scope = new TestScope();
            string path = Path.Combine(scope.TempDir, "p.s2s.json");

            Settings.Instance.Snippets.Mode = SnippetMode.Rules;
            Settings.Instance.Snippets.GapKeepMs = 300;
            Settings.Instance.Snippets.RulesCueChars = "?";
            ProjectIO.Save(path, Settings.Instance);

            string json = File.ReadAllText(path, Encoding.UTF8);
            Assert.Contains("\"mode\": \"Rules\"", json);

            Settings.Instance.Reset();
            Assert.Equal(SnippetMode.Off, Settings.Instance.Snippets.Mode);

            ProjectIO.Load(path);
            Assert.Equal(SnippetMode.Rules, Settings.Instance.Snippets.Mode);
            Assert.Equal(300, Settings.Instance.Snippets.GapKeepMs);
            Assert.Equal("?", Settings.Instance.Snippets.RulesCueChars);
        }

        [Fact]
        public void ProjectFile_WithoutSnippetsSection_LoadsDefaults()
        {
            using var scope = new TestScope();
            string path = Path.Combine(scope.TempDir, "old.s2s.json");
            File.WriteAllText(path, "{ \"deckName\": \"Old\", \"episodeStartNumber\": 2 }", Encoding.UTF8);

            ProjectIO.Load(path);

            Assert.Equal("Old", Settings.Instance.DeckName);
            Assert.NotNull(Settings.Instance.Snippets);
            Assert.Equal(SnippetMode.Off, Settings.Instance.Snippets.Mode);
        }

        [Fact]
        public void Snapshot_And_Restore_CopySnippetSettings()
        {
            using var scope = new TestScope();
            Settings.Instance.Snippets.Mode = SnippetMode.Rules;
            var snap = Settings.Instance.Snapshot();
            Assert.NotSame(Settings.Instance.Snippets, snap.Snippets);

            Settings.Instance.Snippets.Mode = SnippetMode.Off;
            Settings.Instance.RestoreFrom(snap);
            Assert.Equal(SnippetMode.Rules, Settings.Instance.Snippets.Mode);
        }

        [Fact]
        public void Preferences_HaveSnippetDefaults()
        {
            var p = new PreferencesData();
            Assert.Equal("", p.ValidationDir);
            Assert.Equal("<Control>Up w", p.GroupingKeyAttachAbove);
            Assert.Equal("<Control>Down s", p.GroupingKeyAttachBelow);
            Assert.Equal("<Control>BackSpace BackSpace x", p.GroupingKeyDetach);
        }
    }
}
