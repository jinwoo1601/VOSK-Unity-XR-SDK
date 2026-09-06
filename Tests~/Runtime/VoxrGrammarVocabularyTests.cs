// ============================================================================
// Purpose:  PlayMode tests for the grammar-vocabulary authoring warning
// Layer:    Tests.Runtime
// Owns:     VoxrGrammarVocabularyTests (public class)
// Depends:  VoxrGrammarVocabulary
// ============================================================================
#if UNITY_EDITOR_WIN
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoXR.Native;

namespace VoXR.Tests.Runtime
{
    public class VoxrGrammarVocabularyTests
    {
        [Test]
        public void ExtractWords_SplitsMultiWordEntriesAndDedupes()
        {
            List<string> words = VoxrGrammarVocabulary.ExtractWords(
                "[\"close distance\", \"close\", \"distance\", \"cease fire\"]"
            );

            Assert.That(words, Is.EqualTo(new[] { "close", "distance", "cease", "fire" }));
        }

        [Test]
        public void ExtractWords_ExcludesUnkToken()
        {
            List<string> words = VoxrGrammarVocabulary.ExtractWords(
                "[\"[unk]\", \"cease fire\", \"cease\", \"fire\"]"
            );

            Assert.That(words, Does.Not.Contain("[unk]"));
            Assert.That(words, Is.EqualTo(new[] { "cease", "fire" }));
        }

        [Test]
        public void ExtractWords_EmptyOrNullInput_ReturnsEmpty()
        {
            Assert.That(VoxrGrammarVocabulary.ExtractWords(null), Is.Empty);
            Assert.That(VoxrGrammarVocabulary.ExtractWords(""), Is.Empty);
            Assert.That(VoxrGrammarVocabulary.ExtractWords("[]"), Is.Empty);
        }

        [Test]
        public void WarnOnUnknownWords_WarnsOnceNamingTheUnknownWord()
        {
            LogAssert.Expect(LogType.Warning, new Regex("Grammar word \"cqb\" is not in the"));

            VoxrGrammarVocabulary.WarnOnUnknownWords(
                "[\"cqb\", \"safe range\", \"cease fire\"]",
                w => w != "cqb"
            );

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void WarnOnUnknownWords_AllKnown_WarnsNothing()
        {
            VoxrGrammarVocabulary.WarnOnUnknownWords(
                "[\"cease fire\", \"cease\", \"fire\"]",
                w => true
            );

            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void WarnOnUnknownWords_RepeatedUnknownWord_WarnsOnce()
        {
            // "cqb" appears in three entries; ExtractWords de-duplicates, so exactly one
            // warning is expected. A second would be an unexpected log and fail below.
            LogAssert.Expect(LogType.Warning, new Regex("Grammar word \"cqb\" is not in the"));

            VoxrGrammarVocabulary.WarnOnUnknownWords(
                "[\"cqb\", \"cqb range\", \"cqb\"]",
                w => w != "cqb"
            );

            LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
