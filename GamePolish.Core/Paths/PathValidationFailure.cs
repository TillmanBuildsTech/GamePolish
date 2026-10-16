namespace GamePolish.Core.Paths;

/// <summary>One rejected path from a mod source: what came in, why, and what to tell the user.</summary>
/// <param name="Input">The path exactly as the mod source supplied it. Never trimmed or decoded.</param>
/// <param name="Reason">The rule that rejected it.</param>
/// <param name="Message">A sentence naming the input and what was expected instead.</param>
public sealed record PathValidationFailure(string Input, UnsafePathReason Reason, string Message)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Reason}: {Message}";
}
