using System.Text;
using System.Text.Json.Nodes;
using Forge.Core.Trace;
using Forge.Core.Types;

namespace Forge.Agent;

public static class ToolResultCapper
{
  public const int CapBytes = 50 * 1024;
  public const int PreviewChars = 2000;

  public static async Task<JsonNode> CapAsync(JsonNode fullResult, ToolContext ctx, CancellationToken ct)
  {
    var json = fullResult.ToJsonString();
    var bytes = Encoding.UTF8.GetByteCount(json);
    if (bytes <= CapBytes)
    {
      return fullResult;
    }

    var dir = Path.Combine(ctx.StageDir, "tool-outputs");
    Directory.CreateDirectory(dir);
    var idx = ctx.NextToolOutputIdx();
    var rel = Path.Combine("tool-outputs", $"{idx:D4}.json");
    var path = Path.Combine(ctx.StageDir, rel);
    await File.WriteAllTextAsync(path, json, Encoding.UTF8, ct).ConfigureAwait(false);

    ctx.Trace.Trace(new ToolResultTruncatedEvent
    {
      StageId = ctx.StageId,
      Bytes = bytes,
      ArtifactPath = rel
    });

    return BuildStubPayload(json, bytes, rel);
  }

  /// <summary>
  /// Build the disk-backed stub payload used for truncated tool results.
  /// Shared with <c>TrimToolResultsStrategy</c> so the stub schema stays
  /// single-sourced — a future change to the stub shape must only happen here.
  /// </summary>
  internal static JsonObject BuildStubPayload(string originalContent, int sizeBytes, string artifactRelPath)
  {
    var preview = originalContent.Length <= PreviewChars ? originalContent : originalContent[..PreviewChars];
    return new JsonObject
    {
      ["_truncated"] = true,
      ["size_bytes"] = sizeBytes,
      ["preview"] = preview,
      ["artifact"] = new JsonObject
      {
        ["path"] = artifactRelPath,
        ["read_with"] = "read_file_slice"
      }
    };
  }
}
