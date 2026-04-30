using System.Text.RegularExpressions;
using Forge.Core.Exceptions;

namespace Forge.Llm;

/// <summary>
/// Classifies a 429 HTTP response body and headers into the correct typed
/// <see cref="ProviderException"/> subtype. The classification precedence:
/// <list type="number">
/// <item><b>Quota exhausted:</b> body matches the quota regex → <see cref="QuotaExhaustedException"/> (terminal, no retry).</item>
/// <item><b>Rate-limited with long Retry-After:</b> header present &amp; value &gt; <c>maxRetryAfterSeconds</c> → <see cref="RateLimitedException"/> (surfaced, no client retry).</item>
/// <item><b>Rate-limited (transient):</b> header present &amp; value ≤ <c>maxRetryAfterSeconds</c> → <see cref="RateLimitedException"/> with <see cref="RateLimitedException.RetryAfter"/>.</item>
/// <item><b>Rate-limited (unknown delay):</b> no header, no quota body → <see cref="RateLimitedException"/> with <c>RetryAfter = null</c>.</item>
/// </list>
/// The default leans toward rate-limited / retryable because surviving a false
/// positive on retry costs at most the retry budget; a false negative loses a
/// whole run.
/// </summary>
internal static class ProviderErrorClassifier
{
  /// <summary>
  /// Regex matching provider bodies that indicate a quota/billing exhaustion.
  /// Matches case-insensitive; anchored per line by the alternation groups.
  /// </summary>
  private static readonly Regex QuotaRegex = new(
    @"exceeded your (current )?quota|monthly limit|billing",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
    TimeSpan.FromMilliseconds(500));

  /// <summary>
  /// Classify a 429 response. <paramref name="retryAfterHeader"/> is the raw
  /// <c>Retry-After</c> header value (null if absent). <paramref name="body"/>
  /// is the response body text. <paramref name="maxRetryAfterSeconds"/> is
  /// the configured cap from <see cref="RateLimitConfig.MaxRetryAfterSeconds"/>.
  /// </summary>
  public static ProviderException Classify(
    string? retryAfterHeader,
    string body,
    int maxRetryAfterSeconds)
  {
    // 1. Quota-exhausted body pattern → terminal
    if (QuotaRegex.IsMatch(body))
    {
      return new QuotaExhaustedException($"LLM quota exhausted (429): {body}");
    }

    // 2. Parse Retry-After header
    TimeSpan? parsedRetryAfter = TryParseRetryAfter(retryAfterHeader);

    // 3. Retry-After > max → surface without retry
    if (parsedRetryAfter is { TotalSeconds: > 0 } ts && ts.TotalSeconds > maxRetryAfterSeconds)
    {
      return new RateLimitedException(
        $"LLM rate limited (429, Retry-After={retryAfterHeader}): {body}",
        parsedRetryAfter);
    }

    // 4. Transient rate-limit (Retry-After present and within cap, or absent)
    return new RateLimitedException(
      $"LLM rate limited (429): {body}",
      parsedRetryAfter is { TotalSeconds: > 0 } ? parsedRetryAfter : null);
  }

  /// <summary>
  /// Parses a <c>Retry-After</c> header value. Returns null if absent, empty,
  /// or unparseable. Accepts integer seconds (the common LiteLLM shape) and
  /// HTTP-date (RFC 1123) as fallback.
  /// </summary>
  internal static TimeSpan? TryParseRetryAfter(string? headerValue)
  {
    if (string.IsNullOrWhiteSpace(headerValue))
    {
      return null;
    }

    var trimmed = headerValue.Trim();

    // Integer seconds is the primary case (LiteLLM returns this)
    if (int.TryParse(trimmed, out var seconds) && seconds > 0)
    {
      return TimeSpan.FromSeconds(seconds);
    }

    // HTTP-date fallback
    if (DateTimeOffset.TryParse(trimmed, out var date))
    {
      var delta = date - DateTimeOffset.UtcNow;
      if (delta.TotalSeconds > 0)
      {
        return delta;
      }
    }

    return null;
  }
}
