using System;
using NUnit.Framework;
using VoXR.Commands;

namespace VoXR.Tests.Runtime
{
    public class VoxrNumberParserTests
    {
        // --- ParseDigitSequence ---

        [Test]
        public void ParseDigitSequence_Zero()
        {
            Assert.AreEqual(0, VoxrNumberParser.ParseDigitSequence("zero"));
        }

        [Test]
        public void ParseDigitSequence_Nine()
        {
            Assert.AreEqual(9, VoxrNumberParser.ParseDigitSequence("nine"));
        }

        [Test]
        public void ParseDigitSequence_TwoDigits()
        {
            Assert.AreEqual(15, VoxrNumberParser.ParseDigitSequence("one five"));
        }

        [Test]
        public void ParseDigitSequence_ThreeDigits()
        {
            Assert.AreEqual(270, VoxrNumberParser.ParseDigitSequence("two seven zero"));
        }

        [Test]
        public void ParseDigitSequence_EmptyString()
        {
            Assert.AreEqual(0, VoxrNumberParser.ParseDigitSequence(""));
        }

        [Test]
        public void ParseDigitSequence_Null()
        {
            Assert.AreEqual(0, VoxrNumberParser.ParseDigitSequence(null));
        }

        [Test]
        public void ParseDigitSequence_NonDigitWord_Throws()
        {
            Assert.Throws<FormatException>(() => VoxrNumberParser.ParseDigitSequence("fifteen"));
        }

        // --- ParseCardinal ---

        [Test]
        public void ParseCardinal_Teen()
        {
            Assert.AreEqual(15, VoxrNumberParser.ParseCardinal("fifteen"));
        }

        [Test]
        public void ParseCardinal_Tens()
        {
            Assert.AreEqual(20, VoxrNumberParser.ParseCardinal("twenty"));
        }

        [Test]
        public void ParseCardinal_TensPlusUnits()
        {
            Assert.AreEqual(42, VoxrNumberParser.ParseCardinal("forty two"));
        }

        [Test]
        public void ParseCardinal_Hundred()
        {
            Assert.AreEqual(200, VoxrNumberParser.ParseCardinal("two hundred"));
        }

        [Test]
        public void ParseCardinal_HundredPlusRemainder()
        {
            Assert.AreEqual(315, VoxrNumberParser.ParseCardinal("three hundred fifteen"));
        }

        [Test]
        public void ParseCardinal_Thousand()
        {
            Assert.AreEqual(5000, VoxrNumberParser.ParseCardinal("five thousand"));
        }

        [Test]
        public void ParseCardinal_EmptyString()
        {
            Assert.AreEqual(0, VoxrNumberParser.ParseCardinal(""));
        }

        [Test]
        public void ParseCardinal_Null()
        {
            Assert.AreEqual(0, VoxrNumberParser.ParseCardinal(null));
        }

        [Test]
        public void ParseCardinal_UnknownWord_Throws()
        {
            Assert.Throws<FormatException>(() => VoxrNumberParser.ParseCardinal("banana"));
        }
    }
}
