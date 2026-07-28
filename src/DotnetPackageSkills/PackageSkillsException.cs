namespace DotnetPackageSkills;

/// <summary>
/// A failure the user needs to act on. The message is printed verbatim without a
/// stack trace, so it must read as guidance rather than as a diagnostic.
/// </summary>
public sealed class PackageSkillsException(string message, Exception? inner = null)
    : Exception(message, inner);
