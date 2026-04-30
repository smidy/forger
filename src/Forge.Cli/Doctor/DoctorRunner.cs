using System.Text.Json.Nodes;
using Forge.Agent;
using Forge.Core.Config;
using Forge.Llm;
using Forge.Pipeline;
using Forge.Tools.Docker;

namespace Forge.Cli.Doctor;

/// <summary>
/// Runs every <c>forge doctor</c> health check in order. All checks run
/// regardless of failures; the runner never short-circuits.
/// </summary>
internal static class DoctorRunner
{
  private static readonly string[] PlaceholderModelPatterns =
    ["replace-with-your-model-id", "your-model", "replace-me"];

  /// <summary>
  /// Execute all checks against the current environment.
  /// </summary>
  public static async Task<DoctorReport> RunAsync(
    string forgeHome,
    bool probeEndpoint,
    CancellationToken cancellationToken = default)
  {
    var version = typeof(DoctorRunner).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    var checks = new List<DoctorCheck>();
    int hardFailures = 0;
    int warnings = 0;

    // 1. dotnet.runtime
    checks.Add(CheckDotnetRuntime());
    // 2. forge.version
    checks.Add(CheckForgeVersion(forgeHome));

    // 3. home.exists
    var homeCheck = CheckHomeExists(forgeHome);
    checks.Add(homeCheck);
    bool homeOk = homeCheck.Status == "ok";

    // 4-8. LLM config checks (only if home exists)
    LiteLlmConfig? llmCfg = null;
    bool llmConfigPresent = false;
    if (homeOk)
    {
      var presentCheck = CheckLlmConfigPresent(forgeHome);
      checks.Add(presentCheck);
      llmConfigPresent = presentCheck.Status == "ok";

      if (llmConfigPresent)
      {
        var (parseCheck, cfg) = CheckLlmConfigParseable(forgeHome);
        checks.Add(parseCheck);
        llmCfg = cfg;

        if (llmCfg is not null)
        {
          checks.Add(CheckLlmBaseUrlV1Suffix(llmCfg));
          checks.Add(CheckLlmDefaultModelSet(llmCfg));
          checks.Add(CheckLlmApiKeyResolvable(forgeHome));
        }
      }
      else
      {
        // Skip parse-dependent checks
        checks.Add(MakeCheck("llm.config.parseable", "LLM config parseable", "skip", false, "Config file not present", null));
        checks.Add(MakeCheck("llm.baseurl.v1suffix", "LLM baseUrl ends with /v1", "skip", true, "Skipped because llm.json is missing", null));
        checks.Add(MakeCheck("llm.defaultmodel.set", "LLM default model set", "skip", true, "Skipped because llm.json is missing", null));
        checks.Add(MakeCheck("llm.apikey.resolvable", "LLM API key resolvable", "skip", false, "Skipped because llm.json is missing", null));
      }
    }
    else
    {
      checks.Add(MakeCheck("llm.config.present", "LLM config present", "skip", true, "Skipped because ~/.forge/ does not exist", null));
      checks.Add(MakeCheck("llm.config.parseable", "LLM config parseable", "skip", true, "Skipped because ~/.forge/ does not exist", null));
      checks.Add(MakeCheck("llm.baseurl.v1suffix", "LLM baseUrl ends with /v1", "skip", true, "Skipped because ~/.forge/ does not exist", null));
      checks.Add(MakeCheck("llm.defaultmodel.set", "LLM default model set", "skip", true, "Skipped because ~/.forge/ does not exist", null));
      checks.Add(MakeCheck("llm.apikey.resolvable", "LLM API key resolvable", "skip", false, "Skipped because ~/.forge/ does not exist", null));
    }

    // 9. Plugin directories
    checks.AddRange(CheckPluginDirs(forgeHome, "agents"));
    bool llmConfigAvailable = llmCfg is not null;

    // 10. Plugin YAML validation (non-required, warn only)
    checks.AddRange(ValidatePluginFiles(forgeHome, "agents", ".agent.yaml",
      path => { _ = AgentConfig.LoadFromYamlFile(path); }));

    // 13. Plugin model resolution
    checks.AddRange(CheckPluginModelResolved(forgeHome));

    // 13b. Compaction model context coverage (only if llm config is available)
    if (llmConfigAvailable)
    {
      checks.AddRange(CheckCompactionModelCoverage(forgeHome, llmCfg!));
    }

    // 14. Endpoint reachability (only when --probe)
    if (probeEndpoint && llmCfg is not null)
    {
      var probeCheck = await CheckEndpointReachableAsync(llmCfg, cancellationToken).ConfigureAwait(false);
      checks.Add(probeCheck);
    }
    else
    {
      checks.Add(MakeCheck("endpoint.reachable", "LLM endpoint reachable", "skip", false,
        probeEndpoint ? "No LLM config to probe" : "Use --probe to enable", null));
    }

    // 15-20. Docker preflight for the opt-in `bash` tool. All checks are
    // warnings (Required=false) because an install without bash-using agents
    // is still healthy — per docs/plans/bash-tool.md §Doctor integration and
    // docs/plans/bash-tool-rootless-docker.md §5.
    await AppendBashDockerChecksAsync(forgeHome, checks, cancellationToken).ConfigureAwait(false);

    // Aggregate counts
    foreach (var c in checks)
    {
      if (c.Status == "fail" && c.Required) hardFailures++;
      if (c.Status == "warn") warnings++;
    }

    return new DoctorReport
    {
      Version = version,
      ForgeHome = forgeHome,
      HardFailures = hardFailures,
      Warnings = warnings,
      Checks = checks
    };
  }

