using System.Text.Json.Nodes;
using Forge.Core.Exceptions;
using Forge.Core.Types;

namespace Forge.Cli;

/// <summary>
/// Resolves the caller policy from the <c>--callers</c> CLI flag, falling
/// through <c>~/.forge/callers.json</c> to the built-in default.
/// </summary>
internal static class CallerPolicyParser
{
  /// <summary>
  /// Resolve the effective <see cref="CallerPolicy"/>. Returns null when
  /// no <c>--callers</c> flag was passed (caller should decide transport
  /// based on TTY state).
  /// </summary>
  public static CallerPolicy? Resolve(string? flagValue)
  {
    if (string.IsNullOrWhiteSpace(flagValue))
    {
      // Load from ~/.forge/callers.json, or null if absent
      return TryLoadFromUserConfig();
    }

    // Preset names
    switch (flagValue.Trim().ToLowerInvariant())
    {
      case "auto-allow":
        return CallerPolicy.AutoAllow;
      case "auto-deny":
        return CallerPolicy.AutoDeny;
      case "fail-fast":
        return CallerPolicy.FailFast;
    }

    // File path
    if (File.Exists(flagValue))
    {
      return LoadFromFile(flagValue);
    }

    throw new ConfigException(
      $"Unknown --callers value: '{flagValue}'. Expected 'auto-allow', 'auto-deny', 'fail-fast', or a path to a JSON policy file.");
  }

  private static CallerPolicy? TryLoadFromUserConfig()
  {
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var path = Path.Combine(home, ".forge", "callers.json");
    if (!File.Exists(path))
    {
      return null;
    }

    return LoadFromFile(path);
  }

  private static CallerPolicy LoadFromFile(string path)
  {
    var text = File.ReadAllText(path);
    var node = JsonNode.Parse(text) as JsonObject
      ?? throw new ConfigException($"Caller policy file '{path}' is not a valid JSON object.");

    var onPromptStr = node["on_prompt"]?.GetValue<string>() ?? "defer";
    var onApprovalStr = node["on_approval"]?.GetValue<string>() ?? "auto_deny";
    var emitToStderr = node["emit_notifications_to_stderr"]?.GetValue<bool>() ?? true;

    var onPrompt = onPromptStr.ToLowerInvariant() switch
    {
      "defer" => PromptBehavior.Defer,
      "fail_fast" or "failfast" => PromptBehavior.FailFast,
      "silent_empty" or "silentempty" => PromptBehavior.SilentEmpty,
      _ => throw new ConfigException($"Unknown on_prompt: '{onPromptStr}'. Expected 'defer', 'fail_fast', or 'silent_empty'.")
    };

    var onApproval = onApprovalStr.ToLowerInvariant() switch
    {
      "auto_deny" or "autodeny" => ApprovalBehavior.AutoDeny,
      "auto_allow" or "autoallow" => ApprovalBehavior.AutoAllow,
      "per_action" or "peraction" => ApprovalBehavior.PerAction,
      _ => throw new ConfigException($"Unknown on_approval: '{onApprovalStr}'. Expected 'auto_deny', 'auto_allow', or 'per_action'.")
    };

    var perAction = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    if (node["per_action"] is JsonObject pa)
    {
      foreach (var kv in pa)
      {
        if (kv.Value is JsonValue v && v.TryGetValue(out bool b))
        {
          perAction[kv.Key] = b;
        }
        else
        {
          throw new ConfigException($"per_action.{kv.Key} must be a boolean.");
        }
      }
    }

    return new CallerPolicy
    {
      OnPrompt = onPrompt,
      OnApproval = onApproval,
      PerAction = perAction,
      EmitNotificationsToStderr = emitToStderr
    };
  }
}
