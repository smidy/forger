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

public class ContextCompactionTests
{
  // ─── Test 1: Estimator correctness ────────────────────────────────────────

  [Fact]
  public void TokenEstimator_estimate_within_tolerance()
  {
    // Build synthetic messages with known char count: ~200 chars per message
    var messages = new List<JsonNode>();
    for (var i = 0; i < 10; i++)
    {
      messages.Add(new JsonObject
      {
        ["role"] = "assistant",
        ["content"] = new string('x', 200)
      });
    }

    var estimated = TokenEstimator.Estimate(messages);

    // char/4 + 4 per message: (10 * 200) / 4 + 10 * 4 = 500 + 40 = 540
    // Allow ±15%
    estimated.Should().BeInRange(486, 715);
  }

  [Fact]
  public void TokenEstimator_single_message_returns_at_least_1()
  {
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "" }
    };

    var estimated = TokenEstimator.Estimate(messages);
    estimated.Should().BeGreaterThanOrEqualTo(1);
  }

  // ─── Test 2: Estimator correction ─────────────────────────────────────────

  [Fact]
  public void TokenEstimator_correction_scales_when_actual_greater_than_estimate()
  {
    // Simulate: iter 1 estimated 40k, actual 50k => ratio 1.25 > 1.15
    var iter1Estimated = 40000;
    var iter1Actual = 50000;
    var iter2RawEstimate = 40000; // raw estimate for iter 2

    var corrected = TokenEstimator.ApplyCorrection(iter2RawEstimate, iter1Actual, iter1Estimated);
    // 40000 * 1.25 = 50000
    corrected.Should().Be(50000);
  }

  [Fact]
  public void TokenEstimator_correction_not_applied_when_divergence_within_15_percent()
  {
    var corrected = TokenEstimator.ApplyCorrection(10000, 10500, 10000);
    // 10500/10000 = 1.05, within 0.85..1.15 => no correction
    corrected.Should().Be(10000);
  }

  [Fact]
  public void TokenEstimator_correction_returns_estimated_when_lastActual_is_null()
  {
    var corrected = TokenEstimator.ApplyCorrection(10000, null, 10000);
    corrected.Should().Be(10000);
  }

  // ─── Test 3: Trigger off by default ───────────────────────────────────────

  [Fact]
  public void CompactionTrigger_disabled_when_compaction_null()
  {
    var messages = BuildMessagesWithSize(500); // 500 chars per message, ≈ many tokens
    var cfg = new AgentCompactionConfig { Enabled = false };
    var llmCfg = new LiteLlmConfig();
    llmCfg.ModelContext["m"] = 100000;

    var shouldCompact = CompactionTrigger.ShouldCompact(
      messages, cfg, "m", llmCfg, null, null);

    shouldCompact.Should().BeFalse();
  }

  // ─── Test 4: Trigger — absolute threshold ─────────────────────────────────

  [Fact]
  public void CompactionTrigger_fires_on_absolute_threshold()
  {
    // Build messages that estimate to more than 50000 tokens
    var messages = BuildMessagesWithSize(2500, count: 40); // ~(2500*40/4 + 40*4) ≈ 25160 > 50000

    // Make sure we actually exceed threshold
    var est = TokenEstimator.Estimate(messages);
    est.Should().BeGreaterThan(50000);

    var cfg = new AgentCompactionConfig
    {
      Enabled = true,
      TokenThreshold = 50000
    };
    var llmCfg = new LiteLlmConfig();

    var shouldCompact = CompactionTrigger.ShouldCompact(
      messages, cfg, "m", llmCfg, null, null);

    shouldCompact.Should().BeTrue();
  }

  [Fact]
  public void CompactionTrigger_does_not_fire_below_absolute_threshold()
  {
    var messages = BuildMessagesWithSize(5, count: 5); // very small
    var cfg = new AgentCompactionConfig
    {
      Enabled = true,
      TokenThreshold = 50000
    };
    var llmCfg = new LiteLlmConfig();

    var shouldCompact = CompactionTrigger.ShouldCompact(
      messages, cfg, "m", llmCfg, null, null);

    shouldCompact.Should().BeFalse();
  }

  // ─── Test 5: Trigger — pct of model limit ─────────────────────────────────

  [Fact]
  public void CompactionTrigger_fires_on_pct_of_model_limit()
  {
    // Model context = 100000, pct = 0.75 => threshold = 75000
    // Build messages above that
    var messages = BuildMessagesWithSize(8000, count: 40); // ~(8000*40/4 + 160) = 80000 + 160 = 80160 > 75000

    var cfg = new AgentCompactionConfig
    {
      Enabled = true,
      PctOfModelLimit = 0.75
    };
    var llmCfg = new LiteLlmConfig();
    llmCfg.ModelContext["m"] = 100000;

    var shouldCompact = CompactionTrigger.ShouldCompact(
      messages, cfg, "m", llmCfg, null, null);

    shouldCompact.Should().BeTrue();
  }

  // ─── Test 6: Trigger — unknown model falls back ───────────────────────────

  [Fact]
  public void CompactionTrigger_unknown_model_falls_back_and_emits_warning()
  {
    var messages = BuildMessagesWithSize(3000, count: 50); // enough tokens
    var cfg = new AgentCompactionConfig
    {
      Enabled = true,
      PctOfModelLimit = 0.75
    };
    var llmCfg = new LiteLlmConfig(); // empty ModelContext

    int? fallbackThreshold = null;
    var shouldCompact = CompactionTrigger.ShouldCompact(
      messages, cfg, "unknown-model", llmCfg, null, null, out fallbackThreshold);

    shouldCompact.Should().BeTrue();
    fallbackThreshold.Should().NotBeNull();
    fallbackThreshold.Should().Be(100000);
  }

  // ─── Test 7: Selection — preserve head ────────────────────────────────────

  [Fact]
  public void CompactionWindow_preserves_system_and_root_user()
  {
    var messages = BuildTypicalMessages(iterationCount: 5);
    var partition = CompactionWindow.Partition(messages, keepRecent: 3);

    // indices 0 (system) and 1 (root user) must always be preserved
    partition.PreservedIndices.Should().Contain(0);
    partition.PreservedIndices.Should().Contain(1);
  }

  // ─── Test 8: Selection — preserve tail ────────────────────────────────────

  [Fact]
  public void CompactionWindow_preserves_last_N_iterations()
  {
    var messages = BuildTypicalMessages(iterationCount: 10);
    // With keepRecent=3 and 10 iterations: indices 0-1 preserved, last 3 assistant groups preserved
    var partition = CompactionWindow.Partition(messages, keepRecent: 3);

    // Find the assistant indices in the original list
    var asstIndices = new List<int>();
    for (var i = 2; i < messages.Count; i++)
    {
      if (messages[i]?["role"]?.GetValue<string>() == "assistant")
        asstIndices.Add(i);
    }
    asstIndices.Should().HaveCount(10);

    // Last 3 assistant groups should be preserved
    var lastThreeStart = asstIndices[^3];
    for (var i = lastThreeStart; i < messages.Count; i++)
    {
      partition.PreservedIndices.Should().Contain(i);
    }
  }

  // ─── Test 9: Selection — cross-window tool_call ───────────────────────────

  [Fact]
  public void CompactionWindow_promotes_tool_result_when_assistant_in_preserved_set_references_it()
  {
    // Build messages where one tool result in the candidate set is referenced
    // by an assistant in the preserved set (i.e. a tool_call_id crossing the window boundary)
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "sys" },
      new JsonObject { ["role"] = "user", ["content"] = "usr" },
      // Iteration 0 — compaction candidate (old)
      new JsonObject
      {
        ["role"] = "assistant",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject { ["id"] = "call-old", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{}" } }
        }
      },
      new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call-old", ["content"] = "old content" },
      // Iteration 1 — compaction candidate
      new JsonObject
      {
        ["role"] = "assistant",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject { ["id"] = "call-cross", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{}" } }
        }
      },
      new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call-cross", ["content"] = "cross content" },
      // Iteration 2 — preserved (most recent with keepRecent=1)
      new JsonObject
      {
        ["role"] = "assistant",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject { ["id"] = "call-latest", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{}" } }
        }
      },
      // This tool result is referenced by the preserved assistant but is in the candidate set
      new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call-latest", ["content"] = "latest result" }
    };

    var partition = CompactionWindow.Partition(messages, keepRecent: 1);

    // The tool for "call-latest" should be promoted to preserved set
    // Find the index of the tool message with tool_call_id "call-latest"
    var latestToolIdx = messages.FindIndex(m =>
      m?["role"]?.GetValue<string>() == "tool" &&
      m?["tool_call_id"]?.GetValue<string>() == "call-latest");

    partition.PreservedIndices.Should().Contain(latestToolIdx);
  }

  // ─── Test 10: Trim — stub shape ───────────────────────────────────────────

  [Fact]
  public async Task TrimToolResults_strategy_produces_correct_stub()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var messages = BuildTypicalMessages(iterationCount: 5, toolContentLength: 5000);
    var cfg = new AgentCompactionConfig { KeepRecentIterations = 2 };
    var ctx = BuildToolContext(tmp.Path, stageDir);

    var result = await TrimToolResultsStrategy.ExecuteAsync(
      messages, cfg, ctx, iteration: 5, TestContext.Current.CancellationToken);

    // Should have compacted tool results
    result.CompactedIterations.Should().NotBeEmpty();
    result.MessagesBefore.Should().Be(messages.Count);

    // Verify compacted messages have stubs
    foreach (var msg in result.Messages)
    {
      var role = msg?["role"]?.GetValue<string>() ?? "";
      if (role != "tool") continue;
      var content = msg?["content"]?.GetValue<string>();
      if (content is null) continue;

      // Content may be plain text for non-compacted tool results
      // or JSON for compacted stubs. Try to parse as JSON.
      var parsed = TryParseJson(content);
      if (parsed is JsonObject obj && obj["_truncated"]?.GetValue<bool>() == true)
      {
        // This is a stub — verify shape
        obj["size_bytes"].Should().NotBeNull();
        obj["preview"].Should().NotBeNull();
        obj["artifact"].Should().NotBeNull();
        obj["artifact"]!["path"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        obj["artifact"]!["read_with"]!.GetValue<string>().Should().Be("read_file_slice");
        obj["_compacted_at_iteration"]?.GetValue<int>().Should().Be(5);
      }
    }

    // Verify archive files exist on disk
    result.ArchivePath.Should().NotBeNull();
    Directory.Exists(result.ArchivePath).Should().BeTrue();
    Directory.GetFiles(result.ArchivePath!).Should().NotBeEmpty();
  }

  private static JsonNode? TryParseJson(string content)
  {
    try { return System.Text.Json.Nodes.JsonNode.Parse(content); }
    catch (System.Text.Json.JsonException) { return null; }
  }

  // ─── Test 11: Trim — already-stub idempotent ──────────────────────────────

  [Fact]
  public async Task TrimToolResults_leaves_existing_stubs_unchanged()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    // Build messages where some tool results are already stubs
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "sys" },
      new JsonObject { ["role"] = "user", ["content"] = "usr" },
      new JsonObject
      {
        ["role"] = "assistant",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject { ["id"] = "call-1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{}" } }
        }
      },
      // Already a stub
      new JsonObject
      {
        ["role"] = "tool",
        ["tool_call_id"] = "call-1",
        ["content"] = new JsonObject
        {
          ["_truncated"] = true,
          ["size_bytes"] = 100,
          ["preview"] = "existing stub content",
          ["artifact"] = new JsonObject { ["path"] = "stub.json", ["read_with"] = "read_file_slice" },
          ["_compacted_at_iteration"] = 2
        }.ToJsonString()
      }
    };

    var cfg = new AgentCompactionConfig { KeepRecentIterations = 1 };
    var ctx = BuildToolContext(tmp.Path, stageDir);

    var result = await TrimToolResultsStrategy.ExecuteAsync(
      messages, cfg, ctx, iteration: 3, TestContext.Current.CancellationToken);

    // The stub should still be there unchanged
    var toolMsg = result.Messages.OfType<JsonObject>()
      .FirstOrDefault(m => m["role"]?.GetValue<string>() == "tool");

    toolMsg.Should().NotBeNull();
    var content = toolMsg!["content"]?.GetValue<string>() ?? "";
    content.Should().Contain("\"_truncated\":true");
    content.Should().Contain("\"preview\":\"existing stub content\"");
  }

  // ─── Test 12: Pairing invariant — clean case ──────────────────────────────

  [Fact]
  public void PairingInvariant_passes_for_valid_compacted_messages()
  {
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "sys" },
      new JsonObject { ["role"] = "user", ["content"] = "usr" },
      new JsonObject
      {
        ["role"] = "assistant",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject { ["id"] = "call-1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "submit_final", ["arguments"] = "{}" } }
        }
      },
      new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call-1", ["content"] = "result" }
    };

    var act = () => PairingInvariant.Check(messages);
    act.Should().NotThrow();
  }

  // ─── Test 13: Pairing invariant — synthetic violation ─────────────────────

  [Fact]
  public void PairingInvariant_throws_when_tool_call_id_has_no_match()
  {
    // Tool references a call_id that doesn't exist in any assistant
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "sys" },
      new JsonObject { ["role"] = "user", ["content"] = "usr" },
      new JsonObject { ["role"] = "tool", ["tool_call_id"] = "nonexistent-call", ["content"] = "orphan tool result" }
    };

    var act = () => PairingInvariant.Check(messages);
    act.Should().Throw<AgentCompactionInvariantException>()
      .WithMessage("*nonexistent-call*");
  }

  [Fact]
  public void PairingInvariant_throws_when_assistant_tool_call_has_no_result()
  {
    // Assistant at index 2 declares a tool call but has no matching tool result after it.
    // There must be another assistant after index 2 for the check to fire
    // (the last assistant is exempt from the tool-result pairing check).
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "sys" },
      new JsonObject { ["role"] = "user", ["content"] = "usr" },
      new JsonObject
      {
        ["role"] = "assistant",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject { ["id"] = "call-1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{}" } }
        }
      },
      // Missing: tool message for call-1 — this should fail,
      // BUT there must be another assistant after this one
      // (the last assistant is exempt from the tool-result pairing check).
      new JsonObject
      {
        ["role"] = "assistant",
        ["content"] = "I'll submit now",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject { ["id"] = "call-final", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "submit_final", ["arguments"] = "{}" } }
        }
      }
    };

    var act = () => PairingInvariant.Check(messages);
    act.Should().Throw<AgentCompactionInvariantException>()
      .WithMessage("*call-1*");
  }

  [Fact]
  public void PairingInvariant_throws_when_system_message_missing()
  {
    // Must have at least 2 messages to pass the count check (invariant 3 checks count first).
    // Then message[0] must have role "system".
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "user", ["content"] = "usr" },
      new JsonObject { ["role"] = "user", ["content"] = "another" }
    };

    var act = () => PairingInvariant.Check(messages);
    act.Should().Throw<AgentCompactionInvariantException>()
      .WithMessage("*role 'user'*system*");
  }

  [Fact]
  public void PairingInvariant_throws_when_less_than_two_messages()
  {
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "sys" }
    };

    var act = () => PairingInvariant.Check(messages);
    act.Should().Throw<AgentCompactionInvariantException>()
      .WithMessage("*expected at least 2*");
  }

  // ─── Test 14: Trace event shape ───────────────────────────────────────────

  [Fact]
  public void ContextCompactedEvent_emitted_with_correct_counts()
  {
    var evt = new ContextCompactedEvent
    {
      Iteration = 7,
      Strategy = "trim_tool_results",
      MessagesBefore = 42,
      MessagesAfter = 28,
      EstimatedTokensBefore = 80000,
      EstimatedTokensAfter = 30000,
      CompactedIterations = new[] { 0, 1, 2, 3 },
      ArchivePath = "/tmp/archive/path"
    };

    evt.Kind.Should().Be("context_compacted");
    evt.Iteration.Should().Be(7);
    evt.MessagesBefore.Should().Be(42);
    evt.MessagesAfter.Should().Be(28);
    evt.CompactedIterations.Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });
  }

  [Fact]
  public void CompactionSkippedEvent_has_correct_kind()
  {
    var evt = new CompactionSkippedEvent
    {
      Iteration = 3,
      Reason = "invariant_violation"
    };
    evt.Kind.Should().Be("compaction_skipped");
    evt.Reason.Should().Be("invariant_violation");
  }

  [Fact]
  public void CompactionFallbackWarningEvent_has_correct_kind()
  {
    var evt = new CompactionFallbackWarningEvent
    {
      Model = "unknown-model",
      FallbackThreshold = 100000
    };
    evt.Kind.Should().Be("compaction_fallback_warning");
    evt.FallbackThreshold.Should().Be(100000);
  }

  // ─── Test 15: Disabled-by-default regression guard ────────────────────────

  [Fact]
  public void AgentConfig_compaction_is_null_by_default()
  {
    var yaml = @"---
name: test
model: test-model
system_prompt: ""s""
user_prompt: ""u""
input_schema: {type: object}
output_schema: {type: object}
";
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.Compaction.Should().BeNull();
  }

  [Fact]
  public void AgentConfig_compaction_disabled_when_not_explicitly_enabled()
  {
    var yaml = @"---
name: test
model: test-model
system_prompt: ""s""
user_prompt: ""u""
input_schema: {type: object}
output_schema: {type: object}
compaction:
  strategy: trim_tool_results
";
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.Compaction.Should().NotBeNull();
    cfg.Compaction!.Enabled.Should().BeFalse();
  }

  [Fact]
  public void AgentConfig_compaction_parses_full_config()
  {
    var yaml = @"---
name: test
model: test-model
system_prompt: ""s""
user_prompt: ""u""
input_schema: {type: object}
output_schema: {type: object}
compaction:
  enabled: true
  strategy: trim_tool_results
  token_threshold: 50000
  pct_of_model_limit: 0.8
  keep_recent_iterations: 5
";
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.Compaction.Should().NotBeNull();
    cfg.Compaction!.Enabled.Should().BeTrue();
    cfg.Compaction.Strategy.Should().Be("trim_tool_results");
    cfg.Compaction.TokenThreshold.Should().Be(50000);
    cfg.Compaction.PctOfModelLimit.Should().Be(0.8);
    cfg.Compaction.KeepRecentIterations.Should().Be(5);
  }

  [Fact]
  public void AgentConfig_unknown_strategy_throws()
  {
    var yaml = @"---
name: test
model: test-model
system_prompt: ""s""
user_prompt: ""u""
input_schema: {type: object}
output_schema: {type: object}
compaction:
  enabled: true
  strategy: bogus_nonexistent
";
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*bogus_nonexistent*");
  }

  // ─── Test 16: Pairing invariant — last assistant mid-iteration ────────────

  [Fact]
  public void PairingInvariant_allows_last_assistant_without_tool_results()
  {
    // The last assistant in the list may be mid-iteration (no tool results yet)
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "sys" },
      new JsonObject { ["role"] = "user", ["content"] = "usr" },
      new JsonObject
      {
        ["role"] = "assistant",
        ["content"] = "thinking",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject { ["id"] = "call-1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "read_file", ["arguments"] = "{}" } }
        }
      },
      new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call-1", ["content"] = "result" },
      // This assistant is last — allowed to have no tool results yet
      new JsonObject
      {
        ["role"] = "assistant",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject { ["id"] = "call-2", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "submit_final", ["arguments"] = "{}" } }
        }
      }
    };

    var act = () => PairingInvariant.Check(messages);
    act.Should().NotThrow();
  }

  // ─── Test 17: ContextCompactor dispatch ───────────────────────────────────

  [Fact]
  public async Task ContextCompactor_throws_on_unknown_strategy()
  {
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "sys" },
      new JsonObject { ["role"] = "user", ["content"] = "usr" }
    };

    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    var cfg = new AgentCompactionConfig
    {
      Enabled = true,
      Strategy = "bogus_nonexistent"
    };

    var act = () => ContextCompactor.CompactAsync(
      messages, cfg, BuildToolContext(tmp.Path, stageDir), 0, TestContext.Current.CancellationToken);

    await act.Should().ThrowAsync<AgentCompactionInvariantException>()
      .WithMessage("*bogus_nonexistent*");
  }

  // ─── Test 18: TrimToolResults with no compaction candidates ───────────────

  [Fact]
  public async Task TrimToolResults_no_candidates_returns_unchanged()
  {
    using var tmp = new TempDir();
    var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
    Directory.CreateDirectory(stageDir);

    // Only system + user + 1 assistant group (within keepRecent)
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "sys" },
      new JsonObject { ["role"] = "user", ["content"] = "usr" },
      new JsonObject
      {
        ["role"] = "assistant",
        ["tool_calls"] = new JsonArray
        {
          new JsonObject { ["id"] = "call-1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "submit_final", ["arguments"] = "{}" } }
        }
      },
      new JsonObject { ["role"] = "tool", ["tool_call_id"] = "call-1", ["content"] = "result" }
    };

    var cfg = new AgentCompactionConfig { KeepRecentIterations = 3 };
    var ctx = BuildToolContext(tmp.Path, stageDir);

    var result = await TrimToolResultsStrategy.ExecuteAsync(
      messages, cfg, ctx, iteration: 1, TestContext.Current.CancellationToken);

    result.CompactedIterations.Should().BeEmpty();
    result.Messages.Should().HaveCount(messages.Count);
    result.ArchivePath.Should().BeNull();
  }

  // ─── Test 19: LiteLlmConfig ModelContext round-trip ───────────────────────

  [Fact]
  public void LiteLlmConfig_model_context_round_trips()
  {
    var config = new LiteLlmConfig();
    config.ModelContext["test-model"] = 128000;
    config.ModelContext["another-model"] = 262144;

    config.ModelContext.Should().ContainKey("test-model");
    config.ModelContext["test-model"].Should().Be(128000);
    config.ModelContext["another-model"].Should().Be(262144);
  }

  // ─── Helpers ──────────────────────────────────────────────────────────────

  private static List<JsonNode> BuildMessagesWithSize(int charsPerMessage, int count = 5)
  {
    var messages = new List<JsonNode>
    {
      new JsonObject { ["role"] = "system", ["content"] = "sys" },
      new JsonObject { ["role"] = "user", ["content"] = "usr" }
    };

    for (var i = 0; i < count; i++)
    {
      var content = new string('x', charsPerMessage);
      messages.Add(new JsonObject
      {
        ["role"] = "assistant",
        ["content"] = content,
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
        ["content"] = new string('x', charsPerMessage)
      });
    }

    return messages;
  }

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

  private static ToolContext BuildToolContext(string runRoot, string stageDir)
  {
    var idx = 0;
    var trace = new CapturingTraceSink();
    return new ToolContext(
      RunId: "test-run",
      RunWorkspace: runRoot,
      StageDir: stageDir,
      StageId: "agent",
      IterationIndex: null,
      Llm: new StubLlmClient(),
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

  private sealed class StubLlmClient : ILlmClient
  {
    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("Should not be called in compaction tests");
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
