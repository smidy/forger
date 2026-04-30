using FluentAssertions;
using Forge.Cli;
using Forge.Core.Exceptions;

namespace Forge.Cli.Tests;

public sealed class ExitCodeMapperTests
{
  [Fact]
  public void ValidationException_MapsTo_1()
  {
    ExitCodeMapper.ExitCodeFor(new ValidationException("bad")).Should().Be(1);
  }

  [Fact]
  public void ConfigException_MapsTo_1()
  {
    ExitCodeMapper.ExitCodeFor(new ConfigException("bad")).Should().Be(1);
  }

  [Fact]
  public void AgentException_MapsTo_2()
  {
    ExitCodeMapper.ExitCodeFor(new AgentException("agent broke")).Should().Be(2);
  }

  [Fact]
  public void ProviderException_MapsTo_2()
  {
    ExitCodeMapper.ExitCodeFor(new ProviderException("500", 500)).Should().Be(2);
  }

  [Fact]
  public void PipelineException_MapsTo_2()
  {
    ExitCodeMapper.ExitCodeFor(new PipelineException("pipeline broke")).Should().Be(2);
  }

  [Fact]
  public void PartialFailureException_MapsTo_3()
  {
    ExitCodeMapper.ExitCodeFor(new PartialFailureException("some iterations failed")).Should().Be(3);
  }

  [Fact]
  public void OperationCanceledException_MapsTo_130()
  {
    ExitCodeMapper.ExitCodeFor(new OperationCanceledException()).Should().Be(130);
  }

  [Fact]
  public void TaskCanceledException_MapsTo_130()
  {
    ExitCodeMapper.ExitCodeFor(new TaskCanceledException()).Should().Be(130);
  }

  [Fact]
  public void UnknownException_MapsTo_2()
  {
    ExitCodeMapper.ExitCodeFor(new InvalidOperationException("something")).Should().Be(2);
  }

  [Fact]
  public void RateLimitedException_MapsTo_6()
  {
    ExitCodeMapper.ExitCodeFor(new RateLimitedException("rate limited", null)).Should().Be(6);
  }

  [Fact]
  public void QuotaExhaustedException_MapsTo_2()
  {
    ExitCodeMapper.ExitCodeFor(new QuotaExhaustedException("quota")).Should().Be(2);
  }

  [Fact]
  public void RenderStderr_ValidationException_IncludesJsonPath()
  {
    var rendered = ExitCodeMapper.RenderStderr(new ValidationException("bad", "$.foo.bar"));
    rendered.Should().Contain("\"path\"");
    rendered.Should().Contain("$.foo.bar");
    rendered.Should().Contain("\"error\"");
  }

  [Fact]
  public void RenderStderr_ProviderException_IncludesStatusCode()
  {
    var rendered = ExitCodeMapper.RenderStderr(new ProviderException("upstream", 502));
    rendered.Should().Contain("\"statusCode\"");
    rendered.Should().Contain("502");
  }

  [Fact]
  public void RenderStderr_Cancelled_IsFriendly()
  {
    ExitCodeMapper.RenderStderr(new OperationCanceledException()).Should().Be("Cancelled.");
  }

  [Fact]
  public void RenderStderr_RateLimited_Includes_errorKind_and_resumable()
  {
    var rendered = ExitCodeMapper.RenderStderr(
      new RateLimitedException("rate limited (429)", TimeSpan.FromSeconds(30)));
    rendered.Should().Contain("\"errorKind\":\"rate_limited\"");
    rendered.Should().Contain("\"statusCode\":429");
    rendered.Should().Contain("\"resumable\":true");
    rendered.Should().Contain("\"retryAfterSeconds\":30");
  }

  [Fact]
  public void RenderStderr_RateLimited_without_retryAfter_omits_retryAfterSeconds()
  {
    var rendered = ExitCodeMapper.RenderStderr(
      new RateLimitedException("rate limited", null));
    rendered.Should().Contain("\"errorKind\":\"rate_limited\"");
    rendered.Should().NotContain("retryAfterSeconds");
  }

  [Fact]
  public void RenderStderr_QuotaExhausted_Includes_errorKind_and_resumable_false()
  {
    var rendered = ExitCodeMapper.RenderStderr(
      new QuotaExhaustedException("quota exhausted"));
    rendered.Should().Contain("\"errorKind\":\"quota_exhausted\"");
    rendered.Should().Contain("\"statusCode\":429");
    rendered.Should().Contain("\"resumable\":false");
  }
}
