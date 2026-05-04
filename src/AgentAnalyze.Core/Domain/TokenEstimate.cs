namespace AgentAnalyze.Core.Domain;

/// <summary>
/// Helpers for converting character counts to token estimates.
/// </summary>
public static class TokenEstimate
{
    /// <summary>
    /// Rough chars-per-token ratio used for portable, tokenizer-agnostic estimates.
    /// Anthropic and OpenAI tokenizers both fall in roughly the 3.5-4.5 range for English
    /// text, with code somewhat denser. 4 is a conservative middle ground for ranking.
    /// </summary>
    public const double CharsPerToken = 4.0;

    /// <summary>
    /// Estimates token count from a character count.
    /// </summary>
    public static int FromChars(int chars) => chars <= 0 ? 0 : (int)Math.Ceiling(chars / CharsPerToken);

    /// <summary>
    /// Estimates token count from a character count (long).
    /// </summary>
    public static long FromChars(long chars) => chars <= 0 ? 0 : (long)Math.Ceiling(chars / CharsPerToken);
}
