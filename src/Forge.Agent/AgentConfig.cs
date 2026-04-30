using System.Text.Json.Nodes;
using Forge.Core.Config;
using Forge.Core.Exceptions;
using Forge.Core.Filesystem;
using Forge.Core.Json;

namespace Forge.Agent;

// Optional LiteLLM reasoning knobs. Both fields pass through verbatim so
// provider-specific handling stays in LiteLLM, not Forge. Either/both may be
// set; the block itself is optional. See docs/plans/agent-reasoning.md.
public sealed class AgentReasoningConfig
{
  public string? Effort { get; init; }                // "low" | "medium" | "high" | null
  public int? ThinkingBudgetTokens { get; init; }     // Anthropic-only; >= 1024 when set
}

// Opt-out knobs for the post-loop diff verification (see
// docs/plans/agent-diff-verification.md). Defaults enforce reconciliation
// against the write ledger for every agent so drift is a loud migration;
// research-style agents that legitimately produce only run-workspace
// artefacts set `allow_runspace_only: true`.
public sealed class AgentDiffVerificationConfig
{
  public bool Enabled { get; init; } = true;
  public bool AllowRunspaceOnly { get; init; } = false;
}

/// <summary>
/// Configuration for per-iteration context compaction. When enabled, the agent
/// loop rewrites stale tool results into disk-backed stubs to keep the message
/// list under the model's context limit. See <c>docs/plans/context-auto-compaction.md</c>.
/// </summary>
public sealed class AgentCompactionConfig
{
  /// <summary>When true, compaction runs before each LLM call.</summary>
  public bool Enabled { get; init; } = false;

  /// <summary>
  /// Compaction strategy. The only recognised value in v1 is
  /// <c>"trim_tool_results"</c>. Unknown strategies throw at parse time.
  /// </summary>
  public string Strategy { get; init; } = "trim_tool_results";

  /// <summary>
  /// Absolute token threshold. When set, this takes priority over
  /// <c>PctOfModelLimit</c>.
  /// </summary>
  public int? TokenThreshold { get; init; }

  /// <summary>
  /// Fraction of the model's context limit at which compaction triggers.
  /// Default 0.75 (75%) when <c>TokenThreshold</c> is not set.
  /// </summary>
  public double? PctOfModelLimit { get; init; }

  /// <summary>Number of most recent iterations always preserved verbatim.</summary>
  public int KeepRecentIterations { get; init; } = 3;

  /// <summary>
  /// Model used for the <c>summarise</c> strategy's LLM summariser call.
  /// When <c>null</c> (default), falls back to <c>config.Model</c>.
  /// </summary>
  public string? SummarisationModel { get; init; }

  /// <summary>
  /// For the <c>window</c> strategy: number of oldest iteration groups to drop
  /// from the compaction set. When <c>null</c> (default), all groups are dropped.
  /// </summary>
  public int? DropOldestIterations { get; init; }
}

public sealed class AgentConfig
{
  public required string Name { get; init; }
  public required string Model { get; init; }
  public required string SystemPrompt { get; init; }
  public required string UserPrompt { get; init; }
  public int MaxIterations { get; init; } = 40;
  public List<string> Tools { get; init; } = new();
  public required JsonNode InputSchema { get; init; }
  public JsonNode? OutputSchema { get; init; }

  /// <summary>When true (default), load <c>AGENTS.md</c> / <c>CLAUDE.md</c> from cwd, agent dir, and <see cref="ProjectContextRoots"/>.</summary>
  public bool InjectProjectContext { get; init; } = true;

  /// <summary>When true (default), append a skills catalog (name + description) and grant read access to default skill directories.</summary>
  public bool InjectSkillsCatalog { get; init; } = true;

  /// <summary>Extra base paths (after cwd and agent YAML directory) for project markdown.</summary>
  public List<string> ProjectContextRoots { get; init; } = new();

  /// <summary>Optional reasoning controls forwarded to the LiteLLM proxy. Null when absent.</summary>
  public AgentReasoningConfig? Reasoning { get; init; }

  /// <summary>
  /// Optional post-loop diff-verification controls. Null means "use defaults"
  /// (enabled, do not allow runspace-only completions).
  /// </summary>
  public AgentDiffVerificationConfig? DiffVerification { get; init; }

