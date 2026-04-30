using System;
using System.Collections.Generic;
using System.IO;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Agent;
using Forge.Core.Exceptions;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Agent.Tests;

public class AgentResumeTests
{
    [Fact]
    public void AgentSnapshotLoader_TryLoadLatest_returns_null_when_no_snapshots()
    {
        using var tmp = new TempDir();
        var stageDir = WorkspacePaths.StageDir(tmp.Path, "stage");
        Directory.CreateDirectory(stageDir);

        var result = AgentSnapshotLoader.TryLoadLatest(stageDir);
        result.Should().BeNull();
    }

    [Fact]
    public void AgentSnapshotLoader_TryLoadLatest_picks_highest_iteration()
    {
        using var tmp = new TempDir();
        var stageDir = WorkspacePaths.StageDir(tmp.Path, "stage");
        Directory.CreateDirectory(stageDir);
        var iterationsDir = Path.Combine(stageDir, "iterations");
        Directory.CreateDirectory(iterationsDir);

        // Create iteration 0 snapshot
        var iter0Dir = Path.Combine(iterationsDir, "000");
        Directory.CreateDirectory(iter0Dir);
        var snapshot0 = new JsonObject
        {
            ["iter"] = 0,
            ["nudged"] = false,
            ["messages"] = new JsonArray(),
            ["ledger"] = new JsonArray()
        };
        File.WriteAllText(Path.Combine(iter0Dir, "state.json"), snapshot0.ToJsonString());

        // Create iteration 5 snapshot (higher)
        var iter5Dir = Path.Combine(iterationsDir, "005");
        Directory.CreateDirectory(iter5Dir);
        var snapshot5 = new JsonObject
        {
            ["iter"] = 5,
            ["nudged"] = true,
            ["messages"] = new JsonArray(),
            ["ledger"] = new JsonArray()
        };
        File.WriteAllText(Path.Combine(iter5Dir, "state.json"), snapshot5.ToJsonString());

        // Create iteration 3 snapshot (middle)
        var iter3Dir = Path.Combine(iterationsDir, "003");
        Directory.CreateDirectory(iter3Dir);
        var snapshot3 = new JsonObject
        {
            ["iter"] = 3,
            ["nudged"] = false,
            ["messages"] = new JsonArray(),
            ["ledger"] = new JsonArray()
        };
        File.WriteAllText(Path.Combine(iter3Dir, "state.json"), snapshot3.ToJsonString());

        var result = AgentSnapshotLoader.TryLoadLatest(stageDir);
        result.Should().NotBeNull();
        result!.StartingIter.Should().Be(6); // snapshot iter 5 -> starting iter 6
        result.Nudged.Should().BeTrue();
    }

    [Fact]
    public void AgentSnapshotLoader_TryLoadLatest_skips_corrupted_json()
    {
        using var tmp = new TempDir();
        var stageDir = WorkspacePaths.StageDir(tmp.Path, "stage");
        Directory.CreateDirectory(stageDir);
        var iterationsDir = Path.Combine(stageDir, "iterations");
        Directory.CreateDirectory(iterationsDir);

        var iter0Dir = Path.Combine(iterationsDir, "000");
        Directory.CreateDirectory(iter0Dir);
        File.WriteAllText(Path.Combine(iter0Dir, "state.json"), "{ invalid json");

        var iter1Dir = Path.Combine(iterationsDir, "001");
        Directory.CreateDirectory(iter1Dir);
        var snapshot1 = new JsonObject
        {
            ["iter"] = 1,
            ["nudged"] = false,
            ["messages"] = new JsonArray(),
            ["ledger"] = new JsonArray()
        };
        File.WriteAllText(Path.Combine(iter1Dir, "state.json"), snapshot1.ToJsonString());

        var result = AgentSnapshotLoader.TryLoadLatest(stageDir);
        result.Should().NotBeNull();
        result!.StartingIter.Should().Be(2); // snapshot iter 1 -> starting iter 2
    }

    [Fact]
    public void AgentSnapshotLoader_TryLoadLatest_adjusts_starting_iter()
    {
        using var tmp = new TempDir();
        var stageDir = WorkspacePaths.StageDir(tmp.Path, "stage");
        Directory.CreateDirectory(stageDir);
        var iterationsDir = Path.Combine(stageDir, "iterations");
        Directory.CreateDirectory(iterationsDir);

        var iter7Dir = Path.Combine(iterationsDir, "007");
        Directory.CreateDirectory(iter7Dir);
        var snapshot = new JsonObject
        {
            ["iter"] = 7,
            ["nudged"] = true,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "sys" }),
            ["ledger"] = new JsonArray()
        };
        File.WriteAllText(Path.Combine(iter7Dir, "state.json"), snapshot.ToJsonString());

        var result = AgentSnapshotLoader.TryLoadLatest(stageDir);
        result.Should().NotBeNull();
        result!.StartingIter.Should().Be(8);
        result.Nudged.Should().BeTrue();
        result.Messages.Should().HaveCount(1);
        result.LedgerEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task AgentRunner_resume_seeds_messages_nudged_ledger()
    {
        using var tmp = new TempDir();
        var runRoot = Path.Combine(tmp.Path, "run");
        var repoRoot = Path.Combine(tmp.Path, "repo");
        Directory.CreateDirectory(runRoot);
        Directory.CreateDirectory(repoRoot);
        var stageDir = WorkspacePaths.StageDir(runRoot, "agent");
        Directory.CreateDirectory(stageDir);

        // Create a fake snapshot with some messages and a ledger entry
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "sys" },
            new JsonObject { ["role"] = "user", ["content"] = "usr" },
            new JsonObject { ["role"] = "assistant", ["content"] = "thinking" }
        };
        var ledgerEntries = new[]
        {
            new AgentWriteRecord(
                ToolName: "apply_patch",
                RequestedPath: "src/Foo.cs",
                ResolvedPath: Path.Combine(repoRoot, "src", "Foo.cs"),
                RootCategory: "repo-root",
                BytesWritten: 123,
                WasNoOp: false)
        };
        var resumeState = new AgentResumeState
        {
            StartingIter = 5,
            Messages = messages.Select(m => m!.DeepClone()).ToList(),
            Nudged = true,
            LedgerEntries = ledgerEntries
        };

        // Mock LLM that submits final declaring the seeded ledger entry.
        var llm = new FakeLlmClient(SubmitFinalWithFilesModified(new[] { "src/Foo.cs" }));
        var trace = new CapturingTraceSink();
        var ctx = BuildContext(runRoot, stageDir, llm, trace, repoRoot);
        var cfg = BuildAgentConfig(maxIterations: 10);

        var result = await AgentRunner.RunAsync(
            cfg,
            new JsonObject(),
            ctx,
            new ToolRegistry(),
            agentYamlPath: null,
            TestContext.Current.CancellationToken,
            resumeState: resumeState);

        result["done"]!.GetValue<bool>().Should().BeTrue();

        // Verify that the ledger was seeded (diff verification passes)
        var diffEvent = trace.Events.OfType<AgentDiffVerificationEvent>().Single();
        diffEvent.Verdict.Should().Be("pass");
        diffEvent.ActuallyWritten.Should().ContainSingle();
    }

    [Fact]
    public async Task AgentRunner_resume_starts_at_starting_iter()
    {
        using var tmp = new TempDir();
        var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
        Directory.CreateDirectory(stageDir);

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "sys" },
            new JsonObject { ["role"] = "user", ["content"] = "usr" }
        };
        var resumeState = new AgentResumeState
        {
            StartingIter = 10,
            Messages = messages.Select(m => m!.DeepClone()).ToList(),
            Nudged = false,
            LedgerEntries = Array.Empty<AgentWriteRecord>()
        };

        // Mock LLM that returns a tool call (not submit_final) to force another iteration
        // We'll capture the iteration index from the request
        var llm = new FakeLlmClient(SubmitFinalOnly());
        var trace = new CapturingTraceSink();
        var ctx = BuildContext(tmp.Path, stageDir, llm, trace);
        var cfg = BuildAgentConfig(maxIterations: 20);

        var result = await AgentRunner.RunAsync(
            cfg,
            new JsonObject(),
            ctx,
            new ToolRegistry(),
            agentYamlPath: null,
            TestContext.Current.CancellationToken,
            resumeState: resumeState);

        // The first request should have the seeded messages (system+user)
        llm.Requests.Should().HaveCount(1);
        llm.MessageCountsAtCall[0].Should().Be(2); // system + user snapshotted at call time
        // The iteration event should be for iteration 10
        var iterEvent = trace.Events.OfType<AgentIterationEvent>().Single();
        iterEvent.Index.Should().Be(10);
    }

    [Fact]
    public async Task AgentRunner_resume_with_maxIterationsOverride()
    {
        using var tmp = new TempDir();
        var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
        Directory.CreateDirectory(stageDir);

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "sys" },
            new JsonObject { ["role"] = "user", ["content"] = "usr" }
        };
        var resumeState = new AgentResumeState
        {
            StartingIter = 5,
            Messages = messages.Select(m => m!.DeepClone()).ToList(),
            Nudged = false,
            LedgerEntries = Array.Empty<AgentWriteRecord>()
        };

        // Create a tool registry with a no-op tool that the agent can call
        var tools = new ToolRegistry();
        tools.Register(new NoopTool());

        // Mock LLM that returns a call to the no-op tool each time (no submit_final)
        var llm = new FakeLlmClient(NoopToolCall());
        var trace = new CapturingTraceSink();
        var ctx = BuildContext(tmp.Path, stageDir, llm, trace);
        var cfg = BuildAgentConfig(maxIterations: 10); // config max 10, but we'll override to 8

        // Override max iterations to 8, starting iter = 5 => allowed iterations: 5,6,7 (3 calls)
        Func<Task> act = () => AgentRunner.RunAsync(
            cfg,
            new JsonObject(),
            ctx,
            tools,
            agentYamlPath: null,
            TestContext.Current.CancellationToken,
            resumeState: resumeState,
            maxIterationsOverride: 8);

        // Should throw AgentException because max iterations (8) reached without submit_final
        var ex = await act.Should().ThrowAsync<AgentException>();
        ex.Which.Message.Should().Contain("MaxIterations (8) exceeded");

        // Should have made exactly 3 LLM calls (iterations 5,6,7)
        llm.Requests.Should().HaveCount(3);
        // Verify iteration events
        var iterEvents = trace.Events.OfType<AgentIterationEvent>().ToList();
        iterEvents.Should().HaveCount(3);
        iterEvents.Select(e => e.Index).Should().Equal(5, 6, 7);
        // Snapshot should have been written for each iteration
        var snapshotEvents = trace.Events.OfType<AgentStateSnapshotEvent>().ToList();
        snapshotEvents.Should().HaveCount(3);
        snapshotEvents.Select(e => e.Iteration).Should().Equal(5, 6, 7);
    }

    [Fact]
    public async Task AgentRunner_resume_skips_input_validation()
    {
        using var tmp = new TempDir();
        var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
        Directory.CreateDirectory(stageDir);

        var resumeState = new AgentResumeState
        {
            StartingIter = 0,
            Messages = new List<JsonNode>
            {
                new JsonObject { ["role"] = "system", ["content"] = "sys" },
                new JsonObject { ["role"] = "user", ["content"] = "usr" }
            },
            Nudged = false,
            LedgerEntries = Array.Empty<AgentWriteRecord>()
        };

        // AgentConfig with input schema that would reject empty object
        var cfg = BuildAgentConfig(inputSchema: new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["requiredField"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("requiredField")
        });

        // Should not throw validation exception because resume skips input validation
        var llm = new FakeLlmClient(SubmitFinalOnly());
        var trace = new CapturingTraceSink();
        var ctx = BuildContext(tmp.Path, stageDir, llm, trace);

        Func<Task> act = () => AgentRunner.RunAsync(
            cfg,
            new JsonObject(), // missing requiredField
            ctx,
            new ToolRegistry(),
            agentYamlPath: null,
            TestContext.Current.CancellationToken,
            resumeState: resumeState);

        await act.Should().NotThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task AgentRunner_writes_state_snapshot_on_submit_final_iteration()
    {
        // Regression: the iteration that calls submit_final must persist its
        // state.json snapshot. Pre-fix, WriteStateSnapshotAsync was only invoked
        // at end-of-iter (line 440 of AgentRunner.cs), and the submit_final
        // branch returned early at line 350 — leaving the terminal iter with
        // reasoning.txt but no state.json. analyse-reasoning.ps1 (which keys
        // off the highest-numbered state.json) silently returned no analysis,
        // and forge resume's snapshot loader had to fall back to iter N-1.
        using var tmp = new TempDir();
        var stageDir = WorkspacePaths.StageDir(tmp.Path, "agent");
        Directory.CreateDirectory(stageDir);

        var llm = new FakeLlmClient(SubmitFinalOnly());
        var trace = new CapturingTraceSink();
        var ctx = BuildContext(tmp.Path, stageDir, llm, trace);
        var cfg = BuildAgentConfig();

        var result = await AgentRunner.RunAsync(
            cfg,
            new JsonObject(),
            ctx,
            new ToolRegistry(),
            agentYamlPath: null,
            TestContext.Current.CancellationToken);

        result["done"]!.GetValue<bool>().Should().BeTrue();

        // Disk: state.json present for the submit_final iter (iter 0 in this
        // single-call scenario).
        var snapshotPath = Path.Combine(WorkspacePaths.IterationDir(stageDir, 0), "state.json");
        File.Exists(snapshotPath).Should().BeTrue(
            "the iteration that calls submit_final must persist state.json so analysers and `forge resume` can read the terminal conversation");

        // Trace event corroborates: exactly one AgentStateSnapshotEvent for iter 0.
        var snapshotEvents = trace.Events.OfType<AgentStateSnapshotEvent>().ToList();
        snapshotEvents.Should().ContainSingle();
        snapshotEvents[0].Iteration.Should().Be(0);

        // Snapshot content is well-formed JSON and carries the assistant's
        // submit_final message — the state analysers actually consume.
        var snapshotJson = await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken);
        var snapshot = JsonNode.Parse(snapshotJson)!.AsObject();
        snapshot["iter"]!.GetValue<int>().Should().Be(0);
        snapshot["messages"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public async Task AgentRunner_resume_ledger_survives_diff_verification()
    {
        using var tmp = new TempDir();
        var runRoot = Path.Combine(tmp.Path, "run");
        var repoRoot = Path.Combine(tmp.Path, "repo");
        Directory.CreateDirectory(runRoot);
        Directory.CreateDirectory(repoRoot);
        var stageDir = WorkspacePaths.StageDir(runRoot, "agent");
        Directory.CreateDirectory(stageDir);

        // Create a ledger entry for a file write
        var ledgerEntries = new[]
        {
            new AgentWriteRecord(
                ToolName: "apply_patch",
                RequestedPath: "src/Bar.cs",
                ResolvedPath: Path.Combine(repoRoot, "src", "Bar.cs"),
                RootCategory: "repo-root",
                BytesWritten: 456,
                WasNoOp: false)
        };
        var resumeState = new AgentResumeState
        {
            StartingIter = 0,
            Messages = new List<JsonNode>
            {
                new JsonObject { ["role"] = "system", ["content"] = "sys" },
                new JsonObject { ["role"] = "user", ["content"] = "usr" }
            },
            Nudged = false,
            LedgerEntries = ledgerEntries
        };

        var trace = new CapturingTraceSink();
        var cfg = BuildAgentConfig();
        var llm = new FakeLlmClient(SubmitFinalWithFilesModified(new[] { "src/Bar.cs" }));
        var ctx = BuildContext(runRoot, stageDir, llm, trace, repoRoot);

        var result = await AgentRunner.RunAsync(
            cfg,
            new JsonObject(),
            ctx,
            new ToolRegistry(),
            agentYamlPath: null,
            TestContext.Current.CancellationToken,
            resumeState: resumeState);

        var diffEvent = trace.Events.OfType<AgentDiffVerificationEvent>().Single();
        diffEvent.Verdict.Should().Be("pass");
        diffEvent.ActuallyWritten.Should().Contain("src/Bar.cs");
        diffEvent.Declared.Should().Contain("src/Bar.cs");
    }

    // Helper methods from AgentRunnerReasoningTests
    private static AgentConfig BuildAgentConfig(int maxIterations = 4, JsonNode? inputSchema = null) => new()
    {
        Name = "test",
        Model = "test-model",
        SystemPrompt = "s",
        UserPrompt = "u",
        MaxIterations = maxIterations,
        Tools = new(),
        InputSchema = inputSchema ?? new JsonObject { ["type"] = "object" },
        OutputSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["done"] = new JsonObject { ["type"] = "boolean" } },
            ["required"] = new JsonArray("done")
        },
        InjectProjectContext = false,
        InjectSkillsCatalog = false,
        DiffVerification = new AgentDiffVerificationConfig { Enabled = true }
    };

    private static ToolContext BuildContext(string runRoot, string stageDir, ILlmClient llm, ITraceSink trace, string? repoRoot = null)
    {
        var idx = 0;
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

    private static CompletionResponse SubmitFinalOnly() => new()
    {
        Id = "r",
        Choices = new()
        {
            new CompletionChoice
            {
                Index = 0,
                FinishReason = "tool_calls",
                Message = new ChatMessagePayload
                {
                    Role = "assistant",
                    ToolCalls = new()
                    {
                        new ToolCallPayload
                        {
                            Id = "call-1",
                            Type = "function",
                            Function = new FunctionCallPayload { Name = "submit_final", Arguments = "{\"done\": true}" }
                        }
                    }
                }
            }
        },
        Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
    };

    private static CompletionResponse SubmitFinalWithFilesModified(IEnumerable<string> files) => new()
    {
        Id = "r",
        Choices = new()
        {
            new CompletionChoice
            {
                Index = 0,
                FinishReason = "tool_calls",
                Message = new ChatMessagePayload
                {
                    Role = "assistant",
                    ToolCalls = new()
                    {
                        new ToolCallPayload
                        {
                            Id = "call-1",
                            Type = "function",
                            Function = new FunctionCallPayload
                            {
                                Name = "submit_final",
                                Arguments = JsonSerializer.Serialize(new { done = true, files_modified = files })
                            }
                        }
                    }
                }
            }
        },
        Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
    };

    private static CompletionResponse NoopToolCall() => new()
    {
        Id = "r",
        Choices = new()
        {
            new CompletionChoice
            {
                Index = 0,
                FinishReason = "tool_calls",
                Message = new ChatMessagePayload
                {
                    Role = "assistant",
                    ToolCalls = new()
                    {
                        new ToolCallPayload
                        {
                            Id = "call-1",
                            Type = "function",
                            Function = new FunctionCallPayload
                            {
                                Name = "noop",
                                Arguments = "{}"
                            }
                        }
                    }
                }
            }
        },
        Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
    };

    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly CompletionResponse _response;
        public ConcurrentBag<CompletionRequest> Requests { get; } = new();
        public List<int> MessageCountsAtCall { get; } = new();

        public FakeLlmClient(CompletionResponse response) => _response = response;

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            // Snapshot message count before returning — AgentRunner mutates the list post-call.
            MessageCountsAtCall.Add(request.Messages.Count);
            Requests.Add(request);
            return Task.FromResult(_response);
        }
    }

    private sealed class CapturingTraceSink : ITraceSink
    {
        public List<TraceEvent> Events { get; } = new();
        public void Trace(TraceEvent e) => Events.Add(e);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopTool : ToolBase<JsonObject, JsonObject>
    {
        public override string Name => "noop";
        public override string Description => "A tool that does nothing.";

        protected override Task<JsonObject> ExecuteCoreAsync(JsonObject input, ToolContext ctx, CancellationToken cancellationToken)
            => Task.FromResult(new JsonObject());
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