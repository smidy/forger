using System.Text;
using System.Text.RegularExpressions;

namespace Forge.Tools.Docker;

/// <summary>
/// Result of a bounded stream read.
/// </summary>
/// <param name="Text">UTF-8 decoded text (with replacement for invalid bytes), clipped at <c>softCapBytes</c>.</param>
/// <param name="Truncated"><c>true</c> when the underlying byte stream exceeded <c>softCapBytes</c> — the tool reports this back to the agent.</param>
/// <param name="HardKillHit"><c>true</c> when the stream crossed the hard byte ceiling — the lifecycle caller MUST kill the exec.</param>
/// <param name="TotalBytesRead">All bytes read from the stream, including those past <c>softCapBytes</c>. Useful for trace diagnostics.</param>
public sealed record CappedStreamResult(string Text, bool Truncated, bool HardKillHit, long TotalBytesRead);

/// <summary>
/// Bounded reader for a bash-tool <c>docker exec</c> stream. Keeps at most
/// <c>softCapBytes</c> worth of bytes in memory for the agent-visible text,
/// continues draining past that to count total volume, and flips
/// <see cref="CappedStreamResult.HardKillHit"/> true once the stream crosses
/// <c>hardKillBytes</c>. Plan: <c>docs/plans/bash-tool.md</c> §Error surface.
/// </summary>
public static class StreamCap
{
  /// <summary>Default stdout cap (16 KiB) per the bash-tool plan.</summary>
  public const int DefaultStdoutCapBytes = 16 * 1024;

  /// <summary>Default stderr cap (4 KiB) per the bash-tool plan.</summary>
  public const int DefaultStderrCapBytes = 4 * 1024;

  /// <summary>Default hard-kill ceiling (64 MiB) across either stream.</summary>
  public const long DefaultHardKillBytes = 64L * 1024 * 1024;

  // ANSI escape sequences:
  //   CSI (ESC [ ... letter)              e.g. "\x1b[31m"
  //   OSC (ESC ] ... BEL or ESC \)        e.g. "\x1b]0;title\x07"
  //   Two-byte (ESC letter)               e.g. "\x1b=", "\x1b>"
  //   C1 single-byte controls (0x80–0x9f) — tolerated as benign, stripped too.
  private static readonly Regex AnsiEscape = new(
    @"\x1b\[[0-?]*[ -/]*[@-~]" +
    @"|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)" +
    @"|\x1b[@-Z\\-_]" +
    @"|[\x00-\x08\x0b\x0c\x0e-\x1a\x1c-\x1f\x7f]" +
    @"|[\x80-\x9f]",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);

  /// <summary>
  /// Read <paramref name="source"/> to end (or cancellation), keeping at most
  /// <paramref name="softCapBytes"/> bytes for text decoding. Past the soft
  /// cap the reader continues but only counts bytes; past
  /// <paramref name="hardKillBytes"/> it stops and sets
  /// <see cref="CappedStreamResult.HardKillHit"/>.
  /// </summary>
  /// <param name="source">Stream to drain. Not disposed.</param>
  /// <param name="softCapBytes">Byte-length ceiling of the returned <c>Text</c>. Typical: <see cref="DefaultStdoutCapBytes"/> or <see cref="DefaultStderrCapBytes"/>.</param>
  /// <param name="hardKillBytes">Total-byte ceiling before the reader gives up. Typical: <see cref="DefaultHardKillBytes"/>.</param>
  /// <param name="cancellationToken">Propagates cancellation to the stream read.</param>
  public static async Task<CappedStreamResult> ReadCappedAsync(
    Stream source,
    int softCapBytes,
    long hardKillBytes,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(source);
    if (softCapBytes < 0)
    {
      throw new ArgumentOutOfRangeException(nameof(softCapBytes));
    }

    if (hardKillBytes < softCapBytes)
    {
      throw new ArgumentOutOfRangeException(nameof(hardKillBytes),
        "hardKillBytes must be >= softCapBytes.");
    }

    var buffer = new byte[4096];
    using var textBuffer = new MemoryStream(Math.Min(softCapBytes, 64 * 1024));
    var totalBytes = 0L;
    var hardKillHit = false;

    while (true)
    {
      int read;
      try
      {
        read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
          .ConfigureAwait(false);
      }
      catch (IOException)
      {
        // Pipe closed under us (docker exec exited). Treat as end of stream.
        break;
      }

      if (read == 0)
      {
        break;
      }

      totalBytes += read;

      var remainingSoft = softCapBytes - (int)Math.Min(textBuffer.Length, softCapBytes);
      if (remainingSoft > 0)
      {
        var take = Math.Min(remainingSoft, read);
        textBuffer.Write(buffer, 0, take);
      }

      if (totalBytes >= hardKillBytes)
      {
        hardKillHit = true;
        break;
      }
    }

    var truncated = totalBytes > softCapBytes;
    var bytes = textBuffer.ToArray();
    var raw = DecodeUtf8WithReplacement(bytes);
    var stripped = AnsiEscape.Replace(raw, string.Empty);
    return new CappedStreamResult(stripped, truncated, hardKillHit, totalBytes);
  }

  /// <summary>
  /// Strip ANSI and C0/C1 control characters from <paramref name="text"/>.
  /// Callers that skipped <see cref="ReadCappedAsync"/> (e.g. loading a trace
  /// event's stored tail) can reuse this for rendering.
  /// </summary>
  public static string StripAnsi(string text)
  {
    ArgumentNullException.ThrowIfNull(text);
    return AnsiEscape.Replace(text, string.Empty);
  }

  private static string DecodeUtf8WithReplacement(byte[] bytes)
  {
    // Encoding.UTF8 default uses replacement for invalid byte sequences, which
    // is exactly what the plan calls for ("UTF-8 lossy replacement for binary").
    var enc = new UTF8Encoding(
      encoderShouldEmitUTF8Identifier: false,
      throwOnInvalidBytes: false);
    return enc.GetString(bytes);
  }
}