  /// <summary>
  /// Optional <c>bash</c> tool configuration. Required whenever
  /// <see cref="Tools"/> lists <c>bash</c>; see <c>docs/plans/bash-tool.md</c>.
  /// </summary>
  public BashConfig? Bash { get; init; }

  /// <summary>Optional context-compaction configuration. Null when absent.</summary>
  public AgentCompactionConfig? Compaction { get; init; }

  /// <summary>Optional caller-IO budget/behaviour configuration. Uses defaults when null.</summary>
  public CallerIoConfig? CallerIo { get; init; }

  public IReadOnlyList<string>? GetProjectMarkdownReadRoots(string? agentYamlPath) =>
    InjectProjectContext ? ProjectContextPaths.GetOrderedRoots(agentYamlPath, ProjectContextRoots) : null;

  public static AgentConfig FromJsonNode(JsonNode node)
  {
    var o = node.AsObject();
    var tools = JsonNodeHelpers.ListStr(o["tools"]);
    var bash = ParseBash(o["bash"]);
    EnforceBashToolLink(tools, bash, o.ContainsKey("bash"));
    return new AgentConfig
    {
      Name = JsonNodeHelpers.Str(o["name"]),
      // `model` supports `${ENV_VAR}` expansion so bundled example YAMLs can
      // reference `${FORGE_LLM_DEFAULT_MODEL}` without per-machine hand-edits.
      Model = EnvironmentSubstitution.Expand(JsonNodeHelpers.Str(o["model"])),
      SystemPrompt = JsonNodeHelpers.Str(o["system_prompt"]),
      UserPrompt = JsonNodeHelpers.Str(o["user_prompt"]),
      MaxIterations = JsonNodeHelpers.Int(o["max_iterations"]) ?? 40,
      Tools = tools,
      InputSchema = o["input_schema"]?.DeepClone() ?? new JsonObject(),
      OutputSchema = o["output_schema"] switch
      {
        null => null,
        JsonValue v when v.GetValue<object?>() is null => null,
        var n => n.DeepClone()
      },
      InjectProjectContext = JsonNodeHelpers.Bool(o["inject_project_context"], defaultValue: true),
      InjectSkillsCatalog = JsonNodeHelpers.Bool(o["inject_skills_catalog"], defaultValue: true),
      ProjectContextRoots = JsonNodeHelpers.ListStr(o["project_context_roots"]),
      Reasoning = ParseReasoning(o["reasoning"]),
      DiffVerification = ParseDiffVerification(o["diff_verification"]),
      Bash = bash,
      Compaction = ParseCompaction(o["compaction"]),
      CallerIo = ParseCallerIo(o["caller_io"])
    };
  }

  public static AgentConfig LoadFromYamlFile(string path)
  {
    var text = File.ReadAllText(path);
    var json = YamlFront.ParseToJson(text);
    return FromJsonNode(json);
  }

  private static AgentReasoningConfig? ParseReasoning(JsonNode? node)
  {
    if (node is null)
    {
      return null;
    }

    if (node is not JsonObject obj)
    {
      throw new ConfigException("`reasoning` must be a mapping with `effort` and/or `thinking_budget_tokens` keys.");
    }

    var effort = JsonNodeHelpers.NullableStr(obj["effort"]);
    if (effort is not null)
    {
      effort = effort.ToLowerInvariant();
      if (effort is not ("low" or "medium" or "high"))
      {
        throw new ConfigException($"`reasoning.effort` must be one of: low, medium, high. Got: {effort}.");
      }
    }

    var budget = JsonNodeHelpers.Int(obj["thinking_budget_tokens"]);
    if (budget is int b && b < 1024)
    {
      throw new ConfigException($"`reasoning.thinking_budget_tokens` must be >= 1024 (Anthropic lower bound). Got: {b}.");
    }

    if (effort is null && budget is null)
    {
      throw new ConfigException("`reasoning` block must set at least one of `effort` or `thinking_budget_tokens`.");
    }

    return new AgentReasoningConfig
    {
      Effort = effort,
      ThinkingBudgetTokens = budget
    };
  }

