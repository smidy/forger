using FluentAssertions;
using Forge.Core.Exceptions;
using Forge.Llm;

namespace Forge.Llm.Tests;

public class ProviderErrorClassifierTests
{
  private const int DefaultMaxRetryAfter = 60;

  [Fact]
  public void RetryAfter_within_cap_returns_RateLimited_exception_with_RetryAfter()
  {
    var ex = ProviderErrorClassifier.Classify("2", "Rate limit exceeded", DefaultMaxRetryAfter);
    ex.Should().BeOfType<RateLimitedException>();
    var rl = (RateLimitedException)ex;
    rl.RetryAfter.Should().Be(TimeSpan.FromSeconds(2));
    rl.StatusCode.Should().Be(429);
  }

  [Fact]
  public void RetryAfter_exceeds_cap_returns_RateLimited_exception_with_RetryAfter()
  {
    var ex = ProviderErrorClassifier.Classify("120", "Rate limit exceeded", DefaultMaxRetryAfter);
    ex.Should().BeOfType<RateLimitedException>();
    var rl = (RateLimitedException)ex;
    rl.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));
  }

  [Fact]
  public void Quota_body_returns_QuotaExhausted_exception()
  {
    var body = "litellm.RateLimitError: RateLimitError: DashscopeException - You exceeded your current quota, please check your plan and billing details.";
    var ex = ProviderErrorClassifier.Classify(null, body, DefaultMaxRetryAfter);
    ex.Should().BeOfType<QuotaExhaustedException>();
    ex.StatusCode.Should().Be(429);
    ex.Message.Should().Contain("quota exhausted");
  }

  [Fact]
  public void Quota_body_with_monthly_limit_returns_QuotaExhausted()
  {
    var body = "You have reached your monthly limit. Please upgrade your plan.";
    var ex = ProviderErrorClassifier.Classify(null, body, DefaultMaxRetryAfter);
    ex.Should().BeOfType<QuotaExhaustedException>();
  }

  [Fact]
  public void Quota_body_with_billing_returns_QuotaExhausted()
  {
    var body = "Billing limit reached. Contact support.";
    var ex = ProviderErrorClassifier.Classify(null, body, DefaultMaxRetryAfter);
    ex.Should().BeOfType<QuotaExhaustedException>();
  }

  [Fact]
  public void No_header_no_quota_body_returns_RateLimited_with_null_RetryAfter()
  {
    var ex = ProviderErrorClassifier.Classify(null, "Too many requests", DefaultMaxRetryAfter);
    ex.Should().BeOfType<RateLimitedException>();
    var rl = (RateLimitedException)ex;
    rl.RetryAfter.Should().BeNull();
  }

  [Fact]
  public void Quota_regex_wins_over_RetryAfter_header()
  {
    // Quota body should win even if Retry-After is present
    var body = "You exceeded your current quota. Please upgrade your plan. Retry-After: 5";
    var ex = ProviderErrorClassifier.Classify("5", body, DefaultMaxRetryAfter);
    ex.Should().BeOfType<QuotaExhaustedException>();
  }

  [Fact]
  public void TryParseRetryAfter_parses_integer_seconds()
  {
    var result = ProviderErrorClassifier.TryParseRetryAfter("42");
    result.Should().Be(TimeSpan.FromSeconds(42));
  }

  [Fact]
  public void TryParseRetryAfter_returns_null_for_empty()
  {
    ProviderErrorClassifier.TryParseRetryAfter(null).Should().BeNull();
    ProviderErrorClassifier.TryParseRetryAfter("").Should().BeNull();
    ProviderErrorClassifier.TryParseRetryAfter("   ").Should().BeNull();
  }

  [Fact]
  public void TryParseRetryAfter_returns_null_for_zero()
  {
    ProviderErrorClassifier.TryParseRetryAfter("0").Should().BeNull();
  }

  [Fact]
  public void TryParseRetryAfter_returns_null_for_negative()
  {
    ProviderErrorClassifier.TryParseRetryAfter("-5").Should().BeNull();
  }

}
