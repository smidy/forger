namespace Forge.Core.Exceptions;

public abstract class ForgeException : Exception
{
  protected ForgeException(string message) : base(message) { }
  protected ForgeException(string message, Exception inner) : base(message, inner) { }
}

public sealed class ValidationException : ForgeException
{
  public string? JsonPath { get; }

  public ValidationException(string message) : this(message, null) { }

  public ValidationException(string message, string? jsonPath)
    : base(message)
  {
    JsonPath = jsonPath;
  }
}

public sealed class ConfigException : ForgeException
{
  public ConfigException(string message) : base(message) { }
  public ConfigException(string message, Exception inner) : base(message, inner) { }
}

public sealed class AgentException : ForgeException
{
  public AgentException(string message) : base(message) { }
}

public class ProviderException : ForgeException
{
  public int? StatusCode { get; }
  public ProviderException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;
}

public sealed class PipelineException : ForgeException
{
  public PipelineException(string message) : base(message) { }
}

public sealed class PartialFailureException : ForgeException
{
  public PartialFailureException(string message) : base(message) { }
}

/// <summary>
/// A transient 429 rate-limit response from the LLM provider. The run is
/// resumable; wrapper scripts should honour <see cref="RetryAfter"/> before
/// retrying. Non-sealed so a future provider-specific subtype (e.g. per-tenant
/// token-bucket exhaustion) can extend without breaking the matcher.
/// </summary>
public class RateLimitedException : ProviderException
{
  public TimeSpan? RetryAfter { get; }
  public RateLimitedException(string message, TimeSpan? retryAfter)
    : base(message, statusCode: 429)
  {
    RetryAfter = retryAfter;
  }
}

/// <summary>
/// A terminal 429 whose body matched a quota-exhausted pattern. Resume against
/// the same provider will fail the same way — operator action (billing) required.
/// </summary>
public sealed class QuotaExhaustedException : ProviderException
{
  public QuotaExhaustedException(string message) : base(message, statusCode: 429) { }
}

/// <summary>
/// Thrown by <c>HeadlessCallerIo.PromptAsync</c> when <see cref="PromptBehavior.Defer"/>
/// is active. The <c>PipelineExecutor</c> catches this, writes <c>pending_question.json</c>,
/// transitions the stage to <c>needs_caller</c>, and exits with code 7.
/// Carries enough data for the resume path to replay the pending prompt.
/// </summary>
public sealed class CallerDeferredException : ForgeException
{
  /// <summary>The serialized pending-question payload that was written to disk.</summary>
  public string PendingQuestionJson { get; }

  /// <summary>The stage directory where <c>pending_question.json</c> was written.</summary>
  public string StageDir { get; }

  /// <summary>The tool call ID that triggered the prompt.</summary>
  public string? ToolCallId { get; }

  public CallerDeferredException(string pendingQuestionJson, string stageDir, string? toolCallId)
    : base("Agent is waiting for caller input. Resume with: forge resume <run-id> --answer '<json>'")
  {
    PendingQuestionJson = pendingQuestionJson;
    StageDir = stageDir;
    ToolCallId = toolCallId;
  }
}
