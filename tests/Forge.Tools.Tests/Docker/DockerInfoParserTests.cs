using FluentAssertions;
using Forge.Tools.Docker;

namespace Forge.Tools.Tests.Docker;

/// <summary>
/// Pure parser tests for <see cref="DockerInfoParser.Parse"/>. No Docker
/// daemon. Plan: <c>docs/plans/bash-tool-rootless-docker.md</c> §2.
/// </summary>
public class DockerInfoParserTests
{
  [Fact]
  public void Rootful_linux_with_typical_security_options_is_parsed_correctly()
  {
    var raw = "linux/x86_64/27.3.1/[\"name=seccomp,profile=builtin\",\"name=cgroupns\"]";

    var info = DockerInfoParser.Parse(raw);

    info.OsType.Should().Be("linux");
    info.Architecture.Should().Be("x86_64");
    info.ServerVersion.Should().Be("27.3.1");
    info.Rootless.Should().BeFalse();
  }

  [Fact]
  public void Rootless_linux_with_bare_rootless_token_is_detected()
  {
    var raw = "linux/x86_64/27.3.1/[\"name=seccomp,profile=builtin\",\"rootless\",\"name=cgroupns\"]";

    var info = DockerInfoParser.Parse(raw);

    info.Rootless.Should().BeTrue();
    info.OsType.Should().Be("linux");
  }

  [Fact]
  public void Rootless_linux_with_name_rootless_token_is_detected()
  {
    var raw = "linux/x86_64/27.3.1/[\"name=seccomp,profile=builtin\",\"name=rootless\",\"name=cgroupns\"]";

    var info = DockerInfoParser.Parse(raw);

    info.Rootless.Should().BeTrue();
  }

  [Fact]
  public void Rootless_linux_with_name_rootless_inside_comma_list_is_detected()
  {
    var raw = "linux/x86_64/27.3.1/[\"name=rootless,profile=foo\",\"name=cgroupns\"]";

    var info = DockerInfoParser.Parse(raw);

    info.Rootless.Should().BeTrue();
  }

  [Fact]
  public void Substring_rootless_outside_token_boundaries_is_not_a_match()
  {
    var raw = "linux/x86_64/27.3.1/[\"name=notrootless\",\"rootlesschild=foo\"]";

    var info = DockerInfoParser.Parse(raw);

    info.Rootless.Should().BeFalse("comma-token equality matching prevents substring false positives");
  }

  [Fact]
  public void Windows_rootful_returns_windows_OsType()
  {
    var raw = "windows/x86_64/27.3.1/[]";

    var info = DockerInfoParser.Parse(raw);

    info.OsType.Should().Be("windows");
    info.Rootless.Should().BeFalse();
  }

  [Fact]
  public void Arm_linux_preserves_aarch64_architecture()
  {
    var raw = "linux/aarch64/27.3.1/[\"name=cgroupns\"]";

    var info = DockerInfoParser.Parse(raw);

    info.Architecture.Should().Be("aarch64");
  }

  [Fact]
  public void Empty_security_options_array_is_rootful()
  {
    var raw = "linux/x86_64/27.3.1/[]";

    var info = DockerInfoParser.Parse(raw);

    info.Rootless.Should().BeFalse();
  }

  [Fact]
  public void Whitespace_around_input_is_trimmed()
  {
    var raw = "  linux/x86_64/27.3.1/[]  \n";

    var info = DockerInfoParser.Parse(raw);

    info.OsType.Should().Be("linux");
    info.ServerVersion.Should().Be("27.3.1");
  }

  [Fact]
  public void Empty_input_raises_FormatException()
  {
    Action act = () => DockerInfoParser.Parse(string.Empty);

    act.Should().Throw<FormatException>().WithMessage("*empty*");
  }

  [Fact]
  public void Whitespace_only_input_raises_FormatException()
  {
    Action act = () => DockerInfoParser.Parse("   \t  ");

    act.Should().Throw<FormatException>();
  }

  [Fact]
  public void Missing_separators_raises_FormatException()
  {
    Action act = () => DockerInfoParser.Parse("linux-x86_64-27.3.1");

    act.Should().Throw<FormatException>().WithMessage("*four*`/`-separated*");
  }

  [Fact]
  public void Three_separators_only_raises_FormatException()
  {
    Action act = () => DockerInfoParser.Parse("linux/x86_64/27.3.1");

    act.Should().Throw<FormatException>();
  }

  [Fact]
  public void Empty_OsType_raises_FormatException()
  {
    Action act = () => DockerInfoParser.Parse("/x86_64/27.3.1/[]");

    act.Should().Throw<FormatException>().WithMessage("*OSType*");
  }

  [Fact]
  public void Empty_Architecture_raises_FormatException()
  {
    Action act = () => DockerInfoParser.Parse("linux//27.3.1/[]");

    act.Should().Throw<FormatException>().WithMessage("*Architecture*");
  }

  [Fact]
  public void Empty_ServerVersion_raises_FormatException()
  {
    Action act = () => DockerInfoParser.Parse("linux/x86_64//[]");

    act.Should().Throw<FormatException>().WithMessage("*ServerVersion*");
  }

  [Fact]
  public void Malformed_security_options_json_raises_FormatException()
  {
    Action act = () => DockerInfoParser.Parse("linux/x86_64/27.3.1/not-json");

    act.Should().Throw<FormatException>().WithMessage("*not valid JSON*");
  }

  [Fact]
  public void Security_options_object_instead_of_array_raises_FormatException()
  {
    Action act = () => DockerInfoParser.Parse("linux/x86_64/27.3.1/{\"key\":\"val\"}");

    act.Should().Throw<FormatException>().WithMessage("*not a JSON array*");
  }

  [Fact]
  public void Security_options_with_non_string_elements_does_not_raise()
  {
    var raw = "linux/x86_64/27.3.1/[123,null,\"name=cgroupns\"]";

    var info = DockerInfoParser.Parse(raw);

    info.Rootless.Should().BeFalse("non-string elements are silently skipped");
  }

  [Fact]
  public void Format_string_constant_is_the_expected_template()
  {
    DockerInfoParser.FormatString.Should().Be(
      "{{.OSType}}/{{.Architecture}}/{{.ServerVersion}}/{{json .SecurityOptions}}");
  }
}
