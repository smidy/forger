namespace Forge.Core.Exceptions;

/// <summary>Thrown when the user types <c>/exit</c> in chat mode.</summary>
public sealed class ChatExitException : ForgeException
{
  public const string ExitCommand = "/exit";

  public ChatExitException() : base($"User requested {ExitCommand}.") { }
  public ChatExitException(string message) : base(message) { }
}
