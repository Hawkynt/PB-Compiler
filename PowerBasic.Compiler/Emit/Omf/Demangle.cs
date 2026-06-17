namespace PowerBasic.Compiler.Emit.Omf;

/// <summary>The compiler scheme a mangled C++ public symbol was produced by.</summary>
public enum MangleScheme {
  /// <summary>Not a C++ mangling - either an <c>extern "C"</c> public or a plain symbol.</summary>
  None,
  /// <summary>Borland C++ / Turbo C++ (<c>@name$qi</c>).</summary>
  Borland,
  /// <summary>Watcom C++ (<c>W?name$n(i)i</c> family).</summary>
  Watcom,
  /// <summary>Microsoft Visual C++ (<c>?name@@YAHH@Z</c>).</summary>
  Msvc,
}

/// <summary>The outcome of demangling: the recognised scheme and a readable signature.</summary>
/// <param name="Scheme">Which compiler's scheme matched (<see cref="MangleScheme.None"/> = plain/extern-C).</param>
/// <param name="Pretty">A human-readable rendering, e.g. <c>square(int)</c>; for plain symbols the symbol itself.</param>
/// <param name="Name">The bare function name without decoration, e.g. <c>square</c>.</param>
/// <param name="IsMangled">True when a C++ mangling was recognised (scheme != None).</param>
public readonly record struct Demangled(MangleScheme Scheme, string Pretty, string Name, bool IsMangled);

/// <summary>
/// Demangler for C++ public symbols as the period DOS C++ compilers decorate free
/// functions (docs/LINKER.md "C++ mangled symbols"). Free functions keep the cdecl
/// argument convention; only the <em>name</em> carries the signature, so a
/// <c>DECLARE ... CDECL ALIAS "&lt;mangled&gt;"</c> resolves by the exact public - the
/// demangler is a <em>diagnostic</em> aid that turns an unresolved mangled external
/// back into a legible <c>name(types)</c> so the user can write the right ALIAS.
///
/// Schemes:
/// <list type="bullet">
/// <item><b>Borland/Turbo C++</b> - <c>@name$qi</c>: leading <c>@</c>, name, <c>$q</c>
///   (argument-list marker), then a run of Borland type codes (<c>i</c>=int,
///   <c>l</c>=long, <c>d</c>=double, <c>v</c>=void, <c>p</c>=pointer, <c>z</c>=signed,
///   <c>u</c>=unsigned ...). Verified against genuine BCC 3.1 output.</item>
/// <item><b>Watcom C++</b> - <c>W?name$n(i)i</c>: the parenthesised group is the
///   argument list in Watcom type codes. (Watcom C++ <c>wpp</c> is not staged, so this
///   is parsed structurally, not validated end-to-end.)</item>
/// <item><b>MSVC</b> - <c>?name@@YAHH@Z</c>: <c>?</c> intro, name, <c>@@</c>, a calling/
///   storage group, the argument backref-coded type run, terminated by <c>@Z</c>.</item>
/// </list>
/// <c>extern "C"</c> functions are not mangled (a bare or leading-underscore public);
/// <see cref="Parse"/> reports those as <see cref="MangleScheme.None"/>.
/// </summary>
public static class Demangle {

  /// <summary>
  /// Parses <paramref name="symbol"/> into a <see cref="Demangled"/>. Never throws: an
  /// unrecognised or plain symbol comes back as <see cref="MangleScheme.None"/> with the
  /// symbol echoed as its own pretty form.
  /// </summary>
  public static Demangled Parse(string? symbol) {
    if (string.IsNullOrEmpty(symbol))
      return new(MangleScheme.None, symbol ?? "", symbol ?? "", false);

    if (TryBorland(symbol, out var b)) return b;
    if (TryMsvc(symbol, out var m)) return m;
    if (TryWatcom(symbol, out var w)) return w;

    // plain / extern "C": strip a single leading cdecl underscore for the readable name
    var bare = symbol.StartsWith('_') ? symbol[1..] : symbol;
    return new(MangleScheme.None, symbol, bare, false);
  }

