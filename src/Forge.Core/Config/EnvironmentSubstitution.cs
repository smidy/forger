using System.Text.RegularExpressions;

namespace Forge.Core.Config;

/// <summary>
/// <c>${VAR}</c> → environment variable expansion for configuration strings.
/// Undefined variables expand to the empty string so callers get a deterministic
/// "unset" signal rather than retaining literal <c>${...}</c> syntax at runtime.
/// </summary>
public static class EnvironmentSubstitution
{
  private static readonly Regex EnvVar = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

  public static string Expand(string? s)
  {
    if (string.IsNullOrEmpty(s))
    {
      return s ?? string.Empty;
    }

    return EnvVar.Replace(s, m =>
    {
      var name = m.Groups[1].Value;
      return Environment.GetEnvironmentVariable(name) ?? string.Empty;
    });
  }
}
