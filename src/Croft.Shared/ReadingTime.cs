namespace Croft.Shared;

/// <summary>
/// The reading-time rule both sides share (spec §5.1: ceil(words / 220), minimum 1 minute).
/// The API derives the word count from the parsed markdown (code contributes no words); the
/// editor's preview approximates the same count from the raw markdown. The rule itself lives
/// here so the stored reading time and the preview cannot drift apart.
/// </summary>
public static class ReadingTime
{
    private const int WordsPerMinute = 220;

    /// <summary>The reading time for a text of <paramref name="words"/> words: 0 → 1, 220 → 1, 221 → 2, 660 → 3.</summary>
    public static int FromWords(int words) => Math.Max(1, (words + WordsPerMinute - 1) / WordsPerMinute);
}
