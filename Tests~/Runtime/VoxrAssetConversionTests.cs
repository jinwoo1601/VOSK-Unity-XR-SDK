using System;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoXR.Commands;

namespace VoXR.Tests.Runtime
{
    public class VoxrAssetConversionTests
    {
        GameObject _recogniserObject;
        VoxrSlotAsset _slotAsset;
        VoxrCommandSetAsset _setAsset;
        VoxrCommandAsset _commandAsset;

        [TearDown]
        public void DestroyInspectorFixtures()
        {
            if (_recogniserObject != null)
                UnityEngine.Object.DestroyImmediate(_recogniserObject);
            if (_setAsset != null)
                UnityEngine.Object.DestroyImmediate(_setAsset);
            if (_commandAsset != null)
                UnityEngine.Object.DestroyImmediate(_commandAsset);
            if (_slotAsset != null)
                UnityEngine.Object.DestroyImmediate(_slotAsset);
        }

        // --- VoxrSlotAsset ---

        [Test]
        public void SlotAsset_Enumerated_ProducesCorrectDefinition()
        {
            var asset = ScriptableObject.CreateInstance<VoxrSlotAsset>();
            asset.slotName = "weapon";
            asset.slotType = VoxrSlotType.Enumerated;
            asset.values = new[] { "missiles", "torpedoes" };
            asset.aliases = new[]
            {
                new VoxrSlotAsset.AliasEntry { variant = "jackals", canonical = "jackal" },
            };

            var def = asset.ToDefinition();

            Assert.AreEqual("weapon", def.Name);
            Assert.AreEqual(VoxrSlotType.Enumerated, def.Type);
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
            var asset = ScriptableObject.CreateInstance<VoxrSlotAsset>();
            asset.slotName = "heading";
            asset.slotType = VoxrSlotType.NumberSequence;
            asset.minWords = 1;
            asset.maxWords = 3;

            var def = asset.ToDefinition();

            Assert.AreEqual("heading", def.Name);
            Assert.AreEqual(VoxrSlotType.NumberSequence, def.Type);
            Assert.AreEqual(1, def.MinWords);
            Assert.AreEqual(3, def.MaxWords);
            Assert.AreEqual(0, def.Values.Length);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void SlotAsset_EmptyAliases_ProducesNullAliasDict()
        {
            var asset = ScriptableObject.CreateInstance<VoxrSlotAsset>();
            asset.slotName = "target";
            asset.slotType = VoxrSlotType.Enumerated;
            asset.values = new[] { "hotel one" };
            asset.aliases = Array.Empty<VoxrSlotAsset.AliasEntry>();

            var def = asset.ToDefinition();

            Assert.IsNull(def.Aliases);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void SlotAsset_NullAliases_ProducesNullAliasDict()
        {
            var asset = ScriptableObject.CreateInstance<VoxrSlotAsset>();
            asset.slotName = "target";
            asset.slotType = VoxrSlotType.Enumerated;
            asset.values = new[] { "alpha one" };
            asset.aliases = null;

            var def = asset.ToDefinition();

            Assert.IsNull(def.Aliases);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void SlotAsset_NullValues_ProducesEmptyArray()
        {
            var asset = ScriptableObject.CreateInstance<VoxrSlotAsset>();
            asset.slotName = "empty";
            asset.slotType = VoxrSlotType.Enumerated;
            asset.values = null;

            var def = asset.ToDefinition();

            Assert.AreEqual(0, def.Values.Length);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        // --- VoxrCommandAsset ---

        [Test]
        public void CommandAsset_SinglePattern_SplitsCorrectly()
        {
            var asset = ScriptableObject.CreateInstance<VoxrCommandAsset>();
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
            var asset = ScriptableObject.CreateInstance<VoxrCommandAsset>();
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
            var asset = ScriptableObject.CreateInstance<VoxrCommandAsset>();
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
            var asset = ScriptableObject.CreateInstance<VoxrCommandAsset>();
            asset.intent = "test";
            asset.patterns = new[] { "  fire   weapons  " };

            var def = asset.ToDefinition();

            Assert.AreEqual(new[] { "fire", "weapons" }, def.Patterns[0]);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void CommandAsset_EmptyPattern_ProducesEmptyTokenArray()
        {
            var asset = ScriptableObject.CreateInstance<VoxrCommandAsset>();
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
            var asset = ScriptableObject.CreateInstance<VoxrCommandAsset>();
            asset.intent = "test";
            asset.patterns = null;

            var def = asset.ToDefinition();

            Assert.AreEqual(0, def.Patterns.Length);

            UnityEngine.Object.DestroyImmediate(asset);
        }

        // --- VoxrCommandSetAsset ---

        [Test]
        public void CommandSetAsset_ConvertsNameAndCommands()
        {
            var cmdAsset = ScriptableObject.CreateInstance<VoxrCommandAsset>();
            cmdAsset.intent = "cease_fire";
            cmdAsset.patterns = new[] { "cease fire" };

            var setAsset = ScriptableObject.CreateInstance<VoxrCommandSetAsset>();
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
            var cmdAsset = ScriptableObject.CreateInstance<VoxrCommandAsset>();
            cmdAsset.intent = "test";
            cmdAsset.patterns = new[] { "hello" };

            var setAsset = ScriptableObject.CreateInstance<VoxrCommandSetAsset>();
            setAsset.setName = "mixed";
            setAsset.commands = new VoxrCommandAsset[] { cmdAsset, null };

            var set = setAsset.ToSet();

            Assert.AreEqual(1, set.Commands.Length);
            Assert.AreEqual("test", set.Commands[0].Intent);

            UnityEngine.Object.DestroyImmediate(cmdAsset);
            UnityEngine.Object.DestroyImmediate(setAsset);
        }

        [Test]
        public void CommandSetAsset_EmptyCommands_ProducesEmptySet()
        {
            var setAsset = ScriptableObject.CreateInstance<VoxrCommandSetAsset>();
            setAsset.setName = "empty";
            setAsset.commands = Array.Empty<VoxrCommandAsset>();

            var set = setAsset.ToSet();

            Assert.AreEqual("empty", set.Name);
            Assert.AreEqual(0, set.Commands.Length);

            UnityEngine.Object.DestroyImmediate(setAsset);
        }

        // --- Round-trip ---

        [Test]
        public void RoundTrip_AssetToDefinitionToParser_MatchesCommand()
        {
            var slotAsset = ScriptableObject.CreateInstance<VoxrSlotAsset>();
            slotAsset.slotName = "weapon";
            slotAsset.slotType = VoxrSlotType.Enumerated;
            slotAsset.values = new[] { "missiles", "torpedoes" };

            var cmdAsset = ScriptableObject.CreateInstance<VoxrCommandAsset>();
            cmdAsset.intent = "fire";
            cmdAsset.patterns = new[] { "fire {weapon}" };

            var slots = new[] { slotAsset.ToDefinition() };
            var commands = new[] { cmdAsset.ToDefinition() };

            var parser = new VoxrCommandParser(slots, commands);
            var results = parser.Parse("fire missiles", Array.Empty<VoxrWord>());

            Assert.AreEqual(1, results.Length);
            Assert.AreEqual("fire", results[0].Command.Intent);
            Assert.AreEqual("missiles", results[0].Command.GetSlot("weapon"));

            UnityEngine.Object.DestroyImmediate(slotAsset);
            UnityEngine.Object.DestroyImmediate(cmdAsset);
        }

        // --- Inspector conversion in Awake (issue #110) ---

        // Awake runs on activation, so the component is added to an inactive object, its private
        // serialized fields are written, and only then is the object activated — the same order
        // the Inspector produces, which AddComponent on a live object cannot reproduce.
        VoxrCommandRecogniser BuildInactiveRecogniser(
            VoxrSlotAsset[] slots,
            VoxrCommandSetAsset[] sets,
            string[] initialActiveSetNames
        )
        {
            _recogniserObject = new GameObject("AwakeConversion");
            _recogniserObject.SetActive(false);

            var recogniser = _recogniserObject.AddComponent<VoxrCommandRecogniser>();
            SetSerialisedField(recogniser, "slotAssets", slots);
            SetSerialisedField(recogniser, "commandSetAssets", sets);
            SetSerialisedField(recogniser, "initialActiveSetNames", initialActiveSetNames);
            return recogniser;
        }

        static void SetSerialisedField(VoxrCommandRecogniser target, string name, object value)
        {
            var field = typeof(VoxrCommandRecogniser).GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic
            );

            Assert.IsNotNull(field, $"the recogniser must carry a serialized '{name}' field");
            field.SetValue(target, value);
        }

        // A grammar with no slots at all — the shape that could not be authored in the Inspector.
        VoxrCommandSetAsset MakeAllLiteralSet()
        {
            _commandAsset = ScriptableObject.CreateInstance<VoxrCommandAsset>();
            _commandAsset.intent = "cease_fire";
            _commandAsset.patterns = new[] { "cease fire" };

            _setAsset = ScriptableObject.CreateInstance<VoxrCommandSetAsset>();
            _setAsset.setName = "weapons";
            _setAsset.commands = new[] { _commandAsset };
            return _setAsset;
        }

        [Test]
        public void Awake_EmptySlotAssets_ConvertsAllLiteralCommandSets()
        {
            var recogniser = BuildInactiveRecogniser(
                Array.Empty<VoxrSlotAsset>(),
                new[] { MakeAllLiteralSet() },
                new[] { "weapons" }
            );

            _recogniserObject.SetActive(true);

            // Fire synchronously rather than after the buffer window.
            recogniser.BufferWindow = 0f;
            recogniser.CommandCooldown = 0f;

            VoxrCommand? recognised = null;
            recogniser.OnCommandRecognised += cmd => recognised = cmd;
            recogniser.InjectText("cease fire");

            Assert.IsTrue(
                recognised.HasValue,
                "an empty (non-null) Slot Assets array must still convert the command sets"
            );
            Assert.AreEqual("cease_fire", recognised.Value.Intent);
        }

        [Test]
        public void Awake_EmptyCommandSetAssets_WarnsWhenSlotAssetsAssigned()
        {
            _slotAsset = ScriptableObject.CreateInstance<VoxrSlotAsset>();
            _slotAsset.slotName = "weapon";
            _slotAsset.slotType = VoxrSlotType.Enumerated;
            _slotAsset.values = new[] { "missiles" };

            BuildInactiveRecogniser(new[] { _slotAsset }, Array.Empty<VoxrCommandSetAsset>(), null);

            LogAssert.Expect(LogType.Warning, new Regex("Command Set Assets is empty"));

            _recogniserObject.SetActive(true);

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Awake_NullSlotAssets_WarnsWhenCommandSetAssetsAssigned()
        {
            BuildInactiveRecogniser(null, new[] { MakeAllLiteralSet() }, new[] { "weapons" });

            LogAssert.Expect(LogType.Warning, new Regex("Slot Assets is null"));

            _recogniserObject.SetActive(true);

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Awake_NoAssetsAssigned_StaysSilent()
        {
            // Nothing wired is the code-configured case, not a misconfiguration: Configure()
            // may follow from Start(), so this path must not warn.
            BuildInactiveRecogniser(
                Array.Empty<VoxrSlotAsset>(),
                Array.Empty<VoxrCommandSetAsset>(),
                null
            );

            _recogniserObject.SetActive(true);

            LogAssert.NoUnexpectedReceived();
        }
    }
}