  private static DoctorCheck CheckDotnetRuntime()
  {
    // .NET 9+ is guaranteed because the app targets net9.0
    var version = Environment.Version;
    var ok = version.Major >= 9;
    return MakeCheck("dotnet.runtime", ".NET runtime version",
      ok ? "ok" : "fail", true,
      $".NET {version} ({(ok ? "9+ detected" : "need 9+")})",
      ok ? null : "Install .NET 9 SDK from https://dotnet.microsoft.com/download");
  }

  private static DoctorCheck CheckForgeVersion(string forgeHome)
  {
    var version = typeof(DoctorRunner).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    var location = Path.GetDirectoryName(typeof(DoctorRunner).Assembly.Location) ?? "(unknown)";
    return MakeCheck("forge.version", "Forge version and location",
      "ok", true,
      $"forge {version} at {location}",
      null);
  }

  private static DoctorCheck CheckHomeExists(string forgeHome)
  {
    var exists = Directory.Exists(forgeHome);
    return MakeCheck("home.exists", "Forge home directory",
      exists ? "ok" : "fail", true,
      exists ? forgeHome : $"~/.forge/ not found at {forgeHome}",
      exists ? null : "Run 'forge init' to create ~/.forge/");
  }

  private static DoctorCheck CheckLlmConfigPresent(string forgeHome)
  {
    var path = Path.Combine(forgeHome, "llm.json");
    var exists = File.Exists(path);
    return MakeCheck("llm.config.present", "LLM config present",
      exists ? "ok" : "fail", true,
      exists ? path : "llm.json not found",
      exists ? null : "Run 'forge init' to create llm.json");
  }

  private static (DoctorCheck check, LiteLlmConfig? cfg) CheckLlmConfigParseable(string forgeHome)
  {
    try
    {
      var cfg = LiteLlmConfig.Load(forgeHome);
      // Load returns default even when file is missing; verify we got real values
      if (string.IsNullOrEmpty(cfg.BaseUrl) && string.IsNullOrEmpty(cfg.DefaultModel))
      {
        return (MakeCheck("llm.config.parseable", "LLM config parseable", "fail", true,
          "llm.json exists but produced empty/default config", "Check llm.json format"), null);
      }
      return (MakeCheck("llm.config.parseable", "LLM config parseable", "ok", true,
        "Parsed successfully", null), cfg);
    }
    catch (Exception ex)
    {
      return (MakeCheck("llm.config.parseable", "LLM config parseable", "fail", true,
        $"Parse error: {ex.Message}", "Check llm.json syntax"), null);
    }
  }

