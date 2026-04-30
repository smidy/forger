using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Cli.Commands;
using Forge.Cli.Doctor;

namespace Forge.Cli.Tests;

public sealed class DoctorCheckTests : IDisposable
{
  private readonly string _tempHome;

  public DoctorCheckTests()
  {
    _tempHome = Path.Combine(Path.GetTempPath(), $"forge-doctor-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempHome);
  }

  public void Dispose()
  {
    try
    {
      if (Directory.Exists(_tempHome))
        Directory.Delete(_tempHome, recursive: true);
    }
    catch { /* best effort */ }
  }

  /// <summary>
  /// Writes a minimal valid llm.json to the temp forge home.
  /// </summary>
  private void WriteLlmJson(string? baseUrl = "http://localhost:4001/v1", string? apiKey = "sk-local", string? model = "gpt-4o")
  {
    var cfg = new JsonObject
    {
      ["baseUrl"] = baseUrl,
      ["apiKey"] = apiKey,
      ["defaultModel"] = model
    };
    File.WriteAllText(Path.Combine(_tempHome, "llm.json"), cfg.ToJsonString());
  }

  /// <summary>
  /// Writes a minimal agent YAML file.
  /// </summary>
  private void WriteAgentYaml(string name, string model = "gpt-4o")
  {
    var agentsDir = Path.Combine(_tempHome, "agents");
    Directory.CreateDirectory(agentsDir);
    var content = $"""
name: {name}
model: {model}
system_prompt: "test"
user_prompt: "test"
tools:
  - read_file
input_schema:
  type: object
  properties:
    task:
      type: string
  required:
    - task
output_schema:
  type: object
  properties:
    summary:
      type: string
  required:
    - summary
""";
    File.WriteAllText(Path.Combine(agentsDir, $"{name}.agent.yaml"), content);
  }

  [Fact]
  public async Task WorkingInstall_AllOk()
  {
    WriteLlmJson();

    var report = await DoctorRunner.RunAsync(_tempHome, false, TestContext.Current.CancellationToken);

    report.HardFailures.Should().Be(0, "all required checks should pass on a working install");
    report.Checks.Should().AllSatisfy(c =>
    {
      if (c.Required)
        c.Status.Should().BeOneOf("ok", "skip", $"required check {c.Id} should not be 'fail'");
    });
  }

  [Fact]
  public async Task NoLlmJson_ExitsWithHardFailure()
  {
    // Ensure no llm.json exists
    var report = await DoctorRunner.RunAsync(_tempHome, false, TestContext.Current.CancellationToken);

    report.HardFailures.Should().BeGreaterThan(0, "missing llm.json should cause hard failures");
    var llmPresent = report.Checks.First(c => c.Id == "llm.config.present");
    llmPresent.Status.Should().Be("fail");
    llmPresent.FixHint.Should().Contain("forge init");
  }

  [Fact]
  public async Task BaseUrlMissingV1Suffix_ExitsWithHardFailure()
  {
    WriteLlmJson(baseUrl: "http://localhost:4001");

    var report = await DoctorRunner.RunAsync(_tempHome, false, TestContext.Current.CancellationToken);

    report.HardFailures.Should().BeGreaterThan(0);
    var v1Check = report.Checks.First(c => c.Id == "llm.baseurl.v1suffix");
    v1Check.Status.Should().Be("fail");
    v1Check.FixHint.Should().Contain("/v1");
  }

  [Fact]
  public async Task PlaceholderModel_WarnsButExitsZero()
  {
    WriteLlmJson(model: "replace-with-your-model-id");

    var report = await DoctorRunner.RunAsync(_tempHome, false, TestContext.Current.CancellationToken);

    var modelCheck = report.Checks.First(c => c.Id == "llm.defaultmodel.set");
    modelCheck.Status.Should().Be("fail");
    modelCheck.Required.Should().BeTrue();
    report.HardFailures.Should().BeGreaterThan(0);
  }

  [Fact]
  public async Task AgentWithPlaceholderModel_Warns()
  {
    WriteLlmJson(model: "gpt-4o");
    WriteAgentYaml("test-agent", model: "replace-with-your-model-id");

    var report = await DoctorRunner.RunAsync(_tempHome, false, TestContext.Current.CancellationToken);

    var modelCheck = report.Checks.FirstOrDefault(c => c.Id == "plugins.models.resolved");
    modelCheck.Should().NotBeNull();
    modelCheck!.Status.Should().Be("warn");
    report.HardFailures.Should().Be(0, "plugin model warnings are not required");
  }

  [Fact]
  public async Task Probe_SkipWhenNotRequested()
  {
    WriteLlmJson();

    var report = await DoctorRunner.RunAsync(_tempHome, false, TestContext.Current.CancellationToken);

    var probeCheck = report.Checks.First(c => c.Id == "endpoint.reachable");
    probeCheck.Status.Should().Be("skip");
  }

  [Fact]
  public async Task JsonOutputSchema_Stable()
  {
    WriteLlmJson();
    WriteAgentYaml("hello");

    var report = await DoctorRunner.RunAsync(_tempHome, false, TestContext.Current.CancellationToken);

    // Serialize and verify structural stability
    var json = JsonSerializer.Serialize(report, Forge.Core.Json.JsonSerializationDefaults.Indented);
    var parsed = JsonNode.Parse(json)!;

    parsed["version"].Should().NotBeNull();
    parsed["forgeHome"].Should().NotBeNull();
    parsed["hardFailures"].Should().NotBeNull();
    parsed["warnings"].Should().NotBeNull();
    parsed["checks"].Should().BeOfType<JsonArray>();
    foreach (var check in parsed["checks"]!.AsArray())
    {
      check!["id"].Should().NotBeNull();
      check["title"].Should().NotBeNull();
      check["status"].Should().NotBeNull();
      check["required"].Should().NotBeNull();
    }
  }

  [Fact]
  public async Task ApiKeyEnvVar_WarnsWhenUnresolvable()
  {
    WriteLlmJson(apiKey: "${MISSING_ENV_VAR_FOR_TEST}");

    var report = await DoctorRunner.RunAsync(_tempHome, false, TestContext.Current.CancellationToken);

    var keyCheck = report.Checks.First(c => c.Id == "llm.apikey.resolvable");
    keyCheck.Status.Should().Be("warn");
    keyCheck.FixHint.Should().Contain("environment variable");
    report.HardFailures.Should().Be(0, "apiKey resolvable is not a required check");
  }

  [Fact]
  public async Task ValidAgentYaml_PassesValidation()
  {
    WriteLlmJson();
    WriteAgentYaml("good-agent");

    var report = await DoctorRunner.RunAsync(_tempHome, false, TestContext.Current.CancellationToken);

    var validCheck = report.Checks.First(c => c.Id == "plugins.agents.valid");
    validCheck.Status.Should().Be("ok");
  }

  [Fact]
  public async Task ForgeHomeNotExists_HardFailure()
  {
    // Use a non-existent home
    var missingHome = Path.Combine(Path.GetTempPath(), $"forge-doctor-missing-{Guid.NewGuid():N}");

    var report = await DoctorRunner.RunAsync(missingHome, false, TestContext.Current.CancellationToken);

    var homeCheck = report.Checks.First(c => c.Id == "home.exists");
    homeCheck.Status.Should().Be("fail");
    homeCheck.FixHint.Should().Contain("forge init");
    report.HardFailures.Should().BeGreaterThan(0);
  }

  [Fact]
  public async Task AllChecksPresent()
  {
    WriteLlmJson();

    var report = await DoctorRunner.RunAsync(_tempHome, false, TestContext.Current.CancellationToken);

    var expectedIds = new[]
    {
      "dotnet.runtime",
      "forge.version",
      "home.exists",
      "llm.config.present",
      "llm.config.parseable",
      "llm.baseurl.v1suffix",
      "llm.defaultmodel.set",
      "llm.apikey.resolvable",
      "plugins.agents.dir",
      "plugins.agents.valid",
      "plugins.models.resolved",
      "endpoint.reachable",
      "bash.docker.path",
      "bash.docker.reachable",
      "bash.docker.version",
      "bash.platform.linuxamd64",
      "bash.docker.rootless",
      "bash.docker.cgroupv2",
      "compaction.model.context"
    };

    var actualIds = report.Checks.Select(c => c.Id).OrderBy(x => x).ToArray();
    actualIds.Should().BeEquivalentTo(expectedIds, "all checks from the plan must be present");
  }

  [Fact]
  public async Task ExitCode_Zero_WhenNoHardFailures()
  {
    WriteLlmJson();
    WriteAgentYaml("ok-agent");

    var settings = new DoctorSettings { Probe = false, Human = false };
    var appState = new ForgeAppState { ForgeHome = _tempHome, CancellationToken = TestContext.Current.CancellationToken };

    var exitCode = await DoctorCommand.ExecuteAsync(settings, appState);
    exitCode.Should().Be(0);
  }

  [Fact]
  public async Task ExitCode_One_WhenHardFailures()
  {
    // No llm.json — hard failures expected
    var appState = new ForgeAppState { ForgeHome = _tempHome, CancellationToken = TestContext.Current.CancellationToken };
    var settings = new DoctorSettings { Probe = false, Human = false };

    var exitCode = await DoctorCommand.ExecuteAsync(settings, appState);
    exitCode.Should().Be(1);
  }
}
