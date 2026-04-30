using System.Text.Json;
using Forge.Core.Config;
using Forge.Core.Json;

namespace Forge.Llm;

/// <summary>
/// Configuration loaded from <c>~/.forge/llm.json</c>. The <c>model_context</c>
/// map is used by context compaction to resolve per-model context limits.
/// </summary>
public sealed class LiteLlmConfig
{
  public string BaseUrl { get; set; } = "http://localhost:4001";
  public string ApiKey { get; set; } = "";
  public string DefaultModel { get; set; } = "";

  public RateLimitConfig RateLimit { get; set; } = new();

  private static readonly JsonSerializerOptions JsonOpts = JsonSerializationDefaults.LiteLlmConfig;

  /// <summary>
  /// Loads the config from <c>{forgeHome}/llm.json</c>. When the file is missing,
  /// returns a default instance (empty credentials, default base URL) rather than
  /// throwing — so fresh-machine commands like <c>forge init</c> can construct a
  /// service provider before any config exists. Callers that require a validated
  /// config (real model id, non-empty key) must check the returned object's
  /// properties explicitly; a successful <c>Load</c> call is not proof that a
  /// usable config was found on disk.
  /// </summary>
  public static LiteLlmConfig Load(string forgeHome)
  {
    var path = Path.Combine(forgeHome, "llm.json");
    if (!File.Exists(path))
    {
      return ApplyEnv(new LiteLlmConfig());
    }

    var cfg = JsonSerializer.Deserialize<LiteLlmConfig>(File.ReadAllText(path), JsonOpts) ?? new LiteLlmConfig();
    cfg.BaseUrl = EnvironmentSubstitution.Expand(cfg.BaseUrl);
    cfg.ApiKey = EnvironmentSubstitution.Expand(cfg.ApiKey);
    cfg.DefaultModel = EnvironmentSubstitution.Expand(cfg.DefaultModel);
    return ApplyEnv(cfg);
  }

  /// <summary>
  /// Per-model context-window sizes in tokens. Keyed by model identifier as it
  /// appears in agent YAML <c>model:</c> fields. Empty by default; missing
  /// entries fall back to 100_000 tokens with a one-time warning.
  /// </summary>
  public Dictionary<string, int> ModelContext { get; set; } = new();

  private static LiteLlmConfig ApplyEnv(LiteLlmConfig c)
  {
    var b = Environment.GetEnvironmentVariable("FORGE_LLM_BASE_URL");
    if (!string.IsNullOrEmpty(b))
    {
      c.BaseUrl = b.TrimEnd('/');
    }

    var k = Environment.GetEnvironmentVariable("FORGE_LLM_API_KEY");
    if (k is not null)
    {
      c.ApiKey = k;
    }

    var m = Environment.GetEnvironmentVariable("FORGE_LLM_DEFAULT_MODEL");
    if (!string.IsNullOrEmpty(m))
    {
      c.DefaultModel = m;
    }

    return c;
  }

  /// <summary>Kept as a thin wrapper for backwards compatibility; prefer
  /// <see cref="EnvironmentSubstitution.Expand(string?)"/> in new code.</summary>
  public static string EnvSubstitute(string s) => EnvironmentSubstitution.Expand(s);
}

/// <summary>
/// Rate-limit retry configuration loaded from <c>rateLimit</c> block in
/// <c>~/.forge/llm.json</c>. All fields have shipped defaults so the block
/// is entirely optional.
/// </summary>
public sealed class RateLimitConfig
{
  /// <summary>Maximum number of 429 retries before giving up. Default 3.</summary>
  public int MaxRetries { get; set; } = 3;

  /// <summary>Maximum Retry-After value (in seconds) the client will honour
  /// with a retry; larger values are surfaced immediately without retry.</summary>
  public int MaxRetryAfterSeconds { get; set; } = 60;

  /// <summary>Base backoff delay in milliseconds for exponential backoff (1 s).</summary>
  public int BaseBackoffMs { get; set; } = 1000;
}
