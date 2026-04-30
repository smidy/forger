using System.Text;
using System.Text.Json.Nodes;
using Forge.Core.Trace;

namespace Forge.Core.Filesystem;

/// <summary>
/// Loads <c>AGENTS.md</c> then <c>CLAUDE.md</c> from each ordered root, with per-file size caps.
/// </summary>
public static class ProjectMarkdownLoader
{
  public const int DefaultMaxBytesPerFile = 256 * 1024;

  private const string BetweenFiles = "\n\n---\n\n";
  private const string BetweenRoots = "\n\n========\n\n";

  public static string LoadOrderedRoots(
    IReadOnlyList<string> roots,
    int maxBytesPerFile,
    ITraceSink? trace)
  {
    if (roots.Count == 0)
    {
      return "";
    }

    var rootBlocks = new List<string>();
    foreach (var root in roots)
    {
      var block = LoadOneRoot(root, maxBytesPerFile, trace);
      if (!string.IsNullOrWhiteSpace(block))
      {
        rootBlocks.Add(block.Trim());
      }
    }

    return rootBlocks.Count == 0 ? "" : string.Join(BetweenRoots, rootBlocks);
  }

  private static string LoadOneRoot(string root, int maxBytesPerFile, ITraceSink? trace)
  {
    var parts = new List<string>();
    foreach (var fileName in new[] { "AGENTS.md", "CLAUDE.md" })
    {
      var path = Path.Combine(root, fileName);
      if (!File.Exists(path))
      {
        continue;
      }

      var (text, truncated) = ReadUtf8FileWithCap(path, maxBytesPerFile);
      if (truncated)
      {
        trace?.Trace(new GenericTraceEvent
        {
          Payload = new JsonObject
          {
            ["reason"] = "project_markdown_truncated",
            ["path"] = path,
            ["max_bytes"] = maxBytesPerFile
          }
        });
      }

      if (!string.IsNullOrWhiteSpace(text))
      {
        parts.Add(text.Trim());
      }
    }

    return parts.Count == 0 ? "" : string.Join(BetweenFiles, parts);
  }

  private static (string Text, bool Truncated) ReadUtf8FileWithCap(string path, int maxBytes)
  {
    using var fs = File.OpenRead(path);
    var buffer = new byte[maxBytes + 1];
    var n = fs.Read(buffer, 0, buffer.Length);
    var truncated = n > maxBytes;
    var take = truncated ? maxBytes : n;
    var text = Encoding.UTF8.GetString(buffer.AsSpan(0, take));
    return (text, truncated);
  }
}
