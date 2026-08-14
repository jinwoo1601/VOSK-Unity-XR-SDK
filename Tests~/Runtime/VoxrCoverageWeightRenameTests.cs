using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Serialization;
using VoXR.Commands;

namespace VoXR.Tests.Runtime
{
    // DR-4 (issue #65 §5.2): skippedWordPenalty became coverageWeight once the weight started
    // governing orphaned trailing tokens as well as leading skipped ones.
    //
    // The field is a private [SerializeField], so [FormerlySerializedAs] is the WHOLE of the
    // compatibility story — there is no public property or constant to forward from, and the
    // only code-level break is a named-argument call on VoxrBatchTestRunner's constructors.
    //
    // WHAT IS NOT TESTED HERE, and why. The natural test is the end-to-end one: author a
    // scene with the old key, upgrade, read the new field. It is not automatable from this
    // harness. JsonUtility matches JSON keys to field names literally and does NOT consult
    // [FormerlySerializedAs] — measured, not assumed: feeding it {"skippedWordPenalty":0.25}
    // leaves the field at its default rather than at 0.25. Only Unity's native YAML
    // serializer honours the attribute, which needs a real asset round-trip through
    // AssetDatabase in a host project, not a unit test.
    //
    // So the coverage below is deliberately split: that Unity honours the attribute is a
    // platform contract this package does not re-verify, while that WE DECLARED IT CORRECTLY
    // is ours and is pinned. That is not a hypothetical distinction — a blanket rename during
    // this change rewrote the attribute's argument to the new name, which would have silently
    // destroyed every upgrading project's tuning while leaving the shim looking present.
    public class VoxrCoverageWeightRenameTests
    {
        const string OldFieldName = "skippedWordPenalty";
        const string NewFieldName = "coverageWeight";

        GameObject _go;
        VoxrCommandRecogniser _recogniser;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestCoverageWeightRename");
            _recogniser = _go.AddComponent<VoxrCommandRecogniser>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                UnityEngine.Object.DestroyImmediate(_go);
        }

        static FieldInfo CoverageWeightField()
        {
            var field = typeof(VoxrCommandRecogniser).GetField(
                NewFieldName,
                BindingFlags.Instance | BindingFlags.NonPublic
            );

            Assert.IsNotNull(field, $"the serialized field must be named {NewFieldName}");
            return field;
        }

        [Test]
        public void CoverageWeight_CarriesFormerlySerializedAsTheOldName()
        {
            // The upgrade path in one assertion: the attribute must be present AND must name
            // the OLD field. An attribute naming the new field compiles, looks right in a
            // diff, and does nothing.
            var attribute = CoverageWeightField()
                .GetCustomAttribute<FormerlySerializedAsAttribute>();

            Assert.IsNotNull(
                attribute,
                "without it, every project that tuned the old field silently resets to default"
            );
            Assert.AreEqual(
                OldFieldName,
                attribute.oldName,
                "it has to name the field being migrated FROM, not the current one"
            );
        }

        [Test]
        public void CoverageWeight_IsSerializedUnderTheNewName()
        {
            // Guards the other half of the pair: the attribute is only worth anything if the
            // field is actually serialized under the new name.
            JsonUtility.FromJsonOverwrite($"{{\"{NewFieldName}\":0.75}}", _recogniser);

            Assert.AreEqual(0.75f, (float)CoverageWeightField().GetValue(_recogniser), 0.0001f);
        }

        [Test]
        public void CoverageWeight_DefaultsToTheParserConstant()
        {
            Assert.AreEqual(
                VoxrCommandParser.DefaultCoverageWeight,
                (float)CoverageWeightField().GetValue(_recogniser),
                0.0001f,
                "an unauthored field still starts at the documented default"
            );
        }
    }
}
