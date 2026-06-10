namespace PowerBasic.Compiler.Syntax;

/// <summary>Resolves source file names (e.g. <c>$INCLUDE</c> targets) to source text.</summary>
public interface ISourceProvider {

  /// <summary>
  /// Tries to load <paramref name="name"/>; <paramref name="includedFrom"/> is the resolved
  /// name of the including file (null for the main file) so relative lookups can be anchored.
  /// </summary>
  bool TryReadSource(string name, string? includedFrom, out string text, out string resolvedName);
}

/// <summary>Loads sources from the file system, resolving includes relative to the including file.</summary>
public sealed class FileSourceProvider : ISourceProvider {

  public bool TryReadSource(string name, string? includedFrom, out string text, out string resolvedName) {
    var candidate = name;
    if (!Path.IsPathRooted(candidate) && includedFrom != null) {
      var anchor = Path.GetDirectoryName(includedFrom);
      if (!string.IsNullOrEmpty(anchor))
        candidate = Path.Combine(anchor, name);
    }

    if (!File.Exists(candidate)) {
      (text, resolvedName) = ("", candidate);
      return false;
    }

    text = File.ReadAllText(candidate);
    resolvedName = candidate;
    return true;
  }
}

/// <summary>
/// Loads sources from the file system, trying the including file's directory first,
/// then each configured search directory in order (mirrors a compiler include path).
/// </summary>
public sealed class SearchPathSourceProvider(params string[] searchPaths) : ISourceProvider {

  public bool TryReadSource(string name, string? includedFrom, out string text, out string resolvedName) {
    foreach (var candidate in this.Candidates(name, includedFrom))
      if (File.Exists(candidate)) {
        text = File.ReadAllText(candidate);
        resolvedName = candidate;
        return true;
      }

    (text, resolvedName) = ("", name);
    return false;
  }

  private IEnumerable<string> Candidates(string name, string? includedFrom) {
    if (Path.IsPathRooted(name)) {
      yield return name;
      yield break;
    }

    var anchor = includedFrom == null ? null : Path.GetDirectoryName(includedFrom);
    if (!string.IsNullOrEmpty(anchor))
      yield return Path.Combine(anchor, name);

    foreach (var path in searchPaths)
      yield return Path.Combine(path, name);

    yield return name;
  }
}
