using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Cli.Commands;

namespace Forge.Cli.Tests;

public sealed class InitCommandTests : IDisposable
{
  private readonly string _tempHome;

  public InitCommandTests()
  {
    _tempHome = Path.Combine(Path.GetTempPath(), $"forge-init-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempHome);
  }

  public void Dispose()
  {
    try
    {
      if (Directory.Exists(_tempHome))
      {
        Directory.Delete(_tempHome, recursive: true);
      }
    }
    catch
    {
    }
  }

  [Fact]
  public async Task HappyPath_NonInteractive_WritesLlmJson()
  {
    var settings = new InitSettings
    {
      BaseUrl = "http://localhost:4001/v1",
      ApiKey = "sk-test",
      Model = "test-model",
      NonInteractive = true,
    };

    var exit = await InitCommand.ExecuteAsync(settings, _tempHome);
    exit.Should().Be(0);

    var llmJson = Path.Combine(_tempHome, "llm.json");
    File.Exists(llmJson).Should().BeTrue();
    var parsed = JsonNode.Parse(await File.ReadAllTextAsync(llmJson, TestContext.Current.CancellationToken))!.AsObject();
    parsed["baseUrl"]!.GetValue<string>().Should().Be("http://localhost:4001/v1");
    parsed["apiKey"]!.GetValue<string>().Should().Be("sk-test");
    parsed["defaultModel"]!.GetValue<string>().Should().Be("test-model");
  }

  [Fact]
  public async Task ForceGuard_RefusesOverwriteWithoutForce()
  {
    var llmJson = Path.Combine(_tempHome, "llm.json");
    await File.WriteAllTextAsync(llmJson, "{\"baseUrl\":\"x\",\"apiKey\":\"y\",\"defaultModel\":\"z\"}", TestContext.Current.CancellationToken);
    var originalSize = new FileInfo(llmJson).Length;

    var settings = new InitSettings
    {
      BaseUrl = "http://localhost:4001/v1",
      ApiKey = "sk-new",
      Model = "new-model",
      NonInteractive = true,
      Force = false,
    };

    var exit = await InitCommand.ExecuteAsync(settings, _tempHome);
    exit.Should().Be(1);
    new FileInfo(llmJson).Length.Should().Be(originalSize, "existing llm.json must be untouched");
  }

  [Fact]
  public async Task Force_OverwritesExistingLlmJson()
  {
    var llmJson = Path.Combine(_tempHome, "llm.json");
    await File.WriteAllTextAsync(llmJson, "{\"baseUrl\":\"x\",\"apiKey\":\"y\",\"defaultModel\":\"z\"}", TestContext.Current.CancellationToken);

    var settings = new InitSettings
    {
      BaseUrl = "http://localhost:4001/v1",
      ApiKey = "sk-new",
      Model = "new-model",
      NonInteractive = true,
      Force = true,
    };

    var exit = await InitCommand.ExecuteAsync(settings, _tempHome);
    exit.Should().Be(0);
    var parsed = JsonNode.Parse(await File.ReadAllTextAsync(llmJson, TestContext.Current.CancellationToken))!.AsObject();
    parsed["defaultModel"]!.GetValue<string>().Should().Be("new-model");
  }

  [Fact]
  public async Task NonInteractive_MissingModel_ErrorsWithoutWriting()
  {
    var settings = new InitSettings
    {
      BaseUrl = "http://localhost:4001/v1",
      ApiKey = "sk-test",
      Model = null,
      NonInteractive = true,
    };

    var exit = await InitCommand.ExecuteAsync(settings, _tempHome);
    exit.Should().Be(1);
    File.Exists(Path.Combine(_tempHome, "llm.json")).Should().BeFalse();
  }

  [Fact]
  public async Task InvalidBaseUrl_WithoutV1Suffix_Errors()
  {
    var settings = new InitSettings
    {
      BaseUrl = "http://localhost:4001",
      ApiKey = "sk-test",
      Model = "test-model",
      NonInteractive = true,
    };

    var exit = await InitCommand.ExecuteAsync(settings, _tempHome);
    exit.Should().Be(1);
    File.Exists(Path.Combine(_tempHome, "llm.json")).Should().BeFalse();
  }

  [Fact]
  public async Task CopyExamples_OnFreshHome_ExtractsEmbeddedAgentsAndPipelines()
  {
    var settings = new InitSettings
    {
      BaseUrl = "http://localhost:4001/v1",
      ApiKey = "sk-test",
      Model = "test-model",
      NonInteractive = true,
      CopyExamples = true,
    };

    var exit = await InitCommand.ExecuteAsync(settings, _tempHome);
    exit.Should().Be(0);

    var agentsDir = Path.Combine(_tempHome, "agents");
    var pipelinesDir = Path.Combine(_tempHome, "pipelines");
    Directory.Exists(agentsDir).Should().BeTrue();
    Directory.Exists(pipelinesDir).Should().BeTrue();

    var agents = Directory.GetFiles(agentsDir, "*.agent.yaml");
    var pipelines = Directory.GetFiles(pipelinesDir, "*.pipeline.yaml");
    agents.Should().NotBeEmpty("embedded agent YAMLs must land in ~/.forge/agents/");
    pipelines.Should().NotBeEmpty("embedded pipeline YAMLs must land in ~/.forge/pipelines/");
  }

  [Fact]
  public async Task CopyExamples_SkipsExistingFiles()
  {
    var agentsDir = Path.Combine(_tempHome, "agents");
    Directory.CreateDirectory(agentsDir);
    var existing = Path.Combine(agentsDir, "hello.agent.yaml");
    await File.WriteAllTextAsync(existing, "user-edited content", TestContext.Current.CancellationToken);

    var settings = new InitSettings
    {
      BaseUrl = "http://localhost:4001/v1",
      ApiKey = "sk-test",
      Model = "test-model",
      NonInteractive = true,
      CopyExamples = true,
    };

    var exit = await InitCommand.ExecuteAsync(settings, _tempHome);
    exit.Should().Be(0);
    (await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken)).Should().Be("user-edited content", "existing file must not be overwritten");
  }
}

