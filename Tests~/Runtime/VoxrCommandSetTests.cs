using System;
using NUnit.Framework;
using VoXR.Commands;

namespace VoXR.Tests.Runtime
{
    public class VoxrCommandSetTests
    {
        [Test]
        public void Constructor_StoresNameAndCommands()
        {
            var commands = new[]
            {
                new VoxrCommandDefinition("test", new[] { new[] { "hello" } }),
            };

            var set = new VoxrCommandSet("weapons", commands);

            Assert.AreEqual("weapons", set.Name);
            Assert.AreEqual(1, set.Commands.Length);
            Assert.AreEqual("test", set.Commands[0].Intent);
        }

        [Test]
        public void Constructor_NullName_Throws()
        {
            var commands = new[]
            {
                new VoxrCommandDefinition("test", new[] { new[] { "hello" } }),
            };

            Assert.Throws<ArgumentNullException>(() =>
                new VoxrCommandSet(null, commands));
        }

        [Test]
        public void Constructor_NullCommands_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new VoxrCommandSet("test", null));
        }

        [Test]
        public void Constructor_DefensiveCopy_MutationSafe()
        {
            var commands = new[]
            {
                new VoxrCommandDefinition("test", new[] { new[] { "hello" } }),
            };

            var set = new VoxrCommandSet("weapons", commands);

            commands[0] = new VoxrCommandDefinition("mutated", new[] { new[] { "world" } });

            Assert.AreEqual("test", set.Commands[0].Intent);
        }

        [Test]
        public void Constructor_EmptyCommands_Valid()
        {
            var set = new VoxrCommandSet("empty", Array.Empty<VoxrCommandDefinition>());

            Assert.AreEqual("empty", set.Name);
            Assert.AreEqual(0, set.Commands.Length);
        }
    }
}
