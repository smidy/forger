using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Agent.Compaction;
using Forge.Core.Exceptions;
using Forge.Core.Json;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Agent.Tests;

public class SummariseStrategyTests
{
  // ─── Test 1: Happy path ───────────────────────────────────────────────────

  [Fact]
  public async Task Summarise_strategy_replaces_compaction_set_with_synthetic_pair()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    // Build 8 iterations — keepRecent=2 means first 6 are candidates
    var messages = BuildTypicalMessages(iterationCount: 8, toolContentLength: 100);
    var summariser = new ScriptedSummariser("Compacted summary of all iterations.");
    var cfg = new AgentCompactionConfig
    {
      KeepRecentIterations = 2,
      SummarisationModel = "test-summariser"
    };
    var ctx = BuildToolContext(tmp.Path, stageDir, summariser);

    var result = await SummariseStrategy.ExecuteAsync(
      messages, cfg, ctx, iteration: 8, TestContext.Current.CancellationToken);

    // Should have compacted
    result.CompactedIterations.Should().NotBeEmpty();
    result.MessagesBefore.Should().Be(messages.Count);
    result.Messages.Count.Should().BeLessThan(result.MessagesBefore);

    // Should have exactly one synthetic assistant + one synthetic tool pair
    var asstCount = result.Messages.Count(m => m?["role"]?.GetValue<string>() == "assistant");
    var toolCount = result.Messages.Count(m => m?["role"]?.GetValue<string>() == "tool");

    // Original: 1 (system) + 1 (user) + 8 assistant + 8 tool = 18
    // After: 1 system + 1 user + (8 - 6 candidates + 1 synthetic assistant) + (8 - 6 candidates + 1 synthetic tool) = 8
    // So: assistant count = 1 (original recent, excluded) + 1 (synthetic) + 1 (last candidate assistant??)
    // Actually: head preserved + synthetic pair + tail preserved
    // The result has: system(1) + user(1) + 0 head + synthetic_pair(2) + last_2_asst_groups(4) = 8
    // Wait: the preserved tail is the last keepRecent=2 groups: 2 assistant + 2 tool = 4
    // Head before first compaction group: system(1) + user(1) = 2
    // Then synthetic pair: 2
    // Then tail: 4
    // Total: 8
    result.Messages.Should().HaveCount(8);

    // Find the synthetic assistant
    var synAsst = result.Messages.FirstOrDefault(m =>
    {
      var content = m?["content"]?.GetValue<string>() ?? "";
      return content.StartsWith("[compacted]");
    });
    synAsst.Should().NotBeNull();
    synAsst!["tool_calls"].Should().NotBeNull();
    synAsst!["tool_calls"]!.AsArray().Should().NotBeEmpty();

    // Find the synthetic tool
    var synTool = result.Messages.FirstOrDefault(m =>
    {
      var callId = m?["tool_call_id"]?.GetValue<string>() ?? "";
      return callId.StartsWith("compaction_");
    });
    synTool.Should().NotBeNull();
    var toolContent = synTool!["content"]?.GetValue<string>() ?? "";
    toolContent.Should().Contain("_compacted");
    toolContent.Should().Contain("Compacted summary");

