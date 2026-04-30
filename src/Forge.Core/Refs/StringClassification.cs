namespace Forge.Core.Refs;

public enum StringClassification
{
  Literal,
  Reference,
  Template
}

public static class StringClassifier
{
  /// <summary>Pinned DSL: $$ escape, $. / $item / $index refs, {{ }} templates.</summary>
  public static StringClassification Classify(string s)
  {
    if (s.Length >= 2 && s[0] == '$' && s[1] == '$')
    {
      return StringClassification.Literal;
    }

    if (s.StartsWith("$.", StringComparison.Ordinal)
        || string.Equals(s, "$item", StringComparison.Ordinal)
        || s.StartsWith("$item.", StringComparison.Ordinal)
        || string.Equals(s, "$index", StringComparison.Ordinal)
        || s.StartsWith("$index.", StringComparison.Ordinal))
    {
      return StringClassification.Reference;
    }

    if (s.Contains("{{", StringComparison.Ordinal))
    {
      return StringClassification.Template;
    }

    return StringClassification.Literal;
  }

  /// <summary>Strip one leading $ from doubled-$ literals.</summary>
  public static string UnescapeLiteral(string s)
  {
    if (s.Length >= 2 && s[0] == '$' && s[1] == '$')
    {
      return s[1..];
    }

    return s;
  }

  public static bool IsReference(string s) => Classify(s) == StringClassification.Reference;
}
