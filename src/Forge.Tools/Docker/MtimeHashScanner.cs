using System.Security.Cryptography;
using Forge.Core.Exceptions;

namespace Forge.Tools.Docker;

/// <summary>
/// Snapshot entry recorded for a single file under a write root.
/// </summary>
/// <param name="RelPath">Forward-slash path relative to the scanned root. Lets the diff output map 1:1 to the container-visible <c>/work/write/&lt;n&gt;/…</c> namespace.</param>
/// <param name="Size">File length at snapshot time (bytes).</param>
/// <param name="MtimeUtcTicks"><c>File.GetLastWriteTimeUtc(path).Ticks</c>. 100-ns precision on Windows; native on Linux/macOS.</param>
/// <param name="ContentHash">Lower-case hex SHA-256 of the file bytes, or <c>null</c> if the file was larger than the caller's <c>maxHashBytes</c> cap.</param>
/// <param name="HashSkippedForSize"><c>true</c> when <see cref="ContentHash"/> is <c>null</c> solely because the file exceeded the hash-byte cap.</param>
public sealed record FsSnapshotEntry(
  string RelPath,
  long Size,
  long MtimeUtcTicks,
  string? ContentHash,
  bool HashSkippedForSize);

/// <summary>
/// Output of <see cref="MtimeHashScanner.Scan"/>. Carries the entry dictionary
/// plus bookkeeping needed by <see cref="MtimeHashScanner.Diff"/> and the
/// <c>BashDiffTruncatedEvent</c> emitter.
/// </summary>
/// <param name="Root">The canonical host root that was scanned.</param>
/// <param name="Entries">Path-keyed entries. Key is the same string as <see cref="FsSnapshotEntry.RelPath"/>.</param>
/// <param name="WasTruncated"><c>true</c> when a bound (file count, depth, or individual file hashing) stopped the walk early.</param>
/// <param name="TruncationReason">One of <c>max_files</c>, <c>max_depth</c>, <c>max_hash_bytes</c>, or <c>null</c>.</param>
/// <param name="FilesScanned">Total files recorded, including hash-skipped ones.</param>
/// <param name="MaxDepthReached">Deepest directory level visited (root = 0).</param>
public sealed record FsSnapshot(
  string Root,
  IReadOnlyDictionary<string, FsSnapshotEntry> Entries,
  bool WasTruncated,
  string? TruncationReason,
  int FilesScanned,
  int MaxDepthReached);

/// <summary>
/// One file whose content, size, or mtime differed between pre- and post-exec
/// snapshots. Ships as part of the <c>BashOutput.diffs</c> array.
/// </summary>
/// <param name="Path">Root-relative forward-slash path (same shape as <see cref="FsSnapshotEntry.RelPath"/>).</param>
/// <param name="Kind">One of <c>added</c>, <c>removed</c>, or <c>modified</c>.</param>
/// <param name="HashBefore">Pre-exec SHA-256 hex (null when added or hash-skipped).</param>
/// <param name="HashAfter">Post-exec SHA-256 hex (null when removed or hash-skipped).</param>
/// <param name="SizeDelta">Post size minus pre size. Negative on shrink, zero when length unchanged but content differed.</param>
public sealed record DiffEntry(
  string Path,
  string Kind,
  string? HashBefore,
  string? HashAfter,
  long SizeDelta);

/// <summary>
/// Pre- and post-exec scanner that notices every file the bash command
/// touched under a write root. Plan:
/// <c>docs/plans/bash-tool.md</c> §Diff verification.
/// </summary>
/// <remarks>
/// Why SHA-256 (not BLAKE3) — this detector is designed to spot accidental
/// mtime-replay evasion (<c>touch -r</c>) and lost-write bugs. Cryptographic
/// strength is not required; SHA-256 is already in-tree via
/// <c>CanonicalHasher</c> and adds zero dependencies. Re-evaluate for v2 if
/// a benchmark shows it's the bottleneck.
/// </remarks>
public static class MtimeHashScanner
{
  /// <summary>Default file-count cap (10 000) per the bash-tool plan.</summary>
  public const int DefaultMaxFiles = 10_000;

  /// <summary>Default path-depth cap (16) per the bash-tool plan.</summary>
  public const int DefaultMaxDepth = 16;

  /// <summary>Default per-file hash byte cap (4 MiB) per the bash-tool plan.</summary>
  public const long DefaultMaxHashBytes = 4L * 1024 * 1024;

