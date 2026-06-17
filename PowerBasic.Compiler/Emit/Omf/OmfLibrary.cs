namespace PowerBasic.Compiler.Emit.Omf;

/// <summary>
/// A foreign OMF library (.LIB) presented to the <see cref="Linker"/> for lazy,
/// dictionary-driven selective extraction (docs/LINKER.md "Testing"). Unlike eagerly
/// converting every member to a <see cref="PbuFile"/>, this resolves a member only when
/// the linker actually needs one of its symbols, so a 200-member C runtime contributes
/// just the handful of objects that satisfy unresolved imports (transitively) - the rest
/// are never even lowered. That both "trims" the image to what is referenced and avoids
/// choking on unused members that use OMF features the tiny model cannot host.
/// </summary>
public sealed class OmfLibrary {

  private readonly List<OmfModule> _members;
  private readonly IReadOnlyDictionary<string, int> _symbolToMember;
  private readonly Dictionary<int, PbuFile> _converted = [];
  private readonly HashSet<int> _provided = [];

  public OmfLibrary(byte[] bytes) {
    this._members = OmfReader.ReadLibrary(bytes, out this._symbolToMember);
  }

  /// <summary>Total members in the library (the universe before trimming).</summary>
  public int MemberCount => this._members.Count;

  /// <summary>Members actually lowered+pulled so far (the trimmed set).</summary>
  public int ProvidedCount => this._provided.Count;

  /// <summary>True when the library's dictionary advertises <paramref name="symbol"/> (exact, then cdecl "_"+name).</summary>
  public bool Defines(string symbol) => this.FindMember(symbol, out _);

  /// <summary>
  /// Returns the (lowered) member that defines <paramref name="symbol"/> the first time it
  /// is asked for, or null when the library has no such symbol or the member was already
  /// handed out (so a second symbol from the same member does not re-add it). Conversion is
  /// cached and happens at most once per member.
  /// </summary>
  public PbuFile? Provide(string symbol) {
    if (!this.FindMember(symbol, out var member))
      return null;
    if (!this._provided.Add(member))
      return null; // already contributed via one of its other symbols
    if (!this._converted.TryGetValue(member, out var pbu)) {
      pbu = OmfToPbu.Convert(this._members[member]);
      this._converted[member] = pbu;
    }
    return pbu;
  }

  private bool FindMember(string symbol, out int member) {
    if (this._symbolToMember.TryGetValue(symbol, out member))
      return true;
    // cdecl auto-decoration: a BASIC import "foo" may be the C public "_foo".
    if (!symbol.StartsWith('_') && this._symbolToMember.TryGetValue("_" + symbol, out member))
      return true;
    return false;
  }
}
