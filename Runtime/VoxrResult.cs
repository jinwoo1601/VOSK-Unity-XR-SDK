// ============================================================================
// Purpose:  Data structs for speech recognition results (word, result)
// Layer:    Runtime
// Owns:     VoxrWord, VoxrResult (public readonly structs)
// Depends:  (none)
// ============================================================================
namespace VoXR
{
    public readonly struct VoxrWord
    {
        public readonly string Text;

        public readonly float Confidence;

        public readonly float StartTime;

        public readonly float EndTime;

        public VoxrWord(string text, float confidence, float startTime, float endTime)
        {
            Text = text;
            Confidence = confidence;
            StartTime = startTime;
            EndTime = endTime;
        }

        public override string ToString() => $"{Text} ({Confidence:F2})";
    }

    public readonly struct VoxrResult
    {
        public readonly string Text;

        public readonly VoxrWord[] Words;

        public VoxrResult(string text, VoxrWord[] words)
        {
            Text = text;
            Words = words;
        }
    }
}
