using FluentAssertions;
using Forge.Core.Exceptions;
using Forge.Tools.Docker;

namespace Forge.Tools.Tests.Docker;

/// <summary>
/// Pins the bash-tool's pre/post filesystem scanner: mtime+hash detection,
/// <c>touch -r</c> evasion resistance, bounded-traversal limits, and the
/// three diff kinds (<c>added</c> / <c>removed</c> / <c>modified</c>).
/// Plan: <c>docs/plans/bash-tool.md</c>.
/// </summary>
public class MtimeHashScannerTests : IDisposable
{
  private readonly string _root;

  public MtimeHashScannerTests()
  {
    _root = Path.Combine(Path.GetTempPath(), "forge-mhs-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_root);
  }

  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, recursive: true);
    }
    catch
    {
    }
    GC.SuppressFinalize(this);
  }

  private string Write(string rel, string content)
  {
    var full = Path.Combine(_root, rel);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, content);
    return full;
  }

  [Fact]
  public void Empty_root_produces_empty_snapshot()
  {
    var snap = MtimeHashScanner.Scan(_root);
    snap.Entries.Should().BeEmpty();
    snap.FilesScanned.Should().Be(0);
    snap.WasTruncated.Should().BeFalse();
  }

  [Fact]
  public void Missing_root_returns_empty_snapshot_not_throw()
  {
    var ghost = Path.Combine(_root, "does-not-exist");
    var snap = MtimeHashScanner.Scan(ghost);
    snap.Entries.Should().BeEmpty();
    snap.FilesScanned.Should().Be(0);
  }

  [Fact]
  public void Unchanged_tree_yields_no_diffs()
  {
    Write("a.txt", "alpha");
    Write("sub/b.txt", "beta");
    var before = MtimeHashScanner.Scan(_root);
    var after = MtimeHashScanner.Scan(_root);

    MtimeHashScanner.Diff(before, after).Should().BeEmpty();
  }

  [Fact]
  public void Added_file_appears_as_added_diff()
  {
    Write("existing.txt", "x");
    var before = MtimeHashScanner.Scan(_root);
    Write("new.txt", "new content");
    var after = MtimeHashScanner.Scan(_root);

    var diffs = MtimeHashScanner.Diff(before, after);
    diffs.Should().HaveCount(1);
    diffs[0].Path.Should().Be("new.txt");
    diffs[0].Kind.Should().Be("added");
    diffs[0].HashBefore.Should().BeNull();
    diffs[0].HashAfter.Should().NotBeNullOrEmpty();
    diffs[0].SizeDelta.Should().Be("new content".Length);
  }

  [Fact]
  public void Removed_file_appears_as_removed_diff()
  {
    var f = Write("gone.txt", "bye");
    var before = MtimeHashScanner.Scan(_root);
    File.Delete(f);
    var after = MtimeHashScanner.Scan(_root);

    var diffs = MtimeHashScanner.Diff(before, after);
    diffs.Should().HaveCount(1);
    diffs[0].Path.Should().Be("gone.txt");
    diffs[0].Kind.Should().Be("removed");
    diffs[0].HashBefore.Should().NotBeNullOrEmpty();
    diffs[0].HashAfter.Should().BeNull();
    diffs[0].SizeDelta.Should().Be(-3);
  }

  [Fact]
  public void Modified_content_with_same_size_detected_by_hash()
  {
    var f = Write("m.txt", "AAAA");
    var before = MtimeHashScanner.Scan(_root);
    File.WriteAllText(f, "BBBB");
    var after = MtimeHashScanner.Scan(_root);

    var diffs = MtimeHashScanner.Diff(before, after);
    diffs.Should().HaveCount(1);
    diffs[0].Kind.Should().Be("modified");
    diffs[0].SizeDelta.Should().Be(0);
    diffs[0].HashBefore.Should().NotBe(diffs[0].HashAfter);
  }

  [Fact]
  public void Touch_r_mtime_replay_is_not_flagged_when_content_equal()
  {
    var f = Write("stable.txt", "content");
    var before = MtimeHashScanner.Scan(_root);

    // Simulate `touch -r ref file` — mtime bumps but content untouched.
    File.SetLastWriteTimeUtc(f, File.GetLastWriteTimeUtc(f).AddMinutes(-5));
    var after = MtimeHashScanner.Scan(_root);

    MtimeHashScanner.Diff(before, after).Should().BeEmpty();
  }

  [Fact]
  public void Touch_d_past_mtime_with_content_change_still_flagged()
  {
    var f = Write("c.txt", "old");
    var before = MtimeHashScanner.Scan(_root);

    // Agent changes content AND rewinds mtime to hide it.
    File.WriteAllText(f, "new");
    File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-30));
    var after = MtimeHashScanner.Scan(_root);

    MtimeHashScanner.Diff(before, after)
      .Should().ContainSingle(d => d.Kind == "modified" && d.Path == "c.txt");
  }

  [Fact]
  public void Max_files_cap_triggers_truncation()
  {
    for (var i = 0; i < 20; i++)
    {
      Write($"f{i}.txt", "x");
    }

    var snap = MtimeHashScanner.Scan(_root, maxFiles: 5, maxDepth: 8, maxHashBytes: 1024);
    snap.FilesScanned.Should().Be(5);
    snap.WasTruncated.Should().BeTrue();
    snap.TruncationReason.Should().Be("max_files");
  }

  [Fact]
  public void Max_depth_cap_triggers_truncation()
  {
    Write("lvl1/lvl2/lvl3/lvl4/deep.txt", "x");

    var snap = MtimeHashScanner.Scan(_root, maxFiles: 100, maxDepth: 2, maxHashBytes: 1024);
    snap.WasTruncated.Should().BeTrue();
    snap.TruncationReason.Should().Be("max_depth");
  }

  [Fact]
  public void Oversize_file_records_null_hash_and_flags_truncation()
  {
    // Write a 10-byte file, then ask the scanner to cap hashing at 4 bytes.
    Write("big.bin", "0123456789");
    var snap = MtimeHashScanner.Scan(_root, maxFiles: 10, maxDepth: 4, maxHashBytes: 4);

    snap.Entries.Should().ContainKey("big.bin");
    snap.Entries["big.bin"].ContentHash.Should().BeNull();
    snap.Entries["big.bin"].HashSkippedForSize.Should().BeTrue();
    snap.WasTruncated.Should().BeTrue();
    snap.TruncationReason.Should().Be("max_hash_bytes");
  }

  [Fact]
  public void Oversize_file_with_size_change_still_diffs_via_mtime_fallback()
  {
    var f = Write("big.bin", "0123456789");
    var before = MtimeHashScanner.Scan(_root, maxFiles: 10, maxDepth: 4, maxHashBytes: 4);

    File.WriteAllText(f, "012345"); // shrink — size change
    var after = MtimeHashScanner.Scan(_root, maxFiles: 10, maxDepth: 4, maxHashBytes: 4);

    var diffs = MtimeHashScanner.Diff(before, after);
    diffs.Should().ContainSingle(d => d.Kind == "modified" && d.Path == "big.bin");
    diffs[0].SizeDelta.Should().Be(-4);
  }

  [Fact]
  public void Snapshot_rel_path_uses_forward_slash_on_all_os()
  {
    Write("sub/deep/a.txt", "x");
    var snap = MtimeHashScanner.Scan(_root);
    snap.Entries.Keys.Should().Contain("sub/deep/a.txt");
    snap.Entries.Keys.Should().NotContain(k => k.Contains('\\'));
  }

  [Fact]
  public void Diff_rejects_snapshots_of_different_roots()
  {
    var other = Path.Combine(Path.GetTempPath(), "forge-mhs-other-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(other);
    try
    {
      var a = MtimeHashScanner.Scan(_root);
      var b = MtimeHashScanner.Scan(other);

      var act = () => MtimeHashScanner.Diff(a, b);
      act.Should().Throw<ValidationException>()
        .WithMessage("*different roots*");
    }
    finally
    {
      try { Directory.Delete(other, recursive: true); }
      catch { }
    }
  }

  [Fact]
  public void Combined_added_removed_modified_diff()
  {
    Write("keep.txt", "same");
    Write("mod.txt", "v1");
    var gone = Write("gone.txt", "bye");

    var before = MtimeHashScanner.Scan(_root);

    File.WriteAllText(Path.Combine(_root, "mod.txt"), "v2-longer");
    File.Delete(gone);
    Write("new.txt", "fresh");

    var after = MtimeHashScanner.Scan(_root);
    var diffs = MtimeHashScanner.Diff(before, after);

    diffs.Should().HaveCount(3);
    diffs.Should().ContainSingle(d => d.Kind == "added" && d.Path == "new.txt");
    diffs.Should().ContainSingle(d => d.Kind == "removed" && d.Path == "gone.txt");
    diffs.Should().ContainSingle(d => d.Kind == "modified" && d.Path == "mod.txt");
  }

  [Fact]
  public void Scan_argument_validation()
  {
    Action a1 = () => MtimeHashScanner.Scan(_root, maxFiles: 0);
    Action a2 = () => MtimeHashScanner.Scan(_root, maxDepth: 0);
    Action a3 = () => MtimeHashScanner.Scan(_root, maxHashBytes: 0);
    a1.Should().Throw<ArgumentOutOfRangeException>();
    a2.Should().Throw<ArgumentOutOfRangeException>();
    a3.Should().Throw<ArgumentOutOfRangeException>();
  }
}