  private static DoctorCheck CheckLlmBaseUrlV1Suffix(LiteLlmConfig cfg)
  {
    var baseUrl = cfg.BaseUrl.TrimEnd('/');
    var hasV1 = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase);
    return MakeCheck("llm.baseurl.v1suffix", "LLM baseUrl ends with /v1",
      hasV1 ? "ok" : "fail", true,
      hasV1 ? baseUrl : $"baseUrl '{baseUrl}' does not end with /v1",
      hasV1 ? null : "Add '/v1' suffix to baseUrl in ~/.forge/llm.json");
  }

  private static DoctorCheck CheckLlmDefaultModelSet(LiteLlmConfig cfg)
  {
    var model = cfg.DefaultModel?.Trim() ?? "";
    bool isPlaceholder = string.IsNullOrEmpty(model) ||
                         PlaceholderModelPatterns.Any(p =>
                           model.Contains(p, StringComparison.OrdinalIgnoreCase)) ||
                         model.StartsWith("${", StringComparison.Ordinal) ||
                         model == "your-model-id";
    return MakeCheck("llm.defaultmodel.set", "LLM default model set",
      isPlaceholder ? "fail" : "ok", true,
      isPlaceholder
        ? $"Model '{model}' is empty or a placeholder"
        : $"Default model: {model}",
      isPlaceholder
        ? "Set a real model id in ~/.forge/llm.json or via FORGE_LLM_DEFAULT_MODEL env var"
        : null);
  }

  private static DoctorCheck CheckLlmApiKeyResolvable(string forgeHome)
  {
    // Read raw JSON to check for ${VAR} patterns
    var path = Path.Combine(forgeHome, "llm.json");
    try
    {
      if (!File.Exists(path))
        return MakeCheck("llm.apikey.resolvable", "LLM API key resolvable", "skip", false, "llm.json not found", null);

      var raw = File.ReadAllText(path);
      var node = JsonNode.Parse(raw);
      var apiKey = node?["apiKey"]?.GetValue<string>() ?? "";

      if (!apiKey.Contains("${", StringComparison.Ordinal))
      {
        return MakeCheck("llm.apikey.resolvable", "LLM API key resolvable", "ok", false,
          "apiKey is set (literal value)", null);
      }

      // Has ${VAR} — check if the variable is resolvable
      var expanded = EnvironmentSubstitution.Expand(apiKey);
      if (string.IsNullOrEmpty(expanded) || expanded == apiKey)
      {
        return MakeCheck("llm.apikey.resolvable", "LLM API key resolvable", "warn", false,
          $"apiKey uses '{apiKey}' but env var is not set", "Set the referenced environment variable");
      }

      return MakeCheck("llm.apikey.resolvable", "LLM API key resolvable", "ok", false,
        "apiKey resolves via environment variable", null);
    }
    catch (Exception ex)
    {
      return MakeCheck("llm.apikey.resolvable", "LLM API key resolvable", "warn", false,
        $"Could not check: {ex.Message}", null);
    }
  }

  private static List<DoctorCheck> CheckPluginDirs(string forgeHome, string subDir)
  {
    var results = new List<DoctorCheck>();
    var id = $"plugins.{subDir}.dir";
    var title = subDir == "agents" ? "Agent plugin directory" : "Pipeline plugin directory";

    var found = false;
    var paths = new List<string>();

    foreach (var root in PluginPaths.SearchRoots(forgeHome))
    {
      var dir = Path.Combine(root, subDir);
      if (Directory.Exists(dir))
      {
        found = true;
        paths.Add(dir);
      }
    }

    if (!found)
    {
      results.Add(MakeCheck(id, title, "warn", false,
        $"No {subDir}/ directory found in any plugin search path", null));
      return results;
    }

    var fileCount = paths.Sum(d =>
    {
      try { return Directory.GetFiles(d, $"*{(subDir == "agents" ? ".agent.yaml" : ".pipeline.yaml")}").Length; }
      catch { return 0; }
    });

    var detail = fileCount > 0
      ? $"{fileCount} file(s) in {string.Join(", ", paths)}"
      : $"Directory exists but empty: {string.Join(", ", paths)}";

    results.Add(MakeCheck(id, title, found ? "ok" : "warn", false, detail, null));
    return results;
  }

  private static List<DoctorCheck> ValidatePluginFiles(string forgeHome, string subDir, string extension, Action<string> validate)
  {
    var results = new List<DoctorCheck>();
    var id = $"plugins.{subDir}.valid";
    var title = subDir == "agents" ? "Agent YAML files valid" : "Pipeline YAML files valid";
    var files = new List<string>();

    foreach (var root in PluginPaths.SearchRoots(forgeHome))
    {
      var dir = Path.Combine(root, subDir);
      if (!Directory.Exists(dir)) continue;
      try
      {
        files.AddRange(Directory.GetFiles(dir, $"*{extension}"));
      }
      catch { /* skip unreadable dirs */ }
    }

    if (files.Count == 0)
    {
      results.Add(MakeCheck(id, title, "ok", false, "No files to validate", null));
      return results;
    }

    int badCount = 0;
    foreach (var file in files)
    {
      try
      {
        validate(file);
      }
      catch (Exception ex)
      {
        badCount++;
        results.Add(MakeCheck(id, title, "warn", false,
          $"{Path.GetFileName(file)}: {ex.Message}",
          $"Fix the YAML in {file}"));
      }
    }

    if (badCount == 0)
    {
      results.Add(MakeCheck(id, title, "ok", false,
        $"{files.Count} file(s) validated successfully", null));
    }

    return results;
  }

  private static List<DoctorCheck> CheckPluginModelResolved(string forgeHome)
  {
    var results = new List<DoctorCheck>();
    var files = new List<string>();

    foreach (var root in PluginPaths.SearchRoots(forgeHome))
    {
      var dir = Path.Combine(root, "agents");
      if (!Directory.Exists(dir)) continue;
      try
      {
        files.AddRange(Directory.GetFiles(dir, "*.agent.yaml"));
      }
      catch { /* skip unreadable dirs */ }
    }

    if (files.Count == 0)
    {
      results.Add(MakeCheck("plugins.models.resolved", "Agent model ids resolved", "ok", false,
        "No agent files to check", null));
      return results;
    }

    int placeholderCount = 0;
    foreach (var file in files)
    {
      try
      {
        var cfg = AgentConfig.LoadFromYamlFile(file);
        var model = cfg.Model?.Trim() ?? "";
        bool isPlaceholder = string.IsNullOrEmpty(model) ||
                             PlaceholderModelPatterns.Any(p =>
                               model.Contains(p, StringComparison.OrdinalIgnoreCase)) ||
                             model.StartsWith("${", StringComparison.Ordinal) ||
                             model == "your-model-id";

        if (isPlaceholder)
        {
          placeholderCount++;
          results.Add(MakeCheck("plugins.models.resolved", "Agent model ids resolved", "warn", false,
            $"{Path.GetFileName(file)} uses placeholder model '{model}'",
            $"Set {Path.GetFileName(file)} model or set FORGE_LLM_DEFAULT_MODEL"));
        }
      }
      catch
      {
        // Skip files that can't be parsed — already reported by plugins.agents.valid
      }
    }

    if (placeholderCount == 0)
    {
      results.Add(MakeCheck("plugins.models.resolved", "Agent model ids resolved", "ok", false,
        $"{files.Count} agent file(s) all have resolved models", null));
    }

    return results;
  }

  private static async Task<DoctorCheck> CheckEndpointReachableAsync(LiteLlmConfig cfg, CancellationToken cancellationToken)
  {
    try
    {
      var baseUrl = cfg.BaseUrl.TrimEnd('/');
      // Strip /v1 to hit the /models endpoint
      var modelsUrl = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
        ? baseUrl[..^3] + "/models"
        : baseUrl + "/models";

      using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
      using var resp = await http.GetAsync(modelsUrl, cancellationToken).ConfigureAwait(false);

      if (resp.IsSuccessStatusCode)
      {
        return MakeCheck("endpoint.reachable", "LLM endpoint reachable", "ok", false,
          $"GET {modelsUrl} returned {(int)resp.StatusCode}", null);
      }

      return MakeCheck("endpoint.reachable", "LLM endpoint reachable", "warn", false,
        $"GET {modelsUrl} returned {(int)resp.StatusCode} {resp.ReasonPhrase}",
        "Verify the endpoint is running and reachable");
    }
    catch (OperationCanceledException)
    {
      return MakeCheck("endpoint.reachable", "LLM endpoint reachable", "warn", false,
        "Request timed out after 5s", "Verify the endpoint is running");
    }
    catch (Exception ex)
    {
      return MakeCheck("endpoint.reachable", "LLM endpoint reachable", "warn", false,
        $"Could not reach endpoint: {ex.Message}",
        "Verify the endpoint is running and the URL is correct in llm.json");
    }
  }

  private static DoctorCheck MakeCheck(string id, string title, string status, bool required, string? detail, string? fixHint)
  {
    return new DoctorCheck
    {
      Id = id,
      Title = title,
      Status = status,
      Required = required,
      Detail = detail,
      FixHint = fixHint
    };
  }

  private static async Task AppendBashDockerChecksAsync(string forgeHome, List<DoctorCheck> checks, CancellationToken ct)
  {
    // Probe 1: `docker` on PATH. If this fails we short-circuit every other
    // bash docker check to `skip` — there is no docker binary to interrogate.
    var pathResult = await RunProcessCaptureAsync("docker", new[] { "--version" }, 5, ct).ConfigureAwait(false);
    if (pathResult is null)
    {
      checks.Add(MakeCheck("bash.docker.path", "docker CLI on PATH", "warn", false,
        "`docker` not found on PATH — the `bash` tool is unavailable",
        "Install Docker (https://docs.docker.com/get-docker/) if any agent declares `bash` in its tools list"));
      AppendSkippedRemainingBashDockerChecks(checks, "Skipped because `docker` is not on PATH");
      return;
    }

    checks.Add(MakeCheck("bash.docker.path", "docker CLI on PATH", "ok", false,
      pathResult.Value.Stdout.Trim(), null));

    // Composite probe — replaces the two pre-rootless `docker info` spawns.
    // One process call yields OSType, Architecture, ServerVersion, and the
    // SecurityOptions array; DockerInfoParser turns that into a typed
    // DockerDaemonInfo. Plan: docs/plans/bash-tool-rootless-docker.md §5.
    var infoResult = await RunProcessCaptureAsync(
      "docker",
      new[] { "info", "--format", DockerInfoParser.FormatString },
      10,
      ct).ConfigureAwait(false);
    if (infoResult is null || infoResult.Value.ExitCode != 0)
    {
      var stderr = infoResult?.Stderr?.Trim() ?? "docker info timed out";
      checks.Add(MakeCheck("bash.docker.reachable", "Docker daemon reachable", "warn", false,
        $"`docker info` failed: {stderr}",
        OsSpecificStartHint()));
      AppendSkippedRemainingBashDockerChecks(checks, "Skipped because the daemon is not reachable", skipReachable: true);
      return;
    }

    DockerDaemonInfo daemon;
    try
    {
      daemon = DockerInfoParser.Parse(infoResult.Value.Stdout);
    }
    catch (FormatException ex)
    {
      checks.Add(MakeCheck("bash.docker.reachable", "Docker daemon reachable", "warn", false,
        $"`docker info` parse failed: {ex.Message}",
        "Run `docker info` manually and verify the OSType/Architecture/ServerVersion/SecurityOptions fields are present"));
      AppendSkippedRemainingBashDockerChecks(checks, "Skipped because docker info output could not be parsed", skipReachable: true);
      return;
    }

    checks.Add(MakeCheck("bash.docker.reachable", "Docker daemon reachable", "ok", false,
      $"server version {daemon.ServerVersion}", null));

    // Probe: version >= 25.0. Below that, known runC-escape CVEs make the
    // sandbox weaker than the bash plan documents.
    checks.Add(CheckDockerVersion(daemon.ServerVersion));

    // Probe: linux/amd64 platform availability. On a Docker Desktop in
    // Windows-containers mode, OSType is windows and the plan's default image
    // won't run.
    checks.Add(CheckDockerPlatform(daemon));

    // Probe: rootless posture. `ok` always; `warn` only when rootful + at
    // least one configured agent declares `bash.rootless: required`.
    checks.Add(CheckDockerRootless(forgeHome, daemon));

    // Probe: cgroup-v2 delegate. Only meaningful on rootless+linux — without
    // memory/cpu/pids controllers in /sys/fs/cgroup/cgroup.controllers, the
    // resource-limit flags Forge emits are silently ignored.
    checks.Add(CheckDockerCgroupV2(daemon));
  }

  private static void AppendSkippedRemainingBashDockerChecks(
    List<DoctorCheck> checks, string reason, bool skipReachable = false)
  {
    if (!skipReachable)
    {
      checks.Add(MakeCheck("bash.docker.reachable", "Docker daemon reachable", "skip", false, reason, null));
    }
    checks.Add(MakeCheck("bash.docker.version", "Docker version >= 25.0", "skip", false, reason, null));
    checks.Add(MakeCheck("bash.platform.linuxamd64", "Docker supports linux/amd64 images", "skip", false, reason, null));
    checks.Add(MakeCheck("bash.docker.rootless", "Docker daemon rootless posture", "skip", false, reason, null));
    checks.Add(MakeCheck("bash.docker.cgroupv2", "cgroup-v2 delegate (rootless only)", "skip", false, reason, null));
  }

  private static DoctorCheck CheckDockerVersion(string serverVersion)
  {
    // Server version format is "NN.N.N" or "NN.N.N+ext". Parse the leading
    // major.minor as an integer pair; treat any parse failure as a warn.
    var parts = serverVersion.Split(['.', '+', '-'], 3);
    if (parts.Length < 2 || !int.TryParse(parts[0], out var major))
    {
      return MakeCheck("bash.docker.version", "Docker version >= 25.0", "warn", false,
        $"Could not parse server version `{serverVersion}`",
        "Upgrade Docker to >= 25.0 to pick up runC security fixes (CVE-2019-5736, CVE-2024-21626)");
    }

    if (major < 25)
    {
      return MakeCheck("bash.docker.version", "Docker version >= 25.0", "warn", false,
        $"Docker {serverVersion} is older than 25.0",
        "Upgrade Docker to >= 25.0 to pick up runC security fixes (CVE-2019-5736, CVE-2024-21626)");
    }

    return MakeCheck("bash.docker.version", "Docker version >= 25.0", "ok", false,
      $"Docker {serverVersion}", null);
  }

  private static DoctorCheck CheckDockerPlatform(DockerDaemonInfo daemon)
  {
    var detail = $"{daemon.OsType}/{daemon.Architecture}";
    if (daemon.OsType.Equals("linux", StringComparison.OrdinalIgnoreCase))
    {
      return MakeCheck("bash.platform.linuxamd64", "Docker supports linux/amd64 images", "ok", false,
        $"Host platform {detail}", null);
    }

    return MakeCheck("bash.platform.linuxamd64", "Docker supports linux/amd64 images", "warn", false,
      $"Host platform is `{detail}` — the bash tool's default image is linux/amd64",
      "Switch Docker Desktop to Linux containers mode (Windows) or pin a Windows image in the agent's `bash.image`");
  }

  private static DoctorCheck CheckDockerRootless(string forgeHome, DockerDaemonInfo daemon)
  {
    if (!daemon.OsType.Equals("linux", StringComparison.OrdinalIgnoreCase))
    {
      return MakeCheck("bash.docker.rootless", "Docker daemon rootless posture", "skip", false,
        $"Skipped on {daemon.OsType} — Docker Desktop's VM boundary supersedes rootless on Win/macOS",
        null);
    }

    var posture = daemon.Rootless ? "daemon is rootless" : "daemon is rootful";

    if (daemon.Rootless)
    {
      return MakeCheck("bash.docker.rootless", "Docker daemon rootless posture", "ok", false,
        posture, null);
    }

    var requiringAgents = FindAgentsRequiringRootless(forgeHome);
    if (requiringAgents.Count == 0)
    {
      return MakeCheck("bash.docker.rootless", "Docker daemon rootless posture", "ok", false,
        $"{posture} (no agents declare `bash.rootless: required`)", null);
    }

    return MakeCheck("bash.docker.rootless", "Docker daemon rootless posture", "warn", false,
      $"{posture}, but {requiringAgents.Count} agent file(s) declare `bash.rootless: required`: {string.Join(", ", requiringAgents)}",
      "Set up rootless Docker (https://docs.docker.com/engine/security/rootless/) or remove `bash.rootless: required` from those agents");
  }

  private static DoctorCheck CheckDockerCgroupV2(DockerDaemonInfo daemon)
  {
    if (!daemon.OsType.Equals("linux", StringComparison.OrdinalIgnoreCase))
    {
      return MakeCheck("bash.docker.cgroupv2", "cgroup-v2 delegate (rootless only)", "skip", false,
        "Skipped on non-Linux hosts (cgroup-v2 is a Linux kernel feature)", null);
    }

    if (!daemon.Rootless)
    {
      return MakeCheck("bash.docker.cgroupv2", "cgroup-v2 delegate (rootless only)", "skip", false,
        "Skipped on rootful daemon (resource limits go through dockerd directly; cgroup-v2 delegate is only meaningful for rootless)",
        null);
    }

    const string controllersPath = "/sys/fs/cgroup/cgroup.controllers";
    if (!File.Exists(controllersPath))
    {
      return MakeCheck("bash.docker.cgroupv2", "cgroup-v2 delegate (rootless only)", "warn", false,
        $"cgroup v2 not detected ({controllersPath} missing) — `--memory`/`--cpus`/`--pids-limit` will be silently ignored",
        "See https://docs.docker.com/engine/security/rootless/#limiting-resources for cgroup-v2 + systemd delegate setup");
    }

    string controllers;
    try
    {
      controllers = File.ReadAllText(controllersPath).Trim();
    }
    catch (Exception ex)
    {
      return MakeCheck("bash.docker.cgroupv2", "cgroup-v2 delegate (rootless only)", "warn", false,
        $"Could not read {controllersPath}: {ex.Message}", null);
    }

    var tokens = controllers.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var requiredControllers = new[] { "memory", "cpu", "pids" };
    var missing = new List<string>();
    foreach (var c in requiredControllers)
    {
      var present = false;
      foreach (var t in tokens)
      {
        if (t.Equals(c, StringComparison.Ordinal))
        {
          present = true;
          break;
        }
      }
      if (!present)
      {
        missing.Add(c);
      }
    }

    if (missing.Count == 0)
    {
      return MakeCheck("bash.docker.cgroupv2", "cgroup-v2 delegate (rootless only)", "ok", false,
        $"cgroup v2 detected with controllers: {controllers}", null);
    }

    return MakeCheck("bash.docker.cgroupv2", "cgroup-v2 delegate (rootless only)", "warn", false,
      $"cgroup v2 missing controllers: {string.Join(", ", missing)} (delegated: `{controllers}`) — limits for missing controllers will be silently ignored",
      "See https://docs.docker.com/engine/security/rootless/#limiting-resources for systemd delegate=memory cpu pids io setup");
  }

  private static IReadOnlyList<string> FindAgentsRequiringRootless(string forgeHome)
  {
    var requiring = new List<string>();
    foreach (var root in PluginPaths.SearchRoots(forgeHome))
    {
      var dir = Path.Combine(root, "agents");
      if (!Directory.Exists(dir)) continue;
      string[] files;
      try { files = Directory.GetFiles(dir, "*.agent.yaml"); }
      catch { continue; }
      foreach (var file in files)
      {
        try
        {
          var cfg = AgentConfig.LoadFromYamlFile(file);
          if (cfg.Bash?.Rootless == BashRootlessMode.Required)
          {
            requiring.Add(Path.GetFileName(file));
          }
        }
        catch
        {
          // Skip unparseable files — already reported by plugins.agents.valid
        }
      }
    }
    return requiring;
  }

  private static string OsSpecificStartHint()
  {
    if (OperatingSystem.IsWindows())
    {
      return "Start Docker Desktop, or install it from https://docs.docker.com/desktop/install/windows-install/";
    }

    if (OperatingSystem.IsMacOS())
    {
      return "Start Docker Desktop, or install it from https://docs.docker.com/desktop/install/mac-install/";
    }

    return "Start the docker daemon (`systemctl start docker`) or install Docker if not present";
  }

  private static async Task<(int ExitCode, string Stdout, string Stderr)?> RunProcessCaptureAsync(
    string executable,
    IReadOnlyList<string> args,
    int timeoutSec,
    CancellationToken ct)
  {
    var psi = new System.Diagnostics.ProcessStartInfo
    {
      FileName = executable,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };
    foreach (var a in args)
    {
      psi.ArgumentList.Add(a);
    }

    using var proc = new System.Diagnostics.Process { StartInfo = psi };
    try
    {
      if (!proc.Start())
      {
        return null;
      }
    }
    catch
    {
      return null;
    }

    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
    linked.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

    var stdoutTask = proc.StandardOutput.ReadToEndAsync(linked.Token);
    var stderrTask = proc.StandardError.ReadToEndAsync(linked.Token);

    try
    {
      await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
      return null;
    }

    try
    {
      var stdout = await stdoutTask.ConfigureAwait(false);
      var stderr = await stderrTask.ConfigureAwait(false);
      return (proc.ExitCode, stdout, stderr);
    }
    catch
    {
      return null;
    }
  }

  /// <summary>
  /// Scans agent YAML files that enable compaction and checks whether their
  /// configured model appears in <c>model_context</c>. If not, compaction
  /// will fall back to the 100k default, which may be too conservative or
  /// produce unexpected behaviour.
  /// </summary>
  private static List<DoctorCheck> CheckCompactionModelCoverage(string forgeHome, LiteLlmConfig llmCfg)
  {
    var results = new List<DoctorCheck>();
    var files = new List<string>();

    foreach (var root in PluginPaths.SearchRoots(forgeHome))
    {
      var dir = Path.Combine(root, "agents");
      if (!Directory.Exists(dir)) continue;
      try
      {
        files.AddRange(Directory.GetFiles(dir, "*.agent.yaml"));
      }
      catch { /* skip unreadable dirs */ }
    }

    if (files.Count == 0)
    {
      results.Add(MakeCheck("compaction.model.context", "Compaction model context coverage", "ok", false,
        "No agent files to check", null));
      return results;
    }

    int uncoveredCount = 0;
    foreach (var file in files)
    {
      try
      {
        var cfg = AgentConfig.LoadFromYamlFile(file);
        if (cfg.Compaction?.Enabled != true) continue;
        var model = cfg.Model?.Trim() ?? "";
        if (string.IsNullOrEmpty(model)) continue;
        if (!llmCfg.ModelContext.ContainsKey(model))
        {
          uncoveredCount++;
          results.Add(MakeCheck("compaction.model.context", "Compaction model context coverage", "warn", false,
            $"{Path.GetFileName(file)} has compaction enabled but model '{model}' is not in llm.json model_context — compaction will use 100k token default",
            $"Add '{model}' to model_context in ~/.forge/llm.json with its real context limit"));
        }
      }
      catch { /* skip unparseable files */ }
    }

    if (uncoveredCount == 0)
    {
      results.Add(MakeCheck("compaction.model.context", "Compaction model context coverage", "ok", false,
        "All compaction-enabled agents have model context coverage", null));
    }

    return results;
  }
}
