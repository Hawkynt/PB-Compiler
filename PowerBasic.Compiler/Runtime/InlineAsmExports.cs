namespace PowerBasic.Compiler.Runtime;

/// <summary>
/// The string-manager routines PowerBASIC documents as callable from inline assembly, and the
/// canonical label each spelling denotes. <c>GetStrLoc</c> and <c>GET$LOC</c> are the same routine
/// under the two names the manual uses for it.
///
/// <para>
/// This lives apart from any one emitter because three of them need the same answer and must agree:
/// the direct code generator resolves the name while assembling, the IR lowering has to know that a
/// name it cannot find a VARIABLE for is nonetheless bound (a call target is not storage, so there
/// is nothing for it to carry), and the machine emitter resolves it again in the back end's own
/// frame. A private copy in each is three places for the list to drift.
/// </para>
/// </summary>
public static class InlineAsmExports {

  private static readonly Dictionary<string, string> _canonical = new(StringComparer.OrdinalIgnoreCase) {
    ["GetStrLoc"] = "GetStrLoc",
    ["GET$LOC"] = "GetStrLoc",
    ["GetStrLen"] = "GetStrLen",
    ["GET$LEN"] = "GetStrLen",
    ["GetStrAlloc"] = "GetStrAlloc",
    ["GET$ALLOC"] = "GetStrAlloc",
    ["RlsStrAlloc"] = "RlsStrAlloc",
    ["RLS$ALLOC"] = "RlsStrAlloc",
  };

  /// <summary>The runtime label <paramref name="name"/> denotes, or null when it names no export.</summary>
  public static string? Canonical(string name)
    => _canonical.TryGetValue(name, out var label) ? label : null;
}
