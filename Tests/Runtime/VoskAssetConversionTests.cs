using System;
using NUnit.Framework;
using UnityEngine;
using VoskXR.Commands;

namespace VoskXR.Tests.Runtime
{
    public class VoskAssetConversionTests
    {
        // --- VoskSlotAsset ---

        [Test]
        public void SlotAsset_Enumerated_ProducesCorrectDefinition()
        {
            var asset = ScriptableObject.CreateInstance<VoskSlotAsset>();
            asset.slotName = "weapon";
            asset.slotType = VoskSlotType.Enumerated;
            asset.values = new[] { "missiles", "torpedoes" };
            asset.aliases = new[]
            {
                new VoskSlotAsset.AliasEntry { variant = "jackals", canonical = "jackal" },
            };

            var def = asset.ToDefinition();

            Assert.AreEqual("weapon", def.Name);
            Assert.AreEqual(VoskSlotType.Enumerated, def.Type);
            Assert.AreEqual(2, def.Values.Length);
            Assert.AreEqual("missiles", def.Values[0]);
            Assert.AreEqual("torpedoes", def.Values[1]);
            Assert.IsNotNull(def.Aliases);
            Assert.AreEqual("jackal", def.Aliases["jackals"]);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void SlotAsset_NumberSequence_ProducesCorrectDefinition()
        {
            var asset = ScriptableObject.CreateInstance<VoskSlotAsset>();
            asset.slotName = "heading";
            asset.slotType = VoskSlotType.NumberSequence;
            asset.minWords = 1;
            asset.maxWords = 3;

            var def = asset.ToDefinition();

            Assert.AreEqual("heading", def.Name);
            Assert.AreEqual(VoskSlotType.NumberSequence, def.Type);
            Assert.AreEqual(1, def.MinWords);
            Assert.AreEqual(3, def.MaxWords);
            Assert.AreEqual(0, def.Values.Length);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void SlotAsset_EmptyAliases_ProducesNullAliasDict()
        {
            var asset = ScriptableObject.CreateInstance<VoskSlotAsset>();
            asset.slotName = "target";
            asset.slotType = VoskSlotType.Enumerated;
            asset.values = new[] { "hotel one" };
            asset.aliases = Array.Empty<VoskSlotAsset.AliasEntry>();

            var def = asset.ToDefinition();

            Assert.IsNull(def.Aliases);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void SlotAsset_NullAliases_ProducesNullAliasDict()
        {
            var asset = ScriptableObject.CreateInstance<VoskSlotAsset>();
            asset.slotName = "target";
            asset.slotType = VoskSlotType.Enumerated;
            asset.values = new[] { "alpha one" };
            asset.aliases = null;

            var def = asset.ToDefinition();

            Assert.IsNull(def.Aliases);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void SlotAsset_NullValues_ProducesEmptyArray()
        {
            var asset = ScriptableObject.CreateInstance<VoskSlotAsset>();
            asset.slotName = "empty";
            asset.slotType = VoskSlotType.Enumerated;
            asset.values = null;

            var def = asset.ToDefinition();

            Assert.AreEqual(0, def.Values.Length);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        // --- VoskCommandAsset ---

        [Test]
        public void CommandAsset_SinglePattern_SplitsCorrectly()
        {
            var asset = ScriptableObject.CreateInstance<VoskCommandAsset>();
            asset.intent = "fire";
            asset.patterns = new[] { "fire weapons" };

            var def = asset.ToDefinition();

            Assert.AreEqual("fire", def.Intent);
            Assert.AreEqual(1, def.Patterns.Length);
            Assert.AreEqual(new[] { "fire", "weapons" }, def.Patterns[0]);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void CommandAsset_MultiplePatterns_SplitIndependently()
        {
            var asset = ScriptableObject.CreateInstance<VoskCommandAsset>();
            asset.intent = "launch";
            asset.patterns = new[]
            {
                "launch {weapon} target {target}",
                "fire {weapon} at {target}",
            };

            var def = asset.ToDefinition();

            Assert.AreEqual(2, def.Patterns.Length);
            Assert.AreEqual(new[] { "launch", "{weapon}", "target", "{target}" }, def.Patterns[0]);
            Assert.AreEqual(new[] { "fire", "{weapon}", "at", "{target}" }, def.Patterns[1]);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void CommandAsset_SlotTokens_PreservedAfterSplit()
        {
            var asset = ScriptableObject.CreateInstance<VoskCommandAsset>();
            asset.intent = "test";
            asset.patterns = new[] { "launch {?quantity} {weapon} ?a target" };

            var def = asset.ToDefinition();
            var tokens = def.Patterns[0];

            Assert.AreEqual("launch", tokens[0]);
            Assert.AreEqual("{?quantity}", tokens[1]);
            Assert.AreEqual("{weapon}", tokens[2]);
            Assert.AreEqual("?a", tokens[3]);
            Assert.AreEqual("target", tokens[4]);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void CommandAsset_ExtraWhitespace_HandledCorrectly()
        {
            var asset = ScriptableObject.CreateInstance<VoskCommandAsset>();
            asset.intent = "test";
            asset.patterns = new[] { "  fire   weapons  " };

            var def = asset.ToDefinition();

            Assert.AreEqual(new[] { "fire", "weapons" }, def.Patterns[0]);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void CommandAsset_EmptyPattern_ProducesEmptyTokenArray()
        {
            var asset = ScriptableObject.CreateInstance<VoskCommandAsset>();
            asset.intent = "test";
            asset.patterns = new[] { "" };

            var def = asset.ToDefinition();

            Assert.AreEqual(1, def.Patterns.Length);
            Assert.AreEqual(0, def.Patterns[0].Length);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void CommandAsset_NullPatterns_ProducesEmptyArray()
        {
            var asset = ScriptableObject.CreateInstance<VoskCommandAsset>();
            asset.intent = "test";
            asset.patterns = null;

            var def = asset.ToDefinition();

            Assert.AreEqual(0, def.Patterns.Length);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        // --- VoskCommandSetAsset ---

        [Test]
        public void CommandSetAsset_ConvertsNameAndCommands()
        {
            var cmdAsset = ScriptableObject.CreateInstance<VoskCommandAsset>();
            cmdAsset.intent = "cease_fire";
            cmdAsset.patterns = new[] { "cease fire" };

            var setAsset = ScriptableObject.CreateInstance<VoskCommandSetAsset>();
            setAsset.setName = "weapons";
            setAsset.commands = new[] { cmdAsset };

            var set = setAsset.ToSet();

            Assert.AreEqual("weapons", set.Name);
            Assert.AreEqual(1, set.Commands.Length);
            Assert.AreEqual("cease_fire", set.Commands[0].Intent);

            UnityEngine.Object.DestroyImmediate(cmdAsset);
            UnityEngine.Object.DestroyImmediate(setAsset);
        }

        [Test]
        public void CommandSetAsset_NullCommandEntry_SkippedWithWarning()
        {
            var cmdAsset = ScriptableObject.CreateInstance<VoskCommandAsset>();
            cmdAsset.intent = "test";
            cmdAsset.patterns = new[] { "hello" };

            var setAsset = ScriptableObject.CreateInstance<VoskCommandSetAsset>();
            setAsset.setName = "mixed";
            setAsset.commands = new VoskCommandAsset[] { cmdAsset, null };

            var set = setAsset.ToSet();

            Assert.AreEqual(1, set.Commands.Length);
            Assert.AreEqual("test", set.Commands[0].Intent);

            UnityEngine.Object.DestroyImmediate(cmdAsset);
            UnityEngine.Object.DestroyImmediate(setAsset);
        }

        [Test]
        public void CommandSetAsset_EmptyCommands_ProducesEmptySet()
        {
            var setAsset = ScriptableObject.CreateInstance<VoskCommandSetAsset>();
            setAsset.setName = "empty";
            setAsset.commands = Array.Empty<VoskCommandAsset>();

            var set = setAsset.ToSet();

            Assert.AreEqual("empty", set.Name);
            Assert.AreEqual(0, set.Commands.Length);

            UnityEngine.Object.DestroyImmediate(setAsset);
        }

        // --- Round-trip ---

        [Test]
        public void RoundTrip_AssetToDefinitionToParser_MatchesCommand()
        {
            var slotAsset = ScriptableObject.CreateInstance<VoskSlotAsset>();
            slotAsset.slotName = "weapon";
            slotAsset.slotType = VoskSlotType.Enumerated;
            slotAsset.values = new[] { "missiles", "torpedoes" };

            var cmdAsset = ScriptableObject.CreateInstance<VoskCommandAsset>();
            cmdAsset.intent = "fire";
            cmdAsset.patterns = new[] { "fire {weapon}" };

            var slots = new[] { slotAsset.ToDefinition() };
            var commands = new[] { cmdAsset.ToDefinition() };

            var parser = new VoskCommandParser(slots, commands);
            var results = parser.Parse("fire missiles", Array.Empty<VoskWord>());

            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("fire", results[0].Command.Intent);
            Assert.AreEqual("missiles", results[0].Command.GetSlot("weapon"));

            UnityEngine.Object.DestroyImmediate(slotAsset);
            UnityEngine.Object.DestroyImmediate(cmdAsset);
        }
    }
}
