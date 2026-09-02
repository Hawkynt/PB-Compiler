using PowerBasic.Compiler.Runtime;

namespace PowerBasic.Compiler.Syntax;

public sealed partial class Parser {
  /// <summary>
  /// PB36 runtime-target directives extend the historical metastatement grammar. ParseMeta builds a
  /// List&lt;Token&gt;, so this exact overload is selected before the legacy IReadOnlyList validator; every
  /// unrelated directive delegates back to the historical grammar unchanged.
  /// </summary>
  private void ValidateMetaArguments(Token command, List<Token> arguments) {
    switch (command.Text.ToUpperInvariant()) {
      case "CPU":
        this.ValidateRuntimeCpuMeta(command, arguments);
        return;
      case "ISA":
        this.Require(LanguageFeature.ExtendedMetaArguments);
        NormalizeRuntimeTargetNames(arguments);
        ValidateIsaPolicyMeta(command, arguments);
        return;
      case "FPU" or "X87":
        this.Require(LanguageFeature.ExtendedMetaArguments);
        ValidateIsaModeMeta(command, arguments);
        return;
      case "FLOAT":
        ValidateFloatPolicyMeta(command, arguments);
        return;
      default:
        this.ValidateMetaArguments(command, (IReadOnlyList<Token>)arguments);
        return;
    }
  }

  private void ValidateRuntimeCpuMeta(Token command, List<Token> arguments) {
    try {
      this.ValidateMetaArguments(command, (IReadOnlyList<Token>)arguments);
      return;
    } catch (ParserException) {
      // The legacy validator intentionally knows only the historical CPU forms. PB36 adds feature-only
      // targets and the complete RuntimeTarget feature vocabulary below.
    }

    this.Require(LanguageFeature.ExtendedMetaArguments);
    NormalizeRuntimeTargetNames(arguments);
    if (arguments.Count == 0 || !IsRuntimeCpuHead(arguments[0])
        || arguments.Skip(1).Any(token => token.Kind != TokenKind.Identifier || !IsRuntimeCpuFeature(token.Text)))
      throw new ParserException("$CPU requires a supported x86 generation and/or runtime ISA feature", command.Position);
  }

  private static bool IsRuntimeCpuHead(Token token) {
    if (token.Kind == TokenKind.IntegerLiteral)
      return token.IntegerValue is 86 or 186 or 286 or 386 or 486 or 586 or 686
        or 8086 or 80186 or 80286 or 80386 or 80486 or 80586 or 80686;
    if (token.Kind != TokenKind.Identifier)
      return false;

    var target = RuntimeTarget.For(token.Text);
    return target != RuntimeTarget.Baseline;
  }

  private static bool IsRuntimeCpuFeature(string feature) =>
    RuntimeTarget.For("8086", [feature]).Features != RuntimeCpuFeatures.None;

  private static void ValidateIsaPolicyMeta(Token command, List<Token> arguments) {
    var significant = arguments.Where(token => token.Kind is not (TokenKind.Comma or TokenKind.Equals)).ToArray();
    if (significant is not [{ Kind: TokenKind.Identifier } key, { Kind: TokenKind.Identifier } mode]
        || string.IsNullOrWhiteSpace(key.Text) || !RuntimeIsaPolicy.TryParseMode(mode.Text, out _))
      throw new ParserException($"${command.Text} expects an ISA/mnemonic and NATIVE, EMULATE, ERROR or AUTO",
        command.Position);

    arguments.Clear();
    arguments.Add(key);
    arguments.Add(mode);
  }

  private static void ValidateIsaModeMeta(Token command, IReadOnlyList<Token> arguments) {
    if (arguments is [{ Kind: TokenKind.Identifier } mode] && RuntimeIsaPolicy.TryParseMode(mode.Text, out _))
      return;
    throw new ParserException($"${command.Text} expects NATIVE, EMULATE, ERROR or AUTO", command.Position);
  }

  private static void ValidateFloatPolicyMeta(Token command, IReadOnlyList<Token> arguments) {
    if (arguments is [{ Kind: TokenKind.Identifier } mode]
        && RuntimeIsaPolicy.NormalizeKey(mode.Text) is "NPX" or "EMULATE" or "PROCEDURE")
      return;
    throw new ParserException("$FLOAT expects NPX, EMULATE or PROCEDURE", command.Position);
  }

  private static void NormalizeRuntimeTargetNames(List<Token> arguments) {
    for (var i = 0; i < arguments.Count - 1; ++i) {
      if (arguments[i] is not { Kind: TokenKind.Identifier } prefix)
        continue;

      if (prefix.Text.Equals("SSE4", StringComparison.OrdinalIgnoreCase)
          && arguments[i + 1] is { Kind: TokenKind.FloatLiteral } suffix
          && suffix.Text is ".1" or ".2") {
        arguments[i] = new(TokenKind.Identifier, prefix.Text + suffix.Text, prefix.Position);
        arguments.RemoveAt(i + 1);
        continue;
      }

      if (!prefix.Text.Equals("AVX", StringComparison.OrdinalIgnoreCase) || i + 2 >= arguments.Count
          || arguments[i + 1].Kind != TokenKind.Minus)
        continue;

      var width = arguments[i + 2];
      if (width.Kind != TokenKind.IntegerLiteral || width.IntegerValue != 512)
        continue;

      arguments[i] = new(TokenKind.Identifier, prefix.Text + "-" + width.Text, prefix.Position);
      arguments.RemoveRange(i + 1, 2);
    }
  }
}