  private static AgentDiffVerificationConfig? ParseDiffVerification(JsonNode? node)
  {
    if (node is null)
    {
      return null;
    }

    if (node is not JsonObject obj)
    {
      throw new ConfigException("`diff_verification` must be a mapping with `enabled` and/or `allow_runspace_only` keys.");
    }

    return new AgentDiffVerificationConfig
    {
      Enabled = JsonNodeHelpers.Bool(obj["enabled"], defaultValue: true),
      AllowRunspaceOnly = JsonNodeHelpers.Bool(obj["allow_runspace_only"], defaultValue: false)
    };
  }

  private static readonly HashSet<string> CompactionKnownKeys = new(StringComparer.Ordinal)
  {
    "enabled", "strategy", "token_threshold", "pct_of_model_limit",
    "keep_recent_iterations", "summarisation_model", "drop_oldest_iterations"
  };

  private static readonly HashSet<string> CompactionKnownStrategies = new(StringComparer.OrdinalIgnoreCase)
  {
    "trim_tool_results",
    "summarise", "window"
  };

  private static AgentCompactionConfig? ParseCompaction(JsonNode? node)
  {
    if (node is null) return null;

    if (node is not JsonObject obj)
    {
      throw new ConfigException("`compaction` must be a mapping with `enabled`, `strategy`, `token_threshold`, `pct_of_model_limit`, and/or `keep_recent_iterations` keys.");
    }

    foreach (var kv in obj)
    {
      if (!CompactionKnownKeys.Contains(kv.Key))
      {
        var allowed = string.Join(", ", CompactionKnownKeys.OrderBy(s => s));
        throw new ConfigException($"`compaction.{kv.Key}` is not a recognised key. Allowed: {allowed}.");
      }
    }

    var strategy = JsonNodeHelpers.NullableStr(obj["strategy"]) ?? "trim_tool_results";
    if (!CompactionKnownStrategies.Contains(strategy))
    {
      var allowed = string.Join(", ", CompactionKnownStrategies.OrderBy(s => s));
      throw new ConfigException($"`compaction.strategy` must be one of: {allowed}. Got: `{strategy}`.");
    }

    var pctOfModelLimit = ParsePctOfModelLimit(obj["pct_of_model_limit"]);
    if (pctOfModelLimit is double p && p is <= 0 or > 1)
    {
      throw new ConfigException($"`compaction.pct_of_model_limit` must be > 0 and <= 1. Got: {p}.");
    }

    var keepRecent = JsonNodeHelpers.Int(obj["keep_recent_iterations"]) ?? 3;
    if (keepRecent < 1)
    {
      throw new ConfigException($"`compaction.keep_recent_iterations` must be >= 1. Got: {keepRecent}.");
    }

    var summarisationModel = JsonNodeHelpers.NullableStr(obj["summarisation_model"]);

    var dropOldest = JsonNodeHelpers.Int(obj["drop_oldest_iterations"]);
    if (dropOldest is int d && d < 1)
    {
      throw new ConfigException(
        $"`compaction.drop_oldest_iterations` must be >= 1 or null. Got: {d}.");
    }

    return new AgentCompactionConfig
    {
      Enabled = JsonNodeHelpers.Bool(obj["enabled"], defaultValue: false),
      Strategy = strategy.Trim().ToLowerInvariant(),
      TokenThreshold = JsonNodeHelpers.Int(obj["token_threshold"]),
      PctOfModelLimit = pctOfModelLimit,
      KeepRecentIterations = keepRecent,
      SummarisationModel = summarisationModel,
      DropOldestIterations = dropOldest
    };
  }

  private static double? ParsePctOfModelLimit(JsonNode? node)
  {
    if (node is null) return null;
    if (node is JsonValue v)
    {
      if (v.TryGetValue(out double d)) return d;
      if (v.TryGetValue(out decimal m)) return (double)m;
      if (v.TryGetValue(out float f)) return f;
      if (v.TryGetValue(out int i)) return i;
    }
    throw new ConfigException("`compaction.pct_of_model_limit` must be a number between 0 and 1.");
  }

  private static void EnforceBashToolLink(IReadOnlyList<string> tools, BashConfig? bash, bool bashKeyPresent)
  {
    var toolsHasBash = false;
    foreach (var t in tools)
    {
      if (string.Equals(t, "bash", StringComparison.Ordinal))
      {
        toolsHasBash = true;
        break;
      }
    }

    if (toolsHasBash && !bashKeyPresent)
    {
      throw new ConfigException(
        "`tools` lists `bash` but no `bash:` block is declared. Add a `bash:` config block with at least an `image:` digest, or remove `bash` from `tools`.");
    }

    // bash: present but tool not listed is a soft case — the tool simply never
    // fires. The Agent loader reports it; we do not reject at parse time so
    // operators can stage config before flipping the tool on.
    _ = bash;
  }