    // Archive should exist
    result.ArchivePath.Should().NotBeNull();
    Directory.Exists(result.ArchivePath).Should().BeTrue();
  }

  // ─── Test 2: Fallback model ───────────────────────────────────────────────

  [Fact]
  public async Task Summarise_strategy_uses_config_model_when_summarisation_model_null()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var messages = BuildTypicalMessages(iterationCount: 5, toolContentLength: 50);
    var summariser = new ScriptedSummariser("Summary.");

    // SummarisationModel = null, the strategy falls back to "default"
    var cfg = new AgentCompactionConfig
    {
      KeepRecentIterations = 2,
      SummarisationModel = null
    };
    var ctx = BuildToolContext(tmp.Path, stageDir, summariser);

    var result = await SummariseStrategy.ExecuteAsync(
      messages, cfg, ctx, iteration: 5, TestContext.Current.CancellationToken);

    result.CompactedIterations.Should().NotBeEmpty();

    // The ScriptedSummariser captures the model it was invoked with
    summariser.LastUsedModel.Should().Be("default");
  }

  // ─── Test 3: Explicit model ───────────────────────────────────────────────

  [Fact]
  public async Task Summarise_strategy_uses_explicit_model_when_set()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var messages = BuildTypicalMessages(iterationCount: 5, toolContentLength: 50);
    var summariser = new ScriptedSummariser("Summary.");

    var cfg = new AgentCompactionConfig
    {
      KeepRecentIterations = 2,
      SummarisationModel = "haiku-4-5"
    };
    var ctx = BuildToolContext(tmp.Path, stageDir, summariser);

    var result = await SummariseStrategy.ExecuteAsync(
      messages, cfg, ctx, iteration: 5, TestContext.Current.CancellationToken);

    result.CompactedIterations.Should().NotBeEmpty();
    summariser.LastUsedModel.Should().Be("haiku-4-5");
  }

  // ─── Test 4: Summariser fails ─────────────────────────────────────────────

  [Fact]
  public async Task Summarise_strategy_skips_when_summariser_throws()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var messages = BuildTypicalMessages(iterationCount: 5, toolContentLength: 50);
    var summariser = new FailingLlmClient(new InvalidOperationException("Network error"));
    var cfg = new AgentCompactionConfig { KeepRecentIterations = 2 };
    var ctx = BuildToolContext(tmp.Path, stageDir, summariser);

    var result = await SummariseStrategy.ExecuteAsync(
      messages, cfg, ctx, iteration: 5, TestContext.Current.CancellationToken);

    // Should return original messages unchanged
    result.CompactedIterations.Should().BeEmpty();
    result.Messages.Should().HaveCount(messages.Count);
    result.ArchivePath.Should().BeNull();

    // Should have emitted a CompactionSkippedEvent
    var trace = (CapturingTraceSink)ctx.Trace;
    trace.Events.OfType<CompactionSkippedEvent>().Should().Contain(e =>
      e.Reason == "summariser_failed");
  }

  // ─── Test 5: Empty summary ────────────────────────────────────────────────

  [Fact]
  public async Task Summarise_strategy_skips_when_summary_empty()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var messages = BuildTypicalMessages(iterationCount: 5, toolContentLength: 50);
    var summariser = new ScriptedSummariser("");  // empty
    var cfg = new AgentCompactionConfig { KeepRecentIterations = 2 };
    var ctx = BuildToolContext(tmp.Path, stageDir, summariser);

    var result = await SummariseStrategy.ExecuteAsync(
      messages, cfg, ctx, iteration: 5, TestContext.Current.CancellationToken);

    result.CompactedIterations.Should().BeEmpty();
    result.Messages.Should().HaveCount(messages.Count);
  }

  // ─── Test 6: Growing summary aborts ───────────────────────────────────────

  [Fact]
  public async Task Summarise_strategy_aborts_when_summary_exceeds_input_estimate()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    // Very small messages so even a short summary would exceed the input
    var messages = BuildTypicalMessages(iterationCount: 3, toolContentLength: 5);
    var summariser = new ScriptedSummariser(new string('x', 10000));  // very long "summary"
    var cfg = new AgentCompactionConfig { KeepRecentIterations = 1 };
    var ctx = BuildToolContext(tmp.Path, stageDir, summariser);

    var result = await SummariseStrategy.ExecuteAsync(
      messages, cfg, ctx, iteration: 3, TestContext.Current.CancellationToken);

    result.CompactedIterations.Should().BeEmpty();
    result.Messages.Should().HaveCount(messages.Count);

    var trace = (CapturingTraceSink)ctx.Trace;
    trace.Events.OfType<CompactionSkippedEvent>().Should().Contain(e =>
      e.Reason == "summary_exceeds_input");
  }

  // ─── Test 7: CollectCompactionGroups groups correctly ──────────────────────

  [Fact]
  public void CollectCompactionGroups_returns_expected_groups()
  {
    var messages = BuildTypicalMessages(iterationCount: 6, toolContentLength: 50);
    var partition = CompactionWindow.Partition(messages, keepRecent: 2);

    var groups = SummariseStrategy.CollectCompactionGroups(messages, partition);

    // 6 iterations total, keepRecent=2 => 4 candidate iterations
    groups.Should().NotBeEmpty();
    foreach (var g in groups)
    {
      g.StartIndex.Should().BeGreaterThanOrEqualTo(2);
      g.EndIndex.Should().BeGreaterThanOrEqualTo(g.StartIndex);
      g.IterationIndex.Should().BeGreaterThanOrEqualTo(0);
    }
  }

  // ─── Test 8: BuildSummariserInput contains preview-capped content ──────────

  [Fact]
  public void BuildSummariserInput_previews_tool_content()
  {
    var messages = BuildTypicalMessages(iterationCount: 2, toolContentLength: 5000);
    var partition = CompactionWindow.Partition(messages, keepRecent: 1);
    var groups = SummariseStrategy.CollectCompactionGroups(messages, partition);

    var input = SummariseStrategy.BuildSummariserInput(messages, groups);

    foreach (var item in input)
    {
      var grp = item!.AsObject();
      grp["iteration"].Should().NotBeNull();
      grp["assistant"].Should().NotBeNull();
      var toolResults = grp["tool_results"]?.AsArray();
      if (toolResults is not null)
      {
        foreach (var tr in toolResults)
        {
          var preview = tr?["preview"]?.GetValue<string>() ?? "";
          preview.Length.Should().BeLessThanOrEqualTo(2000);
        }
      }
    }
  }

  // ─── Test 9: PairingInvariant accepts synthetic tool name ──────────────────

  [Fact]
  public void PairingInvariant_accepts_synthetic_compaction_name()
  {
    // Build a messages list with a synthetic compaction pair
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "sys" },
      new JsonObject { ["role"] = "user", ["content"] = "usr" },
      new JsonObject
      {
        ["role"] = "assistant",
        ["content"] = "[compacted] Iterations 0-2: Summary.",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject
          {
            ["id"] = "compaction_0_1",
            ["type"] = "function",
            ["function"] = new JsonObject
            {
              ["name"] = "__compaction_summary__",
              ["arguments"] = "{}"
            }
          }
        }
      },
      new JsonObject
      {
        ["role"] = "tool",
        ["tool_call_id"] = "compaction_0_1",
        ["content"] = """{"_compacted":true,"summary":"Summary."}"""
      },
      new JsonObject
      {
        ["role"] = "assistant",
        ["content"] = "Final result"
      }
    };

    var act = () => PairingInvariant.Check(messages);
    act.Should().NotThrow();
  }

  // ─── Helpers ──────────────────────────────────────────────────────────────

  private static List<JsonNode> BuildTypicalMessages(int iterationCount, int toolContentLength = 100)
  {
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "System prompt here" },
      new JsonObject { ["role"] = "user", ["content"] = "User prompt here" }
    };

    for (var i = 0; i < iterationCount; i++)
    {
      messages.Add(new JsonObject
      {
        ["role"] = "assistant",
        ["content"] = $"Thinking step {i}",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject
          {
            ["id"] = $"call-{i}",
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{}" }
          }
        }
      });
      messages.Add(new JsonObject
      {
        ["role"] = "tool",
        ["tool_call_id"] = $"call-{i}",
        ["content"] = new string('x', toolContentLength)
      });
    }

    return messages;
  }

  private static ToolContext BuildToolContext(string runRoot, string stageDir, ILlmClient llm)
  {
    var idx = 0;
    var trace = new CapturingTraceSink();
    return new ToolContext(
      RunId: "test-run",
      RunWorkspace: runRoot,
      StageDir: stageDir,
      StageId: "agent",
      IterationIndex: null,
      Llm: llm,
      Trace: trace,
      Logger: NullLogger.Instance,
      CancellationToken: CancellationToken.None,
      NextToolOutputIdx: () => ++idx);
  }

  private sealed class CapturingTraceSink : ITraceSink
  {
    public List<TraceEvent> Events { get; } = new();
    public void Trace(TraceEvent e) => Events.Add(e);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  /// <summary>
  /// An ILlmClient that returns a scripted summary. Captures the model name
  /// from each request so tests can verify model selection.
  /// </summary>
  private sealed class ScriptedSummariser : ILlmClient
  {
    private readonly string _summary;

    public ScriptedSummariser(string summary)
    {
      _summary = summary;
    }

    public string? LastUsedModel { get; private set; }

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
      LastUsedModel = request.Model;

      var response = new CompletionResponse
      {
        Id = "test-summary",
        Choices = new List<CompletionChoice>
        {
          new()
          {
            Index = 0,
            FinishReason = "stop",
            Message = new ChatMessagePayload
            {
              Role = "assistant",
              Content = JsonValue.Create(_summary)
            }
          }
        },
        Usage = new UsagePayload
        {
          PromptTokens = 100,
          CompletionTokens = 20
        }
      };
      return Task.FromResult(response);
    }
  }

  /// <summary>
  /// An ILlmClient that always throws the specified exception.
  /// </summary>
  private sealed class FailingLlmClient : ILlmClient
  {
    private readonly Exception _exception;

    public FailingLlmClient(Exception exception)
    {
      _exception = exception;
    }

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
      throw _exception;
    }
  }

  private sealed class TempDir : IDisposable
  {
    public string Path { get; }

    public TempDir()
    {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-test-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
      try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
    }
  }
}
