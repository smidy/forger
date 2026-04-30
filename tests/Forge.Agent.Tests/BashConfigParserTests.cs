using FluentAssertions;
using Forge.Agent;
using Forge.Core.Exceptions;

namespace Forge.Agent.Tests;

/// <summary>
/// Parse-time validation coverage for the opt-in <c>bash:</c> block. Every
/// forbidden field, digest/network/UID rejection, and env-allowlist rule gets
/// a dedicated case so the sandbox's defense-in-depth surface is pinned by
/// tests before the runtime lifecycle is wired. Plan:
/// <c>docs/plans/bash-tool.md</c>.
/// </summary>
public class BashConfigParserTests
{
  private const string BaseYaml = """
    name: test
    model: test-model
    system_prompt: "s"
    user_prompt: "u"
    input_schema: {type: object}
    output_schema: {type: object}
    """;

  private const string ValidDigest = "forge-bash@sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

  private static string WithBash(string bashBlock, string? tools = "[bash]") =>
    BaseYaml
    + (tools is null ? string.Empty : $"\ntools: {tools}")
    + "\nbash:\n" + bashBlock;

  [Fact]
  public void Missing_bash_block_yields_null_Bash()
  {
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(BaseYaml));
    cfg.Bash.Should().BeNull();
  }

  [Fact]
  public void Minimal_valid_block_parses_with_defaults()
  {
    var yaml = WithBash($"  image: \"{ValidDigest}\"\n  env_allow: []");
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));

    cfg.Bash.Should().NotBeNull();
    cfg.Bash!.Image.Should().Be(ValidDigest);
    cfg.Bash.Network.Should().Be("none");
    cfg.Bash.TimeoutSec.Should().Be(30);
    cfg.Bash.User.Should().Be("1000:1000");
    cfg.Bash.Memory.Should().Be("512m");
    cfg.Bash.Cpus.Should().Be(1.0);
    cfg.Bash.PidsLimit.Should().Be(100);
    cfg.Bash.TmpfsSize.Should().Be("512m");
    cfg.Bash.StorageOpt.Should().Be("");
    cfg.Bash.ReadOnlyRoot.Should().BeFalse();
    cfg.Bash.Platform.Should().Be("linux/amd64");
    cfg.Bash.EnvAllow.Should().BeEmpty();
    cfg.Bash.Env.Should().BeEmpty();
    cfg.Bash.Diff.Should().BeNull();
    cfg.Bash.ShowMountTable.Should().BeTrue();
    cfg.Bash.ExposeGit.Should().BeFalse();
  }

  [Fact]
  public void ExposeGit_can_be_enabled_via_expose_git_true()
  {
    var yaml = WithBash($"  image: \"{ValidDigest}\"\n  env_allow: []\n  expose_git: true");
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.Bash!.ExposeGit.Should().BeTrue();
  }

  [Fact]
  public void ShowMountTable_can_be_disabled_via_show_mount_table_false()
  {
    var yaml = WithBash($"  image: \"{ValidDigest}\"\n  env_allow: []\n  show_mount_table: false");
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));

    cfg.Bash!.ShowMountTable.Should().BeFalse();
  }

  [Fact]
  public void AutoScratch_defaults_true_and_parses_false_explicitly()
  {
    var yamlDefault = WithBash($"  image: \"{ValidDigest}\"\n  env_allow: []");
    AgentConfig.FromJsonNode(YamlFront.ParseToJson(yamlDefault)).Bash!.AutoScratch
      .Should().BeTrue("HOME/TMPDIR/NUGET_PACKAGES redirect to /forge-scratch is on by default so `dotnet restore` doesn't ENOSPC on the 64 MiB /tmp tmpfs");

    var yamlOff = WithBash($"  image: \"{ValidDigest}\"\n  env_allow: []\n  auto_scratch: false");
    AgentConfig.FromJsonNode(YamlFront.ParseToJson(yamlOff)).Bash!.AutoScratch.Should().BeFalse();
  }

  [Fact]
  public void Tools_lists_bash_without_bash_block_throws()
  {
    var yaml = BaseYaml + "\ntools: [bash]";
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*`tools` lists `bash`*no `bash:` block*");
  }

  [Fact]
  public void Missing_image_throws()
  {
    var yaml = WithBash("  env_allow: []");
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*`bash.image` is required*");
  }

  [Fact]
  public void Non_digest_image_throws()
  {
    var yaml = WithBash("  image: \"forge-bash:latest\"\n  env_allow: []");
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*digest-pinned*@sha256:*");
  }

  [Fact]
  public void Bare_image_id_accepted()
  {
    const string bareId = "sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    var yaml = WithBash($"  image: \"{bareId}\"\n  env_allow: []");
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.Bash.Should().NotBeNull();
    cfg.Bash!.Image.Should().Be(bareId);
  }

  [Theory]
  [InlineData("cap_add")]
  [InlineData("privileged")]
  [InlineData("pid_host")]
  [InlineData("ipc_host")]
  [InlineData("userns_host")]
  [InlineData("devices")]
  [InlineData("extra_mounts")]
  public void Forbidden_field_throws(string field)
  {
    var yaml = WithBash($"  image: \"{ValidDigest}\"\n  {field}: anything\n  env_allow: []");
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage($"*`bash.{field}`*not permitted*");
  }

  [Theory]
  [InlineData("wifi")]
  [InlineData("host")]
  [InlineData("none-ish")]
  public void Invalid_network_throws(string network)
  {
    var yaml = WithBash($"  image: \"{ValidDigest}\"\n  network: {network}\n  env_allow: []");
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*`bash.network`*none*bridge*");
  }

  [Theory]
  [InlineData("none")]
  [InlineData("bridge")]
  public void Valid_network_parses(string network)
  {
    var yaml = WithBash($"  image: \"{ValidDigest}\"\n  network: {network}\n  env_allow: []");
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.Bash!.Network.Should().Be(network);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-5)]
  [InlineData(301)]
  [InlineData(3600)]
  public void Timeout_out_of_range_throws(int seconds)
  {
    var yaml = WithBash($"  image: \"{ValidDigest}\"\n  timeout_sec: {seconds}\n  env_allow: []");
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*`bash.timeout_sec`*between 1 and 300*");
  }

  [Theory]
  [InlineData("0")]
  [InlineData("0:0")]
  [InlineData("0:1000")]
  [InlineData("root")]
  [InlineData("root:root")]
  public void Root_user_rejected(string user)
  {
    var yaml = WithBash($"  image: \"{ValidDigest}\"\n  user: \"{user}\"\n  env_allow: []");
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*`bash.user`*UID 0*");
  }

  [Theory]
  [InlineData("PATH")]
  [InlineData("LD_PRELOAD")]
  [InlineData("LD_LIBRARY_PATH")]
  [InlineData("DYLD_INSERT_LIBRARIES")]
  [InlineData("NODE_OPTIONS")]
  [InlineData("PYTHONPATH")]
  public void Forbidden_env_key_rejected(string key)
  {
    var yaml = WithBash(
      $"  image: \"{ValidDigest}\"\n  env_allow: [\"{key}\"]\n  env:\n    {key}: \"x\"");
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage($"*`bash.env.{key}`*forbidden*");
  }

  [Fact]
  public void Env_key_not_in_allowlist_rejected()
  {
    var yaml = WithBash(
      $"  image: \"{ValidDigest}\"\n  env_allow: [\"FOO\"]\n  env:\n    BAR: \"x\"");
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*`bash.env.BAR`*env_allow*");
  }

  [Fact]
  public void Env_key_in_allowlist_accepted()
  {
    var yaml = WithBash(
      $"  image: \"{ValidDigest}\"\n  env_allow: [\"MY_FLAG\"]\n  env:\n    MY_FLAG: \"1\"");
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.Bash!.Env.Should().ContainKey("MY_FLAG").WhoseValue.Should().Be("1");
  }

  [Fact]
  public void Diff_block_parses_with_overrides()
  {
    var yaml = WithBash(
      $"  image: \"{ValidDigest}\"\n  env_allow: []\n  diff:\n    max_files: 500\n    max_depth: 8\n    max_hash_bytes: 1048576");
    var cfg = AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    cfg.Bash!.Diff.Should().NotBeNull();
    cfg.Bash.Diff!.MaxFiles.Should().Be(500);
    cfg.Bash.Diff.MaxDepth.Should().Be(8);
    cfg.Bash.Diff.MaxHashBytes.Should().Be(1_048_576);
  }

  [Fact]
  public void Diff_non_positive_values_rejected()
  {
    var yaml = WithBash(
      $"  image: \"{ValidDigest}\"\n  env_allow: []\n  diff:\n    max_files: 0");
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*`bash.diff.*`*>= 1*");
  }

  [Fact]
  public void Cpus_zero_rejected()
  {
    var yaml = WithBash(
      $"  image: \"{ValidDigest}\"\n  cpus: 0\n  env_allow: []");
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*`bash.cpus`*positive*");
  }

  [Fact]
  public void Pids_limit_zero_rejected()
  {
    var yaml = WithBash(
      $"  image: \"{ValidDigest}\"\n  pids_limit: 0\n  env_allow: []");
    var act = () => AgentConfig.FromJsonNode(YamlFront.ParseToJson(yaml));
    act.Should().Throw<ConfigException>()
      .WithMessage("*`bash.pids_limit`*>= 1*");
  }
}
