namespace Forge.Cli;

internal static class InputReader
{
  public static async Task<string> ReadInputJsonAsync(string? raw)
  {
    if (string.IsNullOrWhiteSpace(raw))
    {
      return "{}";
    }

    if (raw == "-")
    {
      using var reader = new StreamReader(Console.OpenStandardInput());
      return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    if (raw.StartsWith('@'))
    {
      return await File.ReadAllTextAsync(raw[1..]).ConfigureAwait(false);
    }

    return raw;
  }
}
