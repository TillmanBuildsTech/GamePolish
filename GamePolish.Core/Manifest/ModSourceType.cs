namespace GamePolish.Core.Manifest;

/// <summary>Where an installed mod came from.</summary>
/// <remarks>
/// The trust model is invariant #5: GitHub repos and user-supplied ZIPs, nothing else. A third value
/// here would need a change to that promise, not just a new enum member.
/// </remarks>
public enum ModSourceType
{
    /// <summary>A GitHub repository, cloned shallow into its addon folder.</summary>
    Github = 0,

    /// <summary>A ZIP the user supplied; <c>SourceUrl</c> holds its original file name.</summary>
    Zip = 1,
}