  /// <summary>Convenience: whether <paramref name="symbol"/> looks like a recognised C++ mangling.</summary>
  public static bool IsMangled(string? symbol) => Parse(symbol).IsMangled;

  // ---- Borland / Turbo C++ : @name$q<args> ---------------------------------

  private static bool TryBorland(string s, out Demangled result) {
    result = default;
    // free functions: leading '@', then name, then "$q" introducing the arg list.
    // (member functions use "$<class>$..." which we do not decode; still report the name.)
    if (s.Length < 2 || s[0] != '@')
      return false;
    var dollar = s.IndexOf('$');
    if (dollar <= 1)
      return false;
    var name = s[1..dollar];
    if (!IsIdentifier(name))
      return false;
    var rest = s[(dollar + 1)..];
    // free function: 'q' (sometimes 'q' is preceded by qualifiers we ignore here)
    if (rest.Length == 0 || rest[0] != 'q') {
      // a qualified/member symbol we can still name but not fully decode
      result = new(MangleScheme.Borland, name + "(...)", name, true);
      return true;
    }
    var args = ParseBorlandArgs(rest[1..]);
    result = new(MangleScheme.Borland, $"{name}({string.Join(", ", args)})", name, true);
    return true;
  }

  /// <summary>Decodes the Borland argument type-code run after <c>$q</c> into readable type names.</summary>
  private static List<string> ParseBorlandArgs(string codes) {
    var args = new List<string>();
    var i = 0;
    while (i < codes.Length) {
      var (type, next) = ReadBorlandType(codes, i);
      if (type == null) break;          // unrecognised tail -> stop, keep what we have
      if (type == "void" && args.Count == 0 && next >= codes.Length)
        break;                          // a lone 'v' means an empty parameter list
      args.Add(type);
      i = next;
    }
    return args;
  }

  /// <summary>Reads one Borland type code (with leading pointer/qualifier prefixes) starting at <paramref name="i"/>.</summary>
  private static (string? Type, int Next) ReadBorlandType(string c, int i) {
    var ptr = 0;
    var unsigned = false;
    var signed = false;
    // prefixes: p=pointer, r=reference, u=unsigned, z=signed, x=const, y=volatile
    for (; i < c.Length; i++) {
      switch (c[i]) {
        case 'p': ptr++; continue;
        case 'r': ptr++; continue;       // reference rendered as a pointer for readability
        case 'u': unsigned = true; continue;
        case 'z': signed = true; continue;
        case 'x' or 'y': continue;       // const / volatile qualifier - skip for readability
      }
      break;
    }
    if (i >= c.Length)
      return (null, i);
    string? baseType = c[i] switch {
      'v' => "void",
      'c' => signed ? "signed char" : unsigned ? "unsigned char" : "char",
      's' => unsigned ? "unsigned short" : "short",
      'i' => unsigned ? "unsigned int" : "int",
      'l' => unsigned ? "unsigned long" : "long",
      'f' => "float",
      'd' => "double",
      'g' => "long double",
      _ => null,
    };
    if (baseType == null)
      return (null, i);
    var rendered = baseType + (ptr > 0 ? " " + new string('*', ptr) : "");
    return (rendered, i + 1);
  }

  // ---- Microsoft Visual C++ : ?name@@YAH...@Z -------------------------------

  private static bool TryMsvc(string s, out Demangled result) {
    result = default;
    if (s.Length < 4 || s[0] != '?')
      return false;
    var at = s.IndexOf("@@", StringComparison.Ordinal);
    if (at <= 1)
      return false;
    var name = s[1..at];
    if (!IsIdentifier(name))
      return false;
    // after @@ : function property char (Y=near free function) + calling-conv char, then
    // the return type, then the argument type list, ending with @Z (or just Z).
    var rest = s[(at + 2)..];
    if (rest.Length < 3 || rest[0] != 'Y') {
      result = new(MangleScheme.Msvc, name + "(...)", name, true);
      return true;
    }
    // rest[1] = calling convention (A=cdecl, G=stdcall, E=thiscall, I=fastcall); skip it.
    var i = 2;
    // return type: read and discard one type
    var (_, afterRet) = ReadMsvcType(rest, i);
    i = afterRet;
    var args = new List<string>();
    while (i < rest.Length && rest[i] != '@' && rest[i] != 'Z') {
      var (type, next) = ReadMsvcType(rest, i);
      if (type == null) break;
      if (type == "void" && args.Count == 0) { i = next; break; } // (void) -> empty list
      args.Add(type);
      if (next == i) break;
      i = next;
    }
    result = new(MangleScheme.Msvc, $"{name}({string.Join(", ", args)})", name, true);
    return true;
  }

