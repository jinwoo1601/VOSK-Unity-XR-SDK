using System;
using NUnit.Framework;
using VoskXR.Commands;

namespace VoskXR.Tests.Runtime
{
    public class VoskCommandSetTests
    {
        [Test]
        public void Constructor_StoresNameAndCommands()
        {
            var commands = new[]
            {
                new VoskCommandDefinition("test", new[] { new[] { "hello" } }),
            };

            var set = new VoskCommandSet("weapons", commands);

            Assert.AreEqual("weapons", set.Name);
            Assert.AreEqual(1, set.Commands.Length);
            Assert.AreEqual("test", set.Commands[0].Intent);
        }

        [Test]
        public void Constructor_NullName_Throws()
        {
            var commands = new[]
            {
                new VoskCommandDefinition("test", new[] { new[] { "hello" } }),
            };

            Assert.Throws<ArgumentNullException>(() =>
                new VoskCommandSet(null, commands));
        }

        [Test]
        public void Constructor_NullCommands_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new VoskCommandSet("test", null));
        }

        [Test]
        public void Constructor_DefensiveCopy_MutationSafe()
        {
            var commands = new[]
            {
                new VoskCommandDefinition("test", new[] { new[] { "hello" } }),
            };

            var set = new VoskCommandSet("weapons", commands);

            commands[0] = new VoskCommandDefinition("mutated", new[] { new[] { "world" } });

            Assert.AreEqual("test", set.Commands[0].Intent);
        }

        [Test]
        public void Constructor_EmptyCommands_Valid()
        {
            var set = new VoskCommandSet("empty", Array.Empty<VoskCommandDefinition>());

            Assert.AreEqual("empty", set.Name);
            Assert.AreEqual(0, set.Commands.Length);
        }
    }
}
