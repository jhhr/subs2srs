//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Text.Json;
using subs2srs.Tests.Harness;
using Xunit;

namespace subs2srs.Tests
{
    public class GroupingValidationFileTests
    {
        [Fact]
        public void Build_RecordsKeptLinesJoinsProposalAndOmissionSettings()
        {
            using var scope = new TestScope();
            var s = Settings.Instance;
            s.Subs[0].ExcludeFewerEnabled = true;
            s.Subs[0].ExcludeFewerCount = 3;
            s.Subs[0].ExcludedWords = new[] { "♪" };
            s.Subs[1].Files = Array.Empty<string>();

            var lines = SnippetGroupingTests.Dialogue();
            lines[2].Active = false;
            bool[] joins = { true, false, false, false };
            bool[] proposal = { false, false, false, false };

            string srt = Path.Combine(scope.TempDir, "ep01.srt");
            File.WriteAllText(srt, "1\r\n00:00:01,000 --> 00:00:02,000\r\nx\r\n");

            var file = GroupingValidationFile.Build(lines, joins, proposal, "ai",
                new SnippetLimits(15_000, 500, 0), 7, srt, null, "claude-sonnet-5", 3);

            Assert.Equal(1, file.Version);
            Assert.Equal("ep01.srt", file.Source.Subs1);
            Assert.Null(file.Source.Subs2);
            Assert.Equal(64, file.Source.Subs1Sha256!.Length);
            Assert.Equal(7, file.Source.Episode);
            Assert.Equal(3, file.Source.Omission.Subs1.ExcludeFewerChars);
            Assert.Null(file.Source.Omission.Subs1.ExcludeShorterThanMs);
            Assert.Equal(new[] { "♪" }, file.Source.Omission.Subs1.ExcludedWords);
            Assert.Equal(15.0, file.Limits.MaxSnippetSeconds);

            Assert.Equal(3, file.Lines.Count);                 // omitted line dropped
            Assert.Equal(new[] { 0, 1, 3 }, new[] { file.Lines[0].I, file.Lines[1].I, file.Lines[2].I });
            Assert.Equal("00:00:02.400", file.Lines[1].S);
            Assert.Null(file.Lines[0].T2);                     // no Subs2 configured
            Assert.Equal(new[] { true, false }, file.Joins);   // kept projection, n-1 entries
            Assert.Equal("ai", file.Proposal!.Producer);
            Assert.Equal("claude-sonnet-5", file.Proposal.Model);
            Assert.Equal(3, file.Proposal.PromptVersion);
            Assert.Equal(new[] { false, false }, file.Proposal.Joins);
            Assert.Contains("\"model\": \"claude-sonnet-5\"", file.ToJson());
        }

        [Fact]
        public void Json_RoundTrips_And_ToLinesRebuildsTiming()
        {
            using var scope = new TestScope();
            var lines = SnippetGroupingTests.Dialogue();
            var file = GroupingValidationFile.Build(lines, new[] { true, false, true, false }, null, "rules",
                new SnippetLimits(15_000, 500, 0), 1, "a.srt", "a.en.srt");

            string json = file.ToJson();
            Assert.Contains("\"joins\"", json);
            Assert.DoesNotContain("\"proposal\"", json);
            Assert.Contains("Where are you going?", json); // not escaped as ?

            var back = GroupingValidationFile.FromJson(json);
            Assert.Equal(file.Joins, back.Joins);
            Assert.Equal("a.en.srt", back.Source.Subs2);

            var rebuilt = back.ToLines();
            Assert.Equal(4, rebuilt.Count);
            Assert.Equal(TimeSpan.FromSeconds(6), rebuilt[2].Subs1.StartTime);
            Assert.Equal("SEE YOU LATER.", rebuilt[2].Subs2.Text);
            Assert.All(rebuilt, l => Assert.True(l.Active));
        }

        [Fact]
        public void WriteAndRead_CreateTheDirectory()
        {
            using var scope = new TestScope();
            var file = GroupingValidationFile.Build(SnippetGroupingTests.Dialogue(), new bool[4], null, "rules",
                new SnippetLimits(), 1, "a.srt", null);
            string path = Path.Combine(scope.TempDir, "validation", GroupingValidationFile.FileName("Deck", 1));

            file.Write(path);

            Assert.True(File.Exists(path));
            Assert.Equal("Deck_1.grouping.json", Path.GetFileName(path));
            Assert.Equal(4, GroupingValidationFile.Read(path).Lines.Count);
        }
    }
}