  /// <summary>Reads one MSVC primitive type code (with leading pointer prefixes) at <paramref name="i"/>.</summary>
  private static (string? Type, int Next) ReadMsvcType(string c, int i) {
    var ptr = 0;
    // pointer encodings: P=pointer, A=reference, then a CV/this group char we skip.
    while (i < c.Length && (c[i] == 'P' || c[i] == 'A' || c[i] == 'Q' || c[i] == 'R')) {
      ptr++;
      i++;
      if (i < c.Length && c[i] is >= 'A' and <= 'Z') i++;   // CV-qualifier of the pointee
    }
    if (i >= c.Length)
      return (null, i);
    string? baseType = c[i] switch {
      'X' => "void",
      'D' => "char",
      'C' => "signed char",
      'E' => "unsigned char",
      'F' => "short",
      'G' => "unsigned short",
      'H' => "int",
      'I' => "unsigned int",
      'J' => "long",
      'K' => "unsigned long",
      'M' => "float",
      'N' => "double",
      'O' => "long double",
      _ => null,
    };
    if (baseType == null)
      return (null, i);
    var rendered = baseType + (ptr > 0 ? " " + new string('*', ptr) : "");
    return (rendered, i + 1);
  }

  // ---- Watcom C++ : W?name$n(args)ret ---------------------------------------

  private static bool TryWatcom(string s, out Demangled result) {
    result = default;
    // Watcom free functions carry a parenthesised argument group; the name precedes the
    // first '$' (and an optional leading 'W?' / 'W' decoration).
    var open = s.IndexOf('(');
    var close = s.LastIndexOf(')');
    if (open <= 0 || close <= open)
      return false;
    var head = s[..open];
    var dollar = head.IndexOf('$');
    var rawName = dollar > 0 ? head[..dollar] : head;
    if (rawName.StartsWith("W?", StringComparison.Ordinal)) rawName = rawName[2..];
    else if (rawName.StartsWith('W')) rawName = rawName[1..];
    if (rawName.EndsWith('_')) rawName = rawName[..^1];   // Watcom appends a trailing underscore to the public
    if (!IsIdentifier(rawName))
      return false;
    var inner = s[(open + 1)..close];
    var args = ParseWatcomArgs(inner);
    result = new(MangleScheme.Watcom, $"{rawName}({string.Join(", ", args)})", rawName, true);
    return true;
  }

  /// <summary>Decodes the Watcom parenthesised argument run (a sequence of single-letter type codes).</summary>
  private static List<string> ParseWatcomArgs(string codes) {
    var args = new List<string>();
    var i = 0;
    while (i < codes.Length) {
      var ptr = 0;
      while (i < codes.Length && (codes[i] == 'p' || codes[i] == 'r')) { ptr++; i++; }
      if (i >= codes.Length) break;
      string? baseType = codes[i] switch {
        'v' => "void",
        'c' => "char",
        'a' => "signed char",
        'b' => "unsigned char",
        's' => "short",
        't' => "unsigned short",
        'i' => "int",
        'u' => "unsigned int",
        'l' => "long",
        'm' => "unsigned long",
        'f' => "float",
        'd' => "double",
        _ => null,
      };
      i++;
      if (baseType == null) break;
      if (baseType == "void" && args.Count == 0 && ptr == 0 && i >= codes.Length) break;
      args.Add(baseType + (ptr > 0 ? " " + new string('*', ptr) : ""));
    }
    return args;
  }

  private static bool IsIdentifier(string s) {
    if (s.Length == 0) return false;
    if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
    foreach (var ch in s)
      if (!(char.IsLetterOrDigit(ch) || ch == '_'))
        return false;
    return true;
  }
}