  private static BashConfig? ParseBash(JsonNode? node)
  {
    if (node is null)
    {
      return null;
    }

    if (node is not JsonObject obj)
    {
      throw new ConfigException("`bash` must be a mapping — see docs/plans/bash-tool.md for the schema.");
    }

    foreach (var forbidden in BashConfig.ForbiddenKeys)
    {
      if (obj.ContainsKey(forbidden))
      {
        throw new ConfigException(
          $"`bash.{forbidden}` is not permitted — it would weaken the sandbox. See docs/plans/bash-tool.md §Security posture.");
      }
    }

    var image = JsonNodeHelpers.NullableStr(obj["image"]);
    if (string.IsNullOrWhiteSpace(image))
    {
      throw new ConfigException("`bash.image` is required. Provide a digest-pinned ref, e.g. `forge-bash@sha256:...` (RepoDigest) or `sha256:...` (local image ID).");
    }
    var hasRepoDigest = image.Contains("@sha256:", StringComparison.Ordinal);
    var hasBareImageId = image.StartsWith("sha256:", StringComparison.Ordinal);
    if (!hasRepoDigest && !hasBareImageId)
    {
      throw new ConfigException(
        $"`bash.image` must be digest-pinned (must contain `@sha256:` or start with `sha256:`). Got: `{image}`. Forge never runs `docker pull` implicitly.");
    }

    var network = JsonNodeHelpers.NullableStr(obj["network"]) ?? "none";
    if (network is not ("none" or "bridge"))
    {
      throw new ConfigException($"`bash.network` must be `none` or `bridge`. Got: `{network}`.");
    }

    var timeout = JsonNodeHelpers.Int(obj["timeout_sec"]) ?? 30;
    if (timeout < 1 || timeout > 300)
    {
      throw new ConfigException($"`bash.timeout_sec` must be between 1 and 300. Got: {timeout}.");
    }

    var user = JsonNodeHelpers.NullableStr(obj["user"]) ?? "1000:1000";
    if (IsRootUser(user))
    {
      throw new ConfigException($"`bash.user` must not resolve to UID 0 (root). Got: `{user}`.");
    }

    var cpus = ParseCpus(obj["cpus"]);
    var pids = JsonNodeHelpers.Int(obj["pids_limit"]) ?? 100;
    if (pids < 1)
    {
      throw new ConfigException($"`bash.pids_limit` must be >= 1. Got: {pids}.");
    }

    var envAllow = JsonNodeHelpers.ListStr(obj["env_allow"]);
    var env = ParseEnv(obj["env"], envAllow);

    return new BashConfig
    {
      Image = image,
      Platform = JsonNodeHelpers.NullableStr(obj["platform"]) ?? "linux/amd64",
      Network = network,
      TimeoutSec = timeout,
      Memory = JsonNodeHelpers.NullableStr(obj["memory"]) ?? "512m",
      Cpus = cpus,
      PidsLimit = pids,
      StorageOpt = JsonNodeHelpers.NullableStr(obj["storage_opt"]) ?? "",
      TmpfsSize = JsonNodeHelpers.NullableStr(obj["tmpfs_size"]) ?? "512m",
      User = user,
      ReadOnlyRoot = JsonNodeHelpers.Bool(obj["read_only_root"], defaultValue: false),
      EnvAllow = envAllow,
      Env = env,
      Diff = ParseBashDiff(obj["diff"]),
      ShowMountTable = JsonNodeHelpers.Bool(obj["show_mount_table"], defaultValue: true),
      AutoScratch = JsonNodeHelpers.Bool(obj["auto_scratch"], defaultValue: true),
      ExposeGit = JsonNodeHelpers.Bool(obj["expose_git"], defaultValue: false),
      Rootless = ParseRootlessMode(obj["rootless"]),
      Mounts = ParseBashMounts(obj["mounts"])
    };
  }

