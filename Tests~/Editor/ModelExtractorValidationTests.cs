using System.IO;
using NUnit.Framework;
using UnityEngine;
using VoXR;

namespace VoXR.Tests.Editor
{
    public class ModelExtractorValidationTests
    {
        string _testDir;

        [SetUp]
        public void SetUp()
        {
            _testDir = Path.Combine(Application.temporaryCachePath, "VoxrTestModel_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }

        [Test]
        public void ValidateModelDirectory_ValidStructure_ReturnsTrue()
        {
            CreateValidModelStructure(_testDir);
            Assert.IsTrue(ModelExtractor.ValidateModelDirectory(_testDir));
        }

        [Test]
        public void ValidateModelDirectory_MissingAmFinalMdl_ReturnsFalse()
        {
            CreateValidModelStructure(_testDir);
            File.Delete(Path.Combine(_testDir, "am", "final.mdl"));
            Assert.IsFalse(ModelExtractor.ValidateModelDirectory(_testDir));
        }

        [Test]
        public void ValidateModelDirectory_MissingConfMfccConf_ReturnsFalse()
        {
            CreateValidModelStructure(_testDir);
            File.Delete(Path.Combine(_testDir, "conf", "mfcc.conf"));
            Assert.IsFalse(ModelExtractor.ValidateModelDirectory(_testDir));
        }

        [Test]
        public void ValidateModelDirectory_MissingConfModelConf_ReturnsFalse()
        {
            CreateValidModelStructure(_testDir);
            File.Delete(Path.Combine(_testDir, "conf", "model.conf"));
            Assert.IsFalse(ModelExtractor.ValidateModelDirectory(_testDir));
        }

        [Test]
        public void ValidateModelDirectory_MissingGraphDir_ReturnsFalse()
        {
            CreateValidModelStructure(_testDir);
            Directory.Delete(Path.Combine(_testDir, "graph"), true);
            Assert.IsFalse(ModelExtractor.ValidateModelDirectory(_testDir));
        }

        [Test]
        public void ValidateModelDirectory_NonExistentPath_ReturnsFalse()
        {
            Assert.IsFalse(ModelExtractor.ValidateModelDirectory("/nonexistent/path"));
        }

        static void CreateValidModelStructure(string basePath)
        {
            Directory.CreateDirectory(Path.Combine(basePath, "am"));
            File.WriteAllText(Path.Combine(basePath, "am", "final.mdl"), "stub");

            Directory.CreateDirectory(Path.Combine(basePath, "conf"));
            File.WriteAllText(Path.Combine(basePath, "conf", "mfcc.conf"), "stub");
            File.WriteAllText(Path.Combine(basePath, "conf", "model.conf"), "stub");

            Directory.CreateDirectory(Path.Combine(basePath, "graph"));
        }
    }
}
