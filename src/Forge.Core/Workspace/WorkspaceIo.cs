using System.Text;
using System.Text.Json.Nodes;
using Forge.Core.Json;

namespace Forge.Core.Workspace;

public static class WorkspaceIo
{
  public static async Task WriteJsonAtomicAsync(string path, JsonNode node, CancellationToken ct = default)
  {
    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir))
    {
      Directory.CreateDirectory(dir);
    }

    var json = node.ToJsonString(JsonSerializationDefaults.Indented);
    var tmp = path + ".tmp";
    await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
    File.Move(tmp, path, overwrite: true);
  }

  public static async Task<JsonNode?> ReadJsonAsync(string path, CancellationToken ct = default)
  {
    await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
    return await JsonNode.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
  }

  // Write a reasoning artifact for an iteration. Plaintext, non-atomic — this is
  // append-only forensic data and torn writes on cancellation are acceptable.
  // Returns the absolute path written to and the byte count for the trace event.
  // Caller has verified at least one of reasoningContent / thinkingBlocks is non-empty.
  public static async Task<(string AbsolutePath, int Bytes)> WriteReasoningArtifactAsync(
    string iterationDir,
    string? reasoningContent,
    JsonArray? thinkingBlocks,
    CancellationToken ct = default)
  {
    Directory.CreateDirectory(iterationDir);
    var absolute = Path.GetFullPath(Path.Combine(iterationDir, "reasoning.txt"));

    var initialCapacity = (reasoningContent?.Length ?? 0) + (thinkingBlocks?.Count ?? 0) * 512 + 64;
    var sb = new StringBuilder(initialCapacity);
    if (!string.IsNullOrEmpty(reasoningContent))
    {
      sb.Append(reasoningContent);
    }

    if (thinkingBlocks is { Count: > 0 })
    {
      if (sb.Length > 0)
      {
        if (sb[^1] != '\n')
        {
          sb.Append('\n');
        }
        sb.Append("\n--- thinking_blocks ---\n");
      }

      for (var i = 0; i < thinkingBlocks.Count; i++)
      {
        var block = thinkingBlocks[i];
        var signature = block?["signature"]?.GetValue<string>() ?? "";
        var thinking = block?["thinking"]?.GetValue<string>() ?? "";
        if (i > 0)
        {
          sb.Append("\n\n");
        }

        sb.Append("--- block ").Append(i + 1).Append(" (signature: ").Append(signature).Append(") ---\n");
        sb.Append(thinking);
      }
    }

    var bytes = Encoding.UTF8.GetBytes(sb.ToString());
    await File.WriteAllBytesAsync(absolute, bytes, ct).ConfigureAwait(false);
    return (absolute, bytes.Length);
  }
}
