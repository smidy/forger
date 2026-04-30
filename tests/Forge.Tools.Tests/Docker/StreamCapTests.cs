using System.Text;
using FluentAssertions;
using Forge.Tools.Docker;

namespace Forge.Tools.Tests.Docker;

/// <summary>
/// Unit coverage for the bash-tool bounded stream reader — soft cap, hard kill,
/// UTF-8 lossy replacement, and ANSI/control stripping. Plan:
/// <c>docs/plans/bash-tool.md</c>.
/// </summary>
public class StreamCapTests
{
  [Fact]
  public async Task Short_utf8_returned_untruncated()
  {
    var bytes = Encoding.UTF8.GetBytes("hello world\n");
    using var ms = new MemoryStream(bytes);
    var r = await StreamCap.ReadCappedAsync(ms, softCapBytes: 64, hardKillBytes: 1024, cancellationToken: TestContext.Current.CancellationToken);

    r.Text.Should().Be("hello world\n");
    r.Truncated.Should().BeFalse();
    r.HardKillHit.Should().BeFalse();
    r.TotalBytesRead.Should().Be(bytes.Length);
  }

  [Fact]
  public async Task Soft_cap_truncates_but_continues_counting()
  {
    var bytes = Encoding.UTF8.GetBytes(new string('a', 100));
    using var ms = new MemoryStream(bytes);
    var r = await StreamCap.ReadCappedAsync(ms, softCapBytes: 16, hardKillBytes: 1024, cancellationToken: TestContext.Current.CancellationToken);

    r.Text.Length.Should().Be(16);
    r.Truncated.Should().BeTrue();
    r.HardKillHit.Should().BeFalse();
    r.TotalBytesRead.Should().Be(100);
  }

  [Fact]
  public async Task Hard_kill_fires_when_stream_exceeds_ceiling()
  {
    var bytes = Encoding.UTF8.GetBytes(new string('z', 2048));
    using var ms = new MemoryStream(bytes);
    var r = await StreamCap.ReadCappedAsync(ms, softCapBytes: 32, hardKillBytes: 512, cancellationToken: TestContext.Current.CancellationToken);

    r.Truncated.Should().BeTrue();
    r.HardKillHit.Should().BeTrue();
    r.TotalBytesRead.Should().BeGreaterThanOrEqualTo(512);
    r.TotalBytesRead.Should().BeLessThan(2048 + 4096);
  }

  [Fact]
  public async Task Ansi_color_codes_are_stripped()
  {
    var raw = "\u001b[31mred\u001b[0m and \u001b[1;33;44mbold\u001b[m";
    var bytes = Encoding.UTF8.GetBytes(raw);
    using var ms = new MemoryStream(bytes);
    var r = await StreamCap.ReadCappedAsync(ms, softCapBytes: 1024, hardKillBytes: 4096, cancellationToken: TestContext.Current.CancellationToken);

    r.Text.Should().Be("red and bold");
  }

  [Fact]
  public async Task Osc_title_sequence_is_stripped()
  {
    var raw = "\u001b]0;tab title\u0007done";
    var bytes = Encoding.UTF8.GetBytes(raw);
    using var ms = new MemoryStream(bytes);
    var r = await StreamCap.ReadCappedAsync(ms, softCapBytes: 1024, hardKillBytes: 4096, cancellationToken: TestContext.Current.CancellationToken);

    r.Text.Should().Be("done");
  }

  [Fact]
  public async Task Control_chars_stripped_but_tab_newline_cr_preserved()
  {
    var raw = "a\tb\nc\rd\u0008e";
    var bytes = Encoding.UTF8.GetBytes(raw);
    using var ms = new MemoryStream(bytes);
    var r = await StreamCap.ReadCappedAsync(ms, softCapBytes: 1024, hardKillBytes: 4096, cancellationToken: TestContext.Current.CancellationToken);

    r.Text.Should().Be("a\tb\nc\rde");
  }

  [Fact]
  public async Task Invalid_utf8_is_replaced_not_thrown()
  {
    var bytes = new byte[] { 0x41, 0xC0, 0x80, 0x42 };   // A + overlong null + B
    using var ms = new MemoryStream(bytes);
    var r = await StreamCap.ReadCappedAsync(ms, softCapBytes: 32, hardKillBytes: 1024, cancellationToken: TestContext.Current.CancellationToken);

    r.Text.Should().StartWith("A").And.EndWith("B");
    r.HardKillHit.Should().BeFalse();
  }

  [Fact]
  public async Task Empty_stream_returns_empty_string()
  {
    using var ms = new MemoryStream(Array.Empty<byte>());
    var r = await StreamCap.ReadCappedAsync(ms, softCapBytes: 16, hardKillBytes: 1024, cancellationToken: TestContext.Current.CancellationToken);

    r.Text.Should().Be(string.Empty);
    r.Truncated.Should().BeFalse();
    r.HardKillHit.Should().BeFalse();
    r.TotalBytesRead.Should().Be(0);
  }

  [Fact]
  public async Task Soft_cap_equal_to_payload_not_truncated()
  {
    var bytes = Encoding.UTF8.GetBytes("exactly-16-bytes");
    bytes.Length.Should().Be(16);
    using var ms = new MemoryStream(bytes);
    var r = await StreamCap.ReadCappedAsync(ms, softCapBytes: 16, hardKillBytes: 1024, cancellationToken: TestContext.Current.CancellationToken);

    r.Text.Should().Be("exactly-16-bytes");
    r.Truncated.Should().BeFalse();
  }

  [Fact]
  public async Task Zero_soft_cap_returns_empty_but_counts_bytes()
  {
    var bytes = Encoding.UTF8.GetBytes("anything");
    using var ms = new MemoryStream(bytes);
    var r = await StreamCap.ReadCappedAsync(ms, softCapBytes: 0, hardKillBytes: 1024, cancellationToken: TestContext.Current.CancellationToken);

    r.Text.Should().Be(string.Empty);
    r.Truncated.Should().BeTrue();
    r.TotalBytesRead.Should().Be(bytes.Length);
  }

  [Fact]
  public async Task Hard_kill_smaller_than_soft_throws()
  {
    using var ms = new MemoryStream();
    var act = async () =>
      await StreamCap.ReadCappedAsync(ms, softCapBytes: 128, hardKillBytes: 32, cancellationToken: TestContext.Current.CancellationToken);
    await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
  }

  [Fact]
  public void StripAnsi_round_trips_plain_text()
  {
    StreamCap.StripAnsi("plain text").Should().Be("plain text");
  }

  [Fact]
  public void StripAnsi_removes_csi_sequence()
  {
    StreamCap.StripAnsi("\u001b[2K\u001b[1;31mhi\u001b[0m").Should().Be("hi");
  }

  [Fact]
  public void StripAnsi_empty_text_returns_empty()
  {
    StreamCap.StripAnsi(string.Empty).Should().Be(string.Empty);
  }
}
