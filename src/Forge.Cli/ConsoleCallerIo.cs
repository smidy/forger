using Forge.Core.Types;
using Spectre.Console;

namespace Forge.Cli;

/// <summary>
/// <see cref="ICallerIo"/> implementation for interactive terminal runs.
/// </summary>
internal sealed class ConsoleCallerIo : ICallerIo
{
  private string? _pendingNudge;
  private readonly object _lock = new();

  /// <summary>
  /// Store an interjection line for the next <see cref="TryTakeUserInterjection"/> poll.
  /// Thread-safe.
  /// </summary>
  public void EnqueueNudge(string line)
  {
    lock (_lock)
    {
      _pendingNudge = line;
    }
  }

  public async Task<CallerPromptResponse> PromptAsync(CallerPrompt prompt, CancellationToken ct)
  {
    var panel = new Panel(prompt.Question)
    {
      Border = BoxBorder.Rounded,
      Header = new PanelHeader("[magenta]Agent asks:[/]")
    };

    if (prompt.Hint is not null)
    {
      panel = new Panel(
        new Rows(
          new Markup(prompt.Question),
          new Markup($"[grey]{Markup.Escape(prompt.Hint)}[/]")
        ))
      {
        Border = BoxBorder.Rounded,
        Header = new PanelHeader("[magenta]Agent asks:[/]")
      };
    }

    AnsiConsole.Write(panel);

    string? response;
    if (prompt.Choices is { Count: > 0 })
    {
      AnsiConsole.MarkupLine("[grey]Choices:[/]");
      for (var i = 0; i < prompt.Choices.Count; i++)
      {
        AnsiConsole.MarkupLine($"  [cyan]{i + 1}[/]. {Markup.Escape(prompt.Choices[i])}");
      }

      response = await ReadLineWithCancelAsync("Your choice (number or value):", ct).ConfigureAwait(false);
    }
    else
    {
      response = await ReadLineWithCancelAsync("Your response:", ct).ConfigureAwait(false);
    }

    ct.ThrowIfCancellationRequested();
    return new CallerPromptResponse { Response = response ?? string.Empty };
  }

  public Task NotifyAsync(CallerNotice notice, CancellationToken ct)
  {
    var colour = notice.Level switch
    {
      NoticeLevel.Warn => "gold3_1",
      NoticeLevel.Error => "red",
      _ => "grey"
    };

    var icon = notice.Level switch
    {
      NoticeLevel.Warn => "\u26a0 ",
      NoticeLevel.Error => "\u2717 ",
      _ => "\u2139 "
    };

    if (notice.Level == NoticeLevel.Error)
    {
      AnsiConsole.MarkupLine($"[{colour} bold]{icon}{Markup.Escape(notice.Summary)}[/]");
    }
    else
    {
      AnsiConsole.MarkupLine($"[{colour}]{icon}{Markup.Escape(notice.Summary)}[/]");
    }

    if (notice.Detail is not null)
    {
      AnsiConsole.MarkupLine($"[grey]{Markup.Escape(notice.Detail)}[/]");
    }

    return Task.CompletedTask;
  }

  public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
  {
    var riskColour = request.Risk switch
    {
      RiskLevel.High => "red",
      RiskLevel.Medium => "gold3_1",
      _ => "grey"
    };

    AnsiConsole.Write(new Panel(
      new Rows(
        new Markup($"[bold]Action:[/] {Markup.Escape(request.Action)}"),
        new Markup($"[bold]Summary:[/] {Markup.Escape(request.Summary)}"),
        new Markup($"[bold]Risk:[/] [{riskColour}]{request.Risk}[/]")
      ))
    {
      Border = BoxBorder.Rounded,
      Header = new PanelHeader("[yellow]Approval requested[/]")
    });

    var defaultNo = request.Risk is RiskLevel.High or RiskLevel.Medium;
    var choices = defaultNo ? "y/N" : "Y/n";

    var answer = AnsiConsole.Prompt(
      new TextPrompt<string>($"Approve? ({choices}):")
        .AllowEmpty()
        .Validate(input =>
        {
          var trimmed = input.Trim().ToLowerInvariant();
          if (trimmed is "" or "y" or "n" or "yes" or "no")
          {
            return ValidationResult.Success();
          }
          return ValidationResult.Error("Please enter y/yes or n/no.");
        }));

    ct.ThrowIfCancellationRequested();
    var allowed = answer.Trim().ToLowerInvariant() switch
    {
      "y" or "yes" => true,
      "" => !defaultNo,
      _ => false
    };

    return Task.FromResult(new ApprovalDecision
    {
      Allowed = allowed,
      Reason = allowed ? "user-approved" : "user-denied"
    });
  }

  public Task WriteAssistantTextAsync(string text, CancellationToken ct)
  {
    AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(text)}[/]");
    return Task.CompletedTask;
  }

  public Task WriteToolCallSummaryAsync(int iteration, string toolName, string summary, bool isError, CancellationToken ct)
  {
    var colour = isError ? "red" : "grey";
    AnsiConsole.MarkupLine($"[grey]Iter {iteration}[/] [{colour}]{Markup.Escape(toolName)}[/]: {Markup.Escape(summary)}");
    return Task.CompletedTask;
  }

  public Task WriteSystemNoticeAsync(string notice, CancellationToken ct)
  {
    AnsiConsole.MarkupLine($"[grey italic]{Markup.Escape(notice)}[/]");
    return Task.CompletedTask;
  }

  public string? TryTakeUserInterjection()
  {
    lock (_lock)
    {
      var n = _pendingNudge;
      _pendingNudge = null;
      return n;
    }
  }

  private static async Task<string?> ReadLineWithCancelAsync(string prompt, CancellationToken ct)
  {
    if (!ct.CanBeCanceled)
    {
      return AnsiConsole.Prompt(new TextPrompt<string>(prompt).AllowEmpty());
    }

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var tcs = new TaskCompletionSource<string?>();
    var readTask = Task.Run(() =>
    {
      try
      {
        var result = AnsiConsole.Prompt(new TextPrompt<string>(prompt).AllowEmpty());
        tcs.TrySetResult(result);
      }
      catch (Exception ex)
      {
        tcs.TrySetException(ex);
      }
    }, CancellationToken.None);

    using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
    return await tcs.Task.ConfigureAwait(false);
  }
}
