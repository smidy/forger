using System.Text.Json.Nodes;
using Forge.Core.Types;
using Json.Schema;

namespace Forge.Tools;

public interface ITool
{
  string Name { get; }
  string Description { get; }
  JsonSchema InputSchema { get; }
  JsonSchema OutputSchema { get; }

  Task<JsonNode> ExecuteAsync(JsonNode input, ToolContext ctx, CancellationToken cancellationToken);

  string TrySummarize(JsonNode input, JsonNode? output, string? error);
}
