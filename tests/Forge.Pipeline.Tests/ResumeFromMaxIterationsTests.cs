using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluentAssertions;
using Forge.Agent;
using Forge.Core.Exceptions;
using Forge.Core.Llm;
using Forge.Core.Trace;
using Forge.Core.Types;
using Forge.Core.Workspace;
using Forge.Pipeline;
using Forge.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Pipeline.Tests;

public class ResumeFromMaxIterationsTests
{
    [Fact]
    public async Task Resume_with_maxIterationsOverride_completes_stage()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpForgeHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(tmpForgeHome, "runs"));
        var agentsDir = Path.Combine(tmpForgeHome, "agents");
        Directory.CreateDirectory(agentsDir);

        try
        {
            // Create a simple agent that never calls submit_final (max_iterations = 5)
            var agentYaml = @"
name: test-agent
model: test-model
system_prompt: You are a test agent.
user_prompt: Do something.
max_iterations: 5
tools: []
input_schema: { ""type"": ""object"" }
output_schema:
  type: object
  properties:
    done:
      type: boolean
  required: [done]
";
            var agentPath = Path.Combine(agentsDir, "test-agent.agent.yaml");
            await File.WriteAllTextAsync(agentPath, agentYaml, ct);

            // Create a pipeline with three stages: tool -> agent -> tool
            var pipeline = new PipelineConfig
            {
                Name = "resume-test",
                Stages = new List<StageConfig>
                {
                    new()
                    {
                        Id = "stage1",
                        Tool = "noop",
                        Input = new JsonObject { ["value"] = "first" }
                    },
                    new()
                    {
                        Id = "stage2",
                        Agent = "test-agent",
                        DependsOn = new List<string> { "stage1" }
                    },
                    new()
                    {
                        Id = "stage3",
                        Tool = "noop",
                        DependsOn = new List<string> { "stage2" },
                        Input = new JsonObject { ["value"] = "third" }
                    }
                }
            };

            // Mock LLM that returns a tool call (noop) each time, never submit_final
            var llm = new ProgrammableLlmClient();
            llm.SetResponse(iteration => new CompletionResponse
            {
                Id = $"iter-{iteration}",
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
                                    Id = $"call-{iteration}",
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
            });

            var tools = new ToolRegistry();
            tools.Register(new NoopTool());

            // First run: should fail because stage2 hits max_iterations (5)
            var pipelineInput = new JsonObject();
            Func<Task> firstRun = () => PipelineExecutor.RunAsync(
                pipeline,
                pipelineInput,
                tmpForgeHome,
                llm,
                tools,
                NullLoggerFactory.Instance,
                ct);

            var ex = await firstRun.Should().ThrowAsync<AgentException>();
            ex.Which.Message.Should().Contain("MaxIterations (5) exceeded");

            // Find the run directory
            var runsDir = Path.Combine(tmpForgeHome, "runs");
            var runDirs = Directory.GetDirectories(runsDir);
            runDirs.Should().HaveCount(1);
            var runRoot = runDirs[0];
            var runId = Path.GetFileName(runRoot);

            // Verify stage2 has snapshots for iterations 0-4
            var stage2Dir = WorkspacePaths.StageDir(runRoot, "stage2");
            var iterationsDir = Path.Combine(stage2Dir, "iterations");
            Directory.Exists(iterationsDir).Should().BeTrue();
            var iterationSubdirs = Directory.GetDirectories(iterationsDir);
            iterationSubdirs.Should().HaveCount(5); // 000 through 004
            foreach (var subdir in iterationSubdirs)
            {
                var statePath = Path.Combine(subdir, "state.json");
                File.Exists(statePath).Should().BeTrue();
            }

            // Diagnostic: verify snapshot loader finds stage2's snapshots before resume.
            var preResumeLoad = AgentSnapshotLoader.TryLoadLatest(stage2Dir);
            preResumeLoad.Should().NotBeNull("loader must find written snapshots for resume to work");
            preResumeLoad!.StartingIter.Should().Be(5);

            // Now resume with increased max iterations (10)
            llm.ResetCallCount();
            // Return submit_final on the first post-reset call (agent iter 5).
            llm.SetResponse(iteration => iteration == 0
                ? new CompletionResponse
                {
                    Id = "final",
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
                                        Id = "final-call",
                                        Type = "function",
                                        Function = new FunctionCallPayload
                                        {
                                            Name = "submit_final",
                                            Arguments = "{\"done\": true}"
                                        }
                                    }
                                }
                            }
                        }
                    },
                    Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
                }
                : new CompletionResponse
                {
                    Id = $"iter-{iteration}",
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
                                        Id = $"call-{iteration}",
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
                });

            var resumeResult = await PipelineExecutor.ResumeAsync(
                runId,
                runRoot,
                pipeline,
                await Resumer.HydrateAsync(runRoot, pipeline, tools, tmpForgeHome, force: false, ct, maxIterationsOverride: 10),
                tmpForgeHome,
                llm,
                tools,
                NullLoggerFactory.Instance,
                ct,
                maxIterationsOverride: 10);

            // Stage2 should have completed (submit_final at iteration 5)
            // Verify that only one additional LLM call was made (iteration 5)
            llm.CallCount.Should().Be(1);
            // Stage1 and stage3 should have been untouched (already completed)
            // Verify stage3 output exists
            var stage3OutputPath = WorkspacePaths.StageOutputPath(WorkspacePaths.StageDir(runRoot, "stage3"));
            File.Exists(stage3OutputPath).Should().BeTrue();

            // Verify trace contains StageResumedFromIterEvent
            var tracePath = WorkspacePaths.TracePath(runRoot);
            var traceLines = await File.ReadAllLinesAsync(tracePath, ct);
            var resumedEvent = traceLines.Select(l => JsonNode.Parse(l))
                .FirstOrDefault(n => n?["kind"]?.GetValue<string>() == "stage_resumed_from_iter");
            resumedEvent.Should().NotBeNull();
            resumedEvent!["stageId"]!.GetValue<string>().Should().Be("stage2");
            resumedEvent["fromIter"]!.GetValue<int>().Should().Be(5); // starting iteration after snapshot 4
        }
        finally
        {
            Directory.Delete(tmpForgeHome, recursive: true);
        }
    }

    [Fact]
    public async Task RestartStage_flag_discards_snapshots()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpForgeHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(tmpForgeHome, "runs"));
        var agentsDir = Path.Combine(tmpForgeHome, "agents");
        Directory.CreateDirectory(agentsDir);

        try
        {
            var agentYaml = @"
name: test-agent
model: test-model
system_prompt: You are a test agent.
user_prompt: Do something.
max_iterations: 3
tools: []
input_schema: { ""type"": ""object"" }
output_schema:
  type: object
  properties:
    done:
      type: boolean
  required: [done]
";
            var agentPath = Path.Combine(agentsDir, "test-agent.agent.yaml");
            await File.WriteAllTextAsync(agentPath, agentYaml, ct);

            var pipeline = new PipelineConfig
            {
                Name = "restart-test",
                Stages = new List<StageConfig>
                {
                    new()
                    {
                        Id = "stage1",
                        Agent = "test-agent"
                    }
                }
            };

            var llm = new ProgrammableLlmClient();
            llm.SetResponse(_ => new CompletionResponse
            {
                Id = "iter",
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
                                    Id = "call",
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
            });

            var tools = new ToolRegistry();
            tools.Register(new NoopTool());

            var pipelineInput = new JsonObject();
            Func<Task> firstRun = () => PipelineExecutor.RunAsync(
                pipeline,
                pipelineInput,
                tmpForgeHome,
                llm,
                tools,
                NullLoggerFactory.Instance,
                ct);

            await firstRun.Should().ThrowAsync<AgentException>();

            var runsDir = Path.Combine(tmpForgeHome, "runs");
            var runRoot = Directory.GetDirectories(runsDir).Single();
            var runId = Path.GetFileName(runRoot);

            // Verify snapshots exist
            var stageDir = WorkspacePaths.StageDir(runRoot, "stage1");
            var iterationsDir = Path.Combine(stageDir, "iterations");
            Directory.Exists(iterationsDir).Should().BeTrue();

            // Now resume with --restart-stage stage1
            llm.ResetCallCount();
            // This time let the agent submit_final immediately
            llm.SetResponse(_ => new CompletionResponse
            {
                Id = "final",
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
                                    Id = "final-call",
                                    Type = "function",
                                    Function = new FunctionCallPayload
                                    {
                                        Name = "submit_final",
                                        Arguments = "{\"done\": true}"
                                    }
                                }
                            }
                        }
                    }
                },
                Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
            });

            var resumeResult = await PipelineExecutor.ResumeAsync(
                runId,
                runRoot,
                pipeline,
                await Resumer.HydrateAsync(runRoot, pipeline, tools, tmpForgeHome, force: false, ct, restartStages: new[] { "stage1" }),
                tmpForgeHome,
                llm,
                tools,
                NullLoggerFactory.Instance,
                ct,
                restartStages: new[] { "stage1" });

            // The stage should have started fresh (iteration 0) and completed
            llm.CallCount.Should().Be(1);
            // Verify trace contains StageRestartedByFlagEvent
            var tracePath = WorkspacePaths.TracePath(runRoot);
            var traceLines = await File.ReadAllLinesAsync(tracePath, ct);
            var restartEvent = traceLines.Select(l => JsonNode.Parse(l))
                .FirstOrDefault(n => n?["kind"]?.GetValue<string>() == "stage_restarted_by_flag");
            restartEvent.Should().NotBeNull();
            restartEvent!["stageId"]!.GetValue<string>().Should().Be("stage1");
        }
        finally
        {
            Directory.Delete(tmpForgeHome, recursive: true);
        }
    }

    [Fact]
    public async Task No_resume_path_adds_only_snapshot_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpForgeHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(tmpForgeHome, "runs"));
        var agentsDir = Path.Combine(tmpForgeHome, "agents");
        Directory.CreateDirectory(agentsDir);

        try
        {
            var agentYaml = @"
name: test-agent
model: test-model
system_prompt: You are a test agent.
user_prompt: Do something.
max_iterations: 10
tools: []
input_schema: { ""type"": ""object"" }
output_schema:
  type: object
  properties:
    done:
      type: boolean
  required: [done]
";
            var agentPath = Path.Combine(agentsDir, "test-agent.agent.yaml");
            await File.WriteAllTextAsync(agentPath, agentYaml, ct);

            var pipeline = new PipelineConfig
            {
                Name = "snapshot-test",
                Stages = new List<StageConfig>
                {
                    new()
                    {
                        Id = "stage1",
                        Agent = "test-agent"
                    }
                }
            };

            var llm = new ProgrammableLlmClient();
            int callCount = 0;
            llm.SetResponse(iteration =>
            {
                callCount++;
                // Submit final on iteration 2
                if (iteration == 2)
                {
                    return new CompletionResponse
                    {
                        Id = "final",
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
                                            Id = "final-call",
                                            Type = "function",
                                            Function = new FunctionCallPayload
                                            {
                                                Name = "submit_final",
                                                Arguments = "{\"done\": true}"
                                            }
                                        }
                                    }
                                }
                            }
                        },
                        Usage = new UsagePayload { PromptTokens = 1, CompletionTokens = 1 }
                    };
                }
                // Otherwise return a noop tool call
                return new CompletionResponse
                {
                    Id = $"iter-{iteration}",
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
                                        Id = $"call-{iteration}",
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
            });

            var tools = new ToolRegistry();
            tools.Register(new NoopTool());

            var pipelineInput = new JsonObject();
            var result = await PipelineExecutor.RunAsync(
                pipeline,
                pipelineInput,
                tmpForgeHome,
                llm,
                tools,
                NullLoggerFactory.Instance,
                ct);

            // Verify snapshots were written for every iteration including iter 2 (the
            // submit_final iter). Pre-fix the submit_final branch returned before
            // WriteStateSnapshotAsync ran, leaving the terminal iter without state.json
            // — see AgentRunner.cs and the regression test
            // AgentResumeTests.AgentRunner_writes_state_snapshot_on_submit_final_iteration.
            var runsDir = Path.Combine(tmpForgeHome, "runs");
            var runRoot = Directory.GetDirectories(runsDir).Single();
            var stageDir = WorkspacePaths.StageDir(runRoot, "stage1");
            var iterationsDir = Path.Combine(stageDir, "iterations");
            Directory.Exists(iterationsDir).Should().BeTrue();
            var iterationSubdirs = Directory.GetDirectories(iterationsDir);
            iterationSubdirs.Should().HaveCount(3); // 000, 001, 002
            foreach (var subdir in iterationSubdirs)
            {
                File.Exists(Path.Combine(subdir, "state.json")).Should().BeTrue();
            }

            // Verify trace contains AgentStateSnapshotEvent for iterations 0, 1, 2
            var tracePath = WorkspacePaths.TracePath(runRoot);
            var traceLines = await File.ReadAllLinesAsync(tracePath, ct);
            var snapshotEvents = traceLines.Select(l => JsonNode.Parse(l))
                .Where(n => n?["kind"]?.GetValue<string>() == "agent_state_snapshot")
                .ToList();
            snapshotEvents.Should().HaveCount(3);
            snapshotEvents.Select(e => e!["iteration"]!.GetValue<int>()).Should().Equal(0, 1, 2);
        }
        finally
        {
            Directory.Delete(tmpForgeHome, recursive: true);
        }
    }

    private sealed class NoopTool : ToolBase<JsonObject, JsonObject>
    {
        public override string Name => "noop";
        public override string Description => "A tool that does nothing.";

        protected override Task<JsonObject> ExecuteCoreAsync(JsonObject input, ToolContext ctx, CancellationToken cancellationToken)
            => Task.FromResult(new JsonObject());
    }

    private sealed class ProgrammableLlmClient : ILlmClient
    {
        private readonly ConcurrentDictionary<int, CompletionResponse> _responses = new();
        private Func<int, CompletionResponse>? _responseFunc;
        private int _callCount;

        public int CallCount => _callCount;

        public void SetResponse(Func<int, CompletionResponse> responseFunc)
        {
            _responseFunc = responseFunc;
        }

        public void ResetCallCount()
        {
            _callCount = 0;
        }

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            var iter = _callCount;
            _callCount++;
            var response = _responseFunc?.Invoke(iter) ?? throw new InvalidOperationException("No response configured");
            return Task.FromResult(response);
        }
    }
}