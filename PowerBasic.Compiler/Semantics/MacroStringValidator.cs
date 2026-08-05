namespace PowerBasic.Compiler.Semantics;

/// <summary>
/// Compile-time checking of the two macro languages BASIC embeds in string literals: the tune
/// strings <c>PLAY</c> takes and the turtle-graphics strings <c>DRAW</c> takes.
///
/// Both are ordinarily interpreted at run time, byte by byte, and a typo in one is a runtime error
/// at best and silence at worst - <c>PLAY</c> in particular currently binds, warns that it does
/// nothing, and compiles, so a malformed tune reaches the executable with nothing said about it.
/// When the string is a CONSTANT the whole grammar is knowable at compile time, and there is no
/// reason to wait.
///
/// Only constants are checked. A computed string is nobody's business here, and a partially
/// checkable one - <c>"T120 " + tempo$</c> - is not checked either, because the concatenation is
/// what the folder hands over or nothing is.
/// </summary>
public static class MacroStringValidator {

  /// <summary>Describes what is wrong with a macro string, or null when it is well formed.</summary>
  public static string? ValidatePlay(string tune) {
    var at = 0;
    while (at < tune.Length) {
      var c = char.ToUpperInvariant(tune[at]);
      if (char.IsWhiteSpace(c)) {
        ++at;
        continue;
      }
      ++at;
      switch (c) {
        // a note, optionally sharpened or flattened, optionally with its own length, optionally dotted
        case >= 'A' and <= 'G':
          while (at < tune.Length && tune[at] is '+' or '#' or '-')
            ++at;
          ReadNumber(tune, ref at);
          while (at < tune.Length && tune[at] == '.')
            ++at;
          break;

        // the ones that need a number after them, each with its own range
        case 'N': if (Range(tune, ref at, c, 0, 84) is { } n) return n; break;
        case 'O': if (Range(tune, ref at, c, 0, 6) is { } o) return o; break;
        case 'L': if (Range(tune, ref at, c, 1, 64) is { } l) return l; break;
        case 'P': if (Range(tune, ref at, c, 1, 64) is { } p) return p; break;
        case 'T': if (Range(tune, ref at, c, 32, 255) is { } t) return t; break;

        // M takes a letter, not a number: articulation or foreground/background
        case 'M':
          if (at >= tune.Length || char.ToUpperInvariant(tune[at]) is not ('N' or 'L' or 'S' or 'F' or 'B'))
            return $"M must be followed by N, L, S, F or B at position {at + 1}";
          ++at;
          break;

        case '>' or '<':
          break;

        // X executes another string at run time, so its contents cannot be known from here
        case 'X':
          return null;

        default:
          return $"'{tune[at - 1]}' is not a PLAY command, at position {at}";
      }
    }
    return null;
  }

  /// <summary>Describes what is wrong with a DRAW string, or null when it is well formed.</summary>
  public static string? ValidateDraw(string picture) {
    var at = 0;
    while (at < picture.Length) {
      var c = char.ToUpperInvariant(picture[at]);
      if (char.IsWhiteSpace(c)) {
        ++at;
        continue;
      }

      // B and N are prefixes - move without drawing, and draw without moving - so they attach to
      // whatever comes next rather than standing alone
      if (c is 'B' or 'N') {
        ++at;
        if (at >= picture.Length)
          return $"{c} is a prefix and must be followed by a movement, at position {at}";
        continue;
      }
      ++at;

      switch (c) {
        case 'U' or 'D' or 'L' or 'R' or 'E' or 'F' or 'G' or 'H':
          ReadNumber(picture, ref at);                    // absent means one step
          break;

        // M is the only one taking a pair, and either coordinate may be signed to mean "relative"
        case 'M': {
          SkipSign(picture, ref at);
          if (!ReadNumber(picture, ref at))
            return $"M needs a coordinate pair, at position {at + 1}";
          if (at >= picture.Length || picture[at] != ',')
            return $"M needs a comma between its coordinates, at position {at + 1}";
          ++at;
          SkipSign(picture, ref at);
          if (!ReadNumber(picture, ref at))
            return $"M needs a second coordinate, at position {at + 1}";
          break;
        }

        case 'A': if (Range(picture, ref at, c, 0, 3) is { } a) return a; break;
        case 'C': if (Range(picture, ref at, c, 0, 255) is { } col) return col; break;
        case 'S': if (Range(picture, ref at, c, 1, 255) is { } s) return s; break;

        case 'T':
          if (at >= picture.Length || char.ToUpperInvariant(picture[at]) != 'A')
            return $"T must be TA (turn angle), at position {at + 1}";
          ++at;
          SkipSign(picture, ref at);
          if (!ReadNumber(picture, ref at))
            return $"TA needs an angle, at position {at + 1}";
          break;

        case 'P': {
          if (Range(picture, ref at, c, 0, 255) is { } fill)
            return fill;
          if (at >= picture.Length || picture[at] != ',')
            return $"P needs a border colour after the fill colour, at position {at + 1}";
          ++at;
          if (Range(picture, ref at, c, 0, 255) is { } border)
            return border;
          break;
        }

        case 'X':
          return null;                                    // executes another string at run time

        default:
          return $"'{picture[at - 1]}' is not a DRAW command, at position {at}";
      }
    }
    return null;
  }

  private static void SkipSign(string text, ref int at) {
    if (at < text.Length && text[at] is '+' or '-')
      ++at;
  }

  /// <summary>Consumes a run of digits; false when there was not one.</summary>
  private static bool ReadNumber(string text, ref int at) {
    var start = at;
    while (at < text.Length && char.IsAsciiDigit(text[at]))
      ++at;
    return at > start;
  }

  /// <summary>Consumes the number a command requires and checks its range; null when it is fine.</summary>
  private static string? Range(string text, ref int at, char command, int low, int high) {
    var start = at;
    if (!ReadNumber(text, ref at))
      return $"{command} needs a number after it, at position {start + 1}";

    var value = long.Parse(text[start..at]);
    return value < low || value > high
      ? $"{command}{value} is out of range - {command} takes {low} to {high}, at position {start + 1}"
      : null;
  }
}
