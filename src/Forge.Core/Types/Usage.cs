namespace Forge.Core.Types;

public sealed record Usage(
  int PromptTokens,
  int CompletionTokens,
  int? CacheReadTokens = null,
  int? CacheCreationTokens = null);
