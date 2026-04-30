using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Exceptions;
using Forge.Core.Json;

namespace Forge.Cli;

internal static class ExitCodeMapper
{
  public const int Ok = 0;
  public const int UserError = 1;
  public const int RuntimeFailure = 2;
  public const int PartialFailure = 3;
  public const int RateLimited = 6;
  public const int NeedsCaller = 7;
  public const int Cancelled = 130;

  public static int ExitCodeFor(Exception ex) => ex switch
  {
    OperationCanceledException => Cancelled,
    ValidationException => UserError,
    ConfigException => UserError,
    CallerDeferredException => NeedsCaller,
    PartialFailureException => PartialFailure,
    RateLimitedException => RateLimited,
    AgentException => RuntimeFailure,
    QuotaExhaustedException => RuntimeFailure,
    ProviderException => RuntimeFailure,
    PipelineException => RuntimeFailure,
    ForgeException => RuntimeFailure,
    _ => RuntimeFailure,
  };

  public static string? RenderStderr(Exception ex)
  {
    switch (ex)
    {
      case OperationCanceledException:
        return "Cancelled.";
      case ValidationException v:
      {
        var obj = new JsonObject { ["error"] = v.Message };
        if (!string.IsNullOrEmpty(v.JsonPath)) obj["path"] = v.JsonPath;
        return obj.ToJsonString(JsonSerializationDefaults.General);
      }
      case ConfigException c:
        return new JsonObject { ["error"] = c.Message }.ToJsonString(JsonSerializationDefaults.General);
      case RateLimitedException rl:
      {
        var obj = new JsonObject
        {
          ["error"] = rl.Message,
          ["errorKind"] = "rate_limited",
          ["statusCode"] = 429,
          ["resumable"] = true
        };
        if (rl.RetryAfter is { } ts)
        {
          obj["retryAfterSeconds"] = ts.TotalSeconds;
        }
        return obj.ToJsonString(JsonSerializationDefaults.General);
      }
      case QuotaExhaustedException qe:
        return new JsonObject
        {
          ["error"] = qe.Message,
          ["errorKind"] = "quota_exhausted",
          ["statusCode"] = 429,
          ["resumable"] = false
        }.ToJsonString(JsonSerializationDefaults.General);
      case CallerDeferredException cd:
        return new JsonObject { ["error"] = cd.Message, ["status"] = "needs_caller" }.ToJsonString(JsonSerializationDefaults.General);
      case ProviderException p:
      {
        var obj = new JsonObject { ["error"] = p.Message };
        if (p.StatusCode is int sc) obj["statusCode"] = sc;
        return obj.ToJsonString(JsonSerializationDefaults.General);
      }
      case PartialFailureException pf:
        return new JsonObject { ["error"] = pf.Message }.ToJsonString(JsonSerializationDefaults.General);
      case ForgeException f:
        return new JsonObject { ["error"] = f.Message }.ToJsonString(JsonSerializationDefaults.General);
      default:
        return ex.ToString();
    }
  }
}
