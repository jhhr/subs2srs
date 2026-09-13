//  Copyright (C) 2026 fkzys and contributors
//  SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace subs2srs.Tests
{
    public class KeyBindingTests
    {
        [Theory]
        [InlineData("<Control>Up", "Up", KeyBinding.Mods.Control)]
        [InlineData("<Shift><Alt>BackSpace", "BackSpace", KeyBinding.Mods.Shift | KeyBinding.Mods.Alt)]
        [InlineData("<ctrl><super>x", "x", KeyBinding.Mods.Control | KeyBinding.Mods.Super)]
        [InlineData("w", "w", KeyBinding.Mods.None)]
        [InlineData("  <Primary>Down ", "Down", KeyBinding.Mods.Control)]
        public void Parse_ReadsModifiersAndKeyName(string text, string key, KeyBinding.Mods mods)
        {
            var b = KeyBinding.Parse(text);
            Assert.NotNull(b);
            Assert.Equal(key, b!.KeyName);
            Assert.Equal(mods, b.Modifiers);
        }

        [Theory]
        [InlineData("")]
        [InlineData("<Foo>x")]
        [InlineData("<Control>")]
        [InlineData("<Control")]
        [InlineData("Ctrl Up")]
        public void Parse_RejectsInvalidText(string text)
        {
            Assert.Null(KeyBinding.Parse(text));
        }

        [Fact]
        public void ParseList_SplitsOnWhitespaceAndSkipsInvalidEntries()
        {
            var list = KeyBinding.ParseList("<Control>Up w <Bogus>q");
            Assert.Equal(2, list.Count);
            Assert.Equal("<Control>Up", list[0].ToString());
            Assert.Equal("w", list[1].ToString());
        }

        [Fact]
        public void Matches_ComparesKeyvalAndRelevantModifiersOnly()
        {
            var b = KeyBinding.Parse("<Control>Up")!;
            Assert.True(b.Matches(65362, 65362, KeyBinding.Mods.Control));
            Assert.True(b.Matches(65362, 65362, KeyBinding.Mods.Control | KeyBinding.Mods.Meta)); // Meta not relevant
            Assert.False(b.Matches(65362, 65362, KeyBinding.Mods.None));
            Assert.False(b.Matches(65362, 65362, KeyBinding.Mods.Control | KeyBinding.Mods.Shift));
            Assert.False(b.Matches(65364, 65362, KeyBinding.Mods.Control));
            Assert.False(b.Matches(0, 0, KeyBinding.Mods.Control)); // unresolved key never matches
        }
    }
}