  /// <summary>
  /// Walk <paramref name="root"/> and record every regular file up to the
  /// supplied caps. Symbolic links are traversed as files — the snapshot entry
  /// reflects the link's own attributes, not the target's. Missing roots yield
  /// an empty snapshot; the caller decides whether that's an error.
  /// </summary>
  public static FsSnapshot Scan(
    string root,
    int maxFiles = DefaultMaxFiles,
    int maxDepth = DefaultMaxDepth,
    long maxHashBytes = DefaultMaxHashBytes)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(root);
    if (maxFiles < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(maxFiles), "maxFiles must be >= 1.");
    }

    if (maxDepth < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(maxDepth), "maxDepth must be >= 1.");
    }

    if (maxHashBytes < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(maxHashBytes), "maxHashBytes must be >= 1.");
    }

    var canonicalRoot = Path.GetFullPath(root);
    var entries = new Dictionary<string, FsSnapshotEntry>(StringComparer.Ordinal);
    if (!Directory.Exists(canonicalRoot))
    {
      return new FsSnapshot(canonicalRoot, entries, WasTruncated: false,
        TruncationReason: null, FilesScanned: 0, MaxDepthReached: 0);
    }

    var filesScanned = 0;
    var maxDepthReached = 0;
    var truncated = false;
    string? truncationReason = null;

    // Iterative DFS so we can enforce depth strictly without recursion bloat.
    var stack = new Stack<(string Dir, int Depth)>();
    stack.Push((canonicalRoot, 0));

    while (stack.Count > 0 && !truncated)
    {
      var (dir, depth) = stack.Pop();
      if (depth > maxDepthReached)
      {
        maxDepthReached = depth;
      }

      if (depth >= maxDepth)
      {
        truncated = true;
        truncationReason = "max_depth";
        break;
      }

      string[] subdirs;
      string[] files;
      try
      {
        subdirs = Directory.GetDirectories(dir);
        files = Directory.GetFiles(dir);
      }
      catch (UnauthorizedAccessException)
      {
        continue;
      }
      catch (DirectoryNotFoundException)
      {
        continue;
      }

      foreach (var file in files)
      {
        if (filesScanned >= maxFiles)
        {
          truncated = true;
          truncationReason = "max_files";
          break;
        }

        var entry = Capture(canonicalRoot, file, maxHashBytes, out var hashCapped);
        if (entry is null)
        {
          continue;
        }

        entries[entry.RelPath] = entry;
        filesScanned++;
        if (hashCapped)
        {
          // Not fatal — individual files over the hash cap are recorded with
          // ContentHash=null. Surface it as a soft truncation so the event
          // stream captures "at least one oversize file" without losing data.
          truncated = true;
          truncationReason ??= "max_hash_bytes";
        }
      }

      if (truncated)
      {
        break;
      }

      foreach (var sub in subdirs)
      {
        stack.Push((sub, depth + 1));
      }
    }

    return new FsSnapshot(canonicalRoot, entries, truncated, truncationReason, filesScanned, maxDepthReached);
  }

  /// <summary>
  /// Compare <paramref name="before"/> to <paramref name="after"/> and emit
  /// one <see cref="DiffEntry"/> per changed file. A file is "modified" if
  /// its content hash differs; if both hashes are available and equal, mtime
  /// alone is ignored (<c>touch -r</c> is not a change). When a hash is
  /// missing on either side (oversize file), mtime or size differences
  /// escalate to a modify.
  /// </summary>
  public static IReadOnlyList<DiffEntry> Diff(FsSnapshot before, FsSnapshot after)
  {
    ArgumentNullException.ThrowIfNull(before);
    ArgumentNullException.ThrowIfNull(after);

    if (!string.Equals(before.Root, after.Root, StringComparison.Ordinal))
    {
      throw new ValidationException(
        $"Diff snapshots describe different roots: before=`{before.Root}`, after=`{after.Root}`.");
    }

    var diffs = new List<DiffEntry>();
    foreach (var (path, pre) in before.Entries)
    {
      if (!after.Entries.TryGetValue(path, out var post))
      {
        diffs.Add(new DiffEntry(path, "removed", pre.ContentHash, HashAfter: null, SizeDelta: -pre.Size));
        continue;
      }

      if (IsChanged(pre, post))
      {
        diffs.Add(new DiffEntry(path, "modified", pre.ContentHash, post.ContentHash, post.Size - pre.Size));
      }
    }

    foreach (var (path, post) in after.Entries)
    {
      if (!before.Entries.ContainsKey(path))
      {
        diffs.Add(new DiffEntry(path, "added", HashBefore: null, post.ContentHash, post.Size));
      }
    }

    return diffs;
  }

  private static bool IsChanged(FsSnapshotEntry pre, FsSnapshotEntry post)
  {
    if (pre.ContentHash is not null && post.ContentHash is not null)
    {
      // Both sides fully hashed — content equality wins over mtime.
      return !string.Equals(pre.ContentHash, post.ContentHash, StringComparison.Ordinal);
    }

    // At least one side is hash-skipped; fall back to size + mtime so mtime
    // evasion on unchanged-bytes-but-stale-mtime files is still caught.
    if (pre.Size != post.Size)
    {
      return true;
    }

    return pre.MtimeUtcTicks != post.MtimeUtcTicks;
  }

  private static FsSnapshotEntry? Capture(
    string root,
    string fullPath,
    long maxHashBytes,
    out bool hashCapped)
  {
    hashCapped = false;
    FileInfo info;
    try
    {
      info = new FileInfo(fullPath);
      if (!info.Exists)
      {
        return null;
      }
    }
    catch (IOException)
    {
      return null;
    }
    catch (UnauthorizedAccessException)
    {
      return null;
    }

    var relPath = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    var size = info.Length;
    var mtime = info.LastWriteTimeUtc.Ticks;
    string? hash = null;
    if (size <= maxHashBytes)
    {
      hash = TryHash(fullPath);
    }
    else
    {
      hashCapped = true;
    }

    return new FsSnapshotEntry(relPath, size, mtime, hash, hashCapped);
  }

  private static string? TryHash(string fullPath)
  {
    try
    {
      using var stream = File.OpenRead(fullPath);
      var hashBytes = SHA256.HashData(stream);
      return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
    catch (IOException)
    {
      return null;
    }
    catch (UnauthorizedAccessException)
    {
      return null;
    }
  }
}
