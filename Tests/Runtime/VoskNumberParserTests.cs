using System;
using NUnit.Framework;
using VoskXR.Commands;

namespace VoskXR.Tests.Runtime
{
    public class VoskNumberParserTests
    {
        // --- ParseDigitSequence ---

        [Test]
        public void ParseDigitSequence_Zero()
        {
            Assert.AreEqual(0, VoskNumberParser.ParseDigitSequence("zero"));
        }

        [Test]
        public void ParseDigitSequence_Nine()
        {
            Assert.AreEqual(9, VoskNumberParser.ParseDigitSequence("nine"));
        }

        [Test]
        public void ParseDigitSequence_TwoDigits()
        {
            Assert.AreEqual(15, VoskNumberParser.ParseDigitSequence("one five"));
        }

        [Test]
        public void ParseDigitSequence_ThreeDigits()
        {
            Assert.AreEqual(270, VoskNumberParser.ParseDigitSequence("two seven zero"));
        }

        [Test]
        public void ParseDigitSequence_EmptyString()
        {
            Assert.AreEqual(0, VoskNumberParser.ParseDigitSequence(""));
        }

        [Test]
        public void ParseDigitSequence_Null()
        {
            Assert.AreEqual(0, VoskNumberParser.ParseDigitSequence(null));
        }

        [Test]
        public void ParseDigitSequence_NonDigitWord_Throws()
        {
            Assert.Throws<FormatException>(() => VoskNumberParser.ParseDigitSequence("fifteen"));
        }

        // --- ParseCardinal ---

        [Test]
        public void ParseCardinal_Teen()
        {
            Assert.AreEqual(15, VoskNumberParser.ParseCardinal("fifteen"));
        }

        [Test]
        public void ParseCardinal_Tens()
        {
            Assert.AreEqual(20, VoskNumberParser.ParseCardinal("twenty"));
        }

        [Test]
        public void ParseCardinal_TensPlusUnits()
        {
            Assert.AreEqual(42, VoskNumberParser.ParseCardinal("forty two"));
        }

        [Test]
        public void ParseCardinal_Hundred()
        {
            Assert.AreEqual(200, VoskNumberParser.ParseCardinal("two hundred"));
        }

        [Test]
        public void ParseCardinal_HundredPlusRemainder()
        {
            Assert.AreEqual(315, VoskNumberParser.ParseCardinal("three hundred fifteen"));
        }

        [Test]
        public void ParseCardinal_Thousand()
        {
            Assert.AreEqual(5000, VoskNumberParser.ParseCardinal("five thousand"));
        }

        [Test]
        public void ParseCardinal_EmptyString()
        {
            Assert.AreEqual(0, VoskNumberParser.ParseCardinal(""));
        }

        [Test]
        public void ParseCardinal_Null()
        {
            Assert.AreEqual(0, VoskNumberParser.ParseCardinal(null));
        }

        [Test]
        public void ParseCardinal_UnknownWord_Throws()
        {
            Assert.Throws<FormatException>(() => VoskNumberParser.ParseCardinal("banana"));
        }
    }
}