  private static BashRootlessMode ParseRootlessMode(JsonNode? node)
  {
    if (node is null)
    {
      return BashRootlessMode.Auto;
    }

    var raw = JsonNodeHelpers.NullableStr(node);
    if (string.IsNullOrWhiteSpace(raw))
    {
      return BashRootlessMode.Auto;
    }

    return raw.Trim().ToLowerInvariant() switch
    {
      "auto" => BashRootlessMode.Auto,
      "required" => BashRootlessMode.Required,
      "forbidden" => BashRootlessMode.Forbidden,
      _ => throw new ConfigException(
        $"`bash.rootless` must be one of `auto`, `required`, `forbidden`. Got: `{raw}`. Plan: docs/plans/bash-tool-rootless-docker.md.")
    };
  }

  private static IReadOnlyList<BashMount> ParseBashMounts(JsonNode? node)
  {
    if (node is null)
    {
      return Array.Empty<BashMount>();
    }

    if (node is not JsonArray arr)
    {
      throw new ConfigException("`bash.mounts` must be a list of `{host, container, mode}` entries.");
    }

    var seen = new HashSet<string>(StringComparer.Ordinal);
    var result = new List<BashMount>(arr.Count);
    for (var i = 0; i < arr.Count; i++)
    {
      var entry = arr[i];
      if (entry is not JsonObject obj)
      {
        throw new ConfigException($"`bash.mounts[{i}]` must be a mapping with `host`, `container`, and `mode` keys.");
      }

      var host = JsonNodeHelpers.NullableStr(obj["host"]);
      if (string.IsNullOrWhiteSpace(host))
      {
        throw new ConfigException($"`bash.mounts[{i}].host` is required and must be a non-empty path.");
      }

      var container = JsonNodeHelpers.NullableStr(obj["container"]);
      if (string.IsNullOrWhiteSpace(container))
      {
        throw new ConfigException($"`bash.mounts[{i}].container` is required and must be a non-empty path.");
      }

      if (!container.StartsWith('/'))
      {
        throw new ConfigException($"`bash.mounts[{i}].container` must start with `/`. Got: `{container}`.");
      }

      if (string.Equals(container, "/run", StringComparison.Ordinal)
          || container.StartsWith("/run/", StringComparison.Ordinal))
      {
        throw new ConfigException($"`bash.mounts[{i}].container` cannot be `/run` or a sub-path of `/run` — that path is reserved for the per-run workspace.");
      }

      if (!seen.Add(container))
      {
        throw new ConfigException($"`bash.mounts[{i}].container` `{container}` is declared more than once.");
      }

      var modeRaw = JsonNodeHelpers.NullableStr(obj["mode"]) ?? "rw";
      var mode = modeRaw.Trim().ToLowerInvariant() switch
      {
        "ro" => BashMountMode.ReadOnly,
        "rw" => BashMountMode.ReadWrite,
        _ => throw new ConfigException($"`bash.mounts[{i}].mode` must be `ro` or `rw`. Got: `{modeRaw}`.")
      };

      result.Add(new BashMount(host, container, mode));
    }

    return result;
  }

  private static BashDiffConfig? ParseBashDiff(JsonNode? node)
  {
    if (node is null)
    {
      return null;
    }

    if (node is not JsonObject obj)
    {
      throw new ConfigException("`bash.diff` must be a mapping with `max_files` / `max_depth` / `max_hash_bytes` keys.");
    }

    var maxFiles = JsonNodeHelpers.Int(obj["max_files"]) ?? 10_000;
    var maxDepth = JsonNodeHelpers.Int(obj["max_depth"]) ?? 16;
    var maxHashBytes = JsonNodeHelpers.Int(obj["max_hash_bytes"]) ?? (4 * 1024 * 1024);
    if (maxFiles < 1 || maxDepth < 1 || maxHashBytes < 1)
    {
      throw new ConfigException("`bash.diff.*` values must be >= 1.");
    }

    return new BashDiffConfig
    {
      MaxFiles = maxFiles,
      MaxDepth = maxDepth,
      MaxHashBytes = maxHashBytes
    };
  }