public sealed class ExampleExtractorTests : IDisposable
{
  private readonly string _tempDir;

  public ExampleExtractorTests()
  {
    _tempDir = Path.Combine(Path.GetTempPath(), $"forge-extractor-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempDir);
  }

  public void Dispose()
  {
    try
    {
      if (Directory.Exists(_tempDir))
      {
        Directory.Delete(_tempDir, recursive: true);
      }
    }
    catch
    {
    }
  }

  [Fact]
  public void CopyEmbeddedExamples_ReturnsCopiedFileNames()
  {
    var agentsDir = Path.Combine(_tempDir, "agents");
    var pipelinesDir = Path.Combine(_tempDir, "pipelines");
    Directory.CreateDirectory(agentsDir);
    Directory.CreateDirectory(pipelinesDir);

    var copied = ExampleExtractor.CopyEmbeddedExamples(agentsDir, pipelinesDir);

    copied.Should().NotBeNull();
    foreach (var name in copied)
    {
      var inAgents = Path.Combine(agentsDir, name);
      var inPipelines = Path.Combine(pipelinesDir, name);
      (File.Exists(inAgents) || File.Exists(inPipelines)).Should().BeTrue($"copied file {name} should exist on disk");
    }
  }

  [Fact]
  public void CopyEmbeddedExamples_SkipsExistingFiles()
  {
    var agentsDir = Path.Combine(_tempDir, "agents");
    var pipelinesDir = Path.Combine(_tempDir, "pipelines");
    Directory.CreateDirectory(agentsDir);
    Directory.CreateDirectory(pipelinesDir);

    var sentinel = Path.Combine(agentsDir, "hello.agent.yaml");
    File.WriteAllText(sentinel, "existing content");

    ExampleExtractor.CopyEmbeddedExamples(agentsDir, pipelinesDir);

    File.ReadAllText(sentinel).Should().Be("existing content");
  }
}