  private static double ParseCpus(JsonNode? node)
  {
    if (node is null)
    {
      return 1.0;
    }

    if (node is JsonValue v)
    {
      if (v.TryGetValue(out double d))
      {
        return d > 0 ? d : throw new ConfigException($"`bash.cpus` must be positive. Got: {d}.");
      }

      if (v.TryGetValue(out int i))
      {
        return i > 0 ? i : throw new ConfigException($"`bash.cpus` must be positive. Got: {i}.");
      }

      if (v.TryGetValue(out decimal dc))
      {
        var x = (double)dc;
        return x > 0 ? x : throw new ConfigException($"`bash.cpus` must be positive. Got: {x}.");
      }
    }

    throw new ConfigException("`bash.cpus` must be a positive number.");
  }

  private static IReadOnlyDictionary<string, string> ParseEnv(JsonNode? node, IReadOnlyList<string> envAllow)
  {
    if (node is null)
    {
      return new Dictionary<string, string>();
    }

    if (node is not JsonObject obj)
    {
      throw new ConfigException("`bash.env` must be a mapping of string → string.");
    }

    var allow = new HashSet<string>(envAllow, StringComparer.Ordinal);
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var kv in obj)
    {
      var key = kv.Key;
      if (BashConfig.ForbiddenEnvPattern.IsMatch(key))
      {
        throw new ConfigException(
          $"`bash.env.{key}` is forbidden (matches `PATH|LD_*|DYLD_*|NODE_OPTIONS|PYTHONPATH`). These keys would leak host toolchain state into the sandbox.");
      }

      if (!allow.Contains(key))
      {
        throw new ConfigException(
          $"`bash.env.{key}` is not listed in `bash.env_allow`. Every env key must appear in the allowlist.");
      }

      var value = JsonNodeHelpers.NullableStr(kv.Value) ?? "";
      result[key] = value;
    }

    return result;
  }

  private static bool IsRootUser(string user)
  {
    var trimmed = user.Trim();
    if (trimmed.Length == 0)
    {
      return true;
    }

    var colonIdx = trimmed.IndexOf(':');
    var uidPart = colonIdx >= 0 ? trimmed.AsSpan(0, colonIdx) : trimmed.AsSpan();
    if (uidPart.Length == 0)
    {
      return true;
    }

    if (int.TryParse(uidPart, out var uid))
    {
      return uid == 0;
    }

    return string.Equals(uidPart.ToString(), "root", StringComparison.OrdinalIgnoreCase);
  }

  private static readonly HashSet<string> CallerIoKnownKeys = new(StringComparer.Ordinal)
  {
    "max_prompts", "max_notifications", "max_approvals", "on_budget_exceeded"
  };

  private static CallerIoConfig? ParseCallerIo(JsonNode? node)
  {
    if (node is null) return null;

    if (node is not JsonObject obj)
    {
      throw new ConfigException("`caller_io` must be a mapping with `max_prompts`, `max_notifications`, `max_approvals`, and/or `on_budget_exceeded` keys.");
    }

    foreach (var kv in obj)
    {
      if (!CallerIoKnownKeys.Contains(kv.Key))
      {
        var allowed = string.Join(", ", CallerIoKnownKeys.OrderBy(s => s));
        throw new ConfigException($"`caller_io.{kv.Key}` is not a recognised key. Allowed: {allowed}.");
      }
    }

    var maxPrompts = JsonNodeHelpers.Int(obj["max_prompts"]) ?? 5;
    var maxNotifications = JsonNodeHelpers.Int(obj["max_notifications"]) ?? 50;
    var maxApprovals = JsonNodeHelpers.Int(obj["max_approvals"]) ?? 10;
    var onBudgetExceeded = JsonNodeHelpers.NullableStr(obj["on_budget_exceeded"]) ?? "error";

    if (maxPrompts < 0) throw new ConfigException("`caller_io.max_prompts` must be >= 0.");
    if (maxNotifications < 0) throw new ConfigException("`caller_io.max_notifications` must be >= 0.");
    if (maxApprovals < 0) throw new ConfigException("`caller_io.max_approvals` must be >= 0.");
    if (onBudgetExceeded is not "error" and not "silent")
    {
      throw new ConfigException($"`caller_io.on_budget_exceeded` must be `error` or `silent`. Got: `{onBudgetExceeded}`.");
    }

    return new CallerIoConfig { MaxPrompts = maxPrompts, MaxNotifications = maxNotifications, MaxApprovals = maxApprovals, OnBudgetExceeded = onBudgetExceeded.ToLowerInvariant() };
  }
}
