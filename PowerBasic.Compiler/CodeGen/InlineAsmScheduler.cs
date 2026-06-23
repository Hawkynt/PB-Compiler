namespace PowerBasic.Compiler.CodeGen;

/// <summary>
/// pb36 inline-assembly instruction scheduler: reorders a block of consecutive single-instruction
/// <c>!</c> lines to <b>group memory and ALU operations</b> and let independent dependency chains
/// interleave (better execution-port / U-V-pipe utilization on a Pentium-class CPU). It is strictly
/// dependency-preserving - a conservative def/use model (register RAW/WAR/WAW, flags, and memory
/// where any write-involving pair is ordered) yields a partial order, and the result is one valid
/// topological order - so the reordered block is semantically identical (output-preserving).
///
/// SAFETY BY CONSTRUCTION: this analysis only *decides* the order; the lines are still emitted by the
/// unchanged text assembler. Anything it cannot classify with certainty (an unknown mnemonic, a jump,
/// a label, a segment/FPU/SIMD operand, a multi-instruction line) makes the whole block non-reorderable,
/// so it is emitted verbatim - there is no path to a miscompile from an under-modelled instruction.
/// </summary>
public static class InlineAsmScheduler {

  // the eight general-purpose word "slots"; a byte half or 32-bit name maps to its word slot
  private const int FlagsBit = 8;

  private readonly record struct Instr(
    int Original, ushort Reads, ushort Writes, bool ReadsFlags, bool WritesFlags,
    bool MemRead, bool MemWrite, string? MemKey) {
    public bool TouchesMemory => this.MemRead || this.MemWrite;
  }

  /// <summary>
  /// Returns the new order (a permutation of <c>0..lines.Count-1</c>) that groups memory/ALU work
  /// while preserving every dependency, or <c>null</c> when the block is left unchanged - because a
  /// line is not confidently schedulable, or the dependency-respecting schedule equals the input.
  /// Never changes semantics: the result is one valid topological order of the dependency partial order.
  /// </summary>
  public static int[]? Schedule(IReadOnlyList<string> lines) {
    if (lines.Count < 3)
      return null; // nothing meaningful to interleave

    var instrs = new Instr[lines.Count];
    for (var i = 0; i < lines.Count; ++i) {
      if (Describe(lines[i], i) is not { } d)
        return null; // an un-modelled line -> leave the whole block verbatim
      instrs[i] = d;
    }

    // dependency edges i -> j (i before j, conflicting); a valid schedule keeps every such pair ordered
    var n = instrs.Length;
    var after = new List<int>[n];
    var indeg = new int[n];
    for (var i = 0; i < n; ++i)
      after[i] = [];
    for (var j = 0; j < n; ++j)
      for (var i = 0; i < j; ++i)
        if (Conflicts(instrs[i], instrs[j])) {
          after[i].Add(j);
          ++indeg[j];
        }

    // list schedule: among ready instructions prefer the same class (memory vs ALU) as the last
    // emitted to cluster the ports, breaking ties by original order (stable -> minimal disturbance)
    var order = new List<int>(n);
    var ready = new List<int>();
    for (var i = 0; i < n; ++i)
      if (indeg[i] == 0)
        ready.Add(i);
    var lastTouchedMemory = false;
    while (ready.Count > 0) {
      var pick = -1;
      foreach (var c in ready)
        if (pick < 0
            || (instrs[c].TouchesMemory == lastTouchedMemory && instrs[pick].TouchesMemory != lastTouchedMemory)
            || (instrs[c].TouchesMemory == instrs[pick].TouchesMemory && c < pick))
          pick = c;
      ready.Remove(pick);
      order.Add(pick);
      lastTouchedMemory = instrs[pick].TouchesMemory;
      foreach (var k in after[pick])
        if (--indeg[k] == 0)
          ready.Add(k);
    }

    // already in original order? avoid pointless churn
    var changed = false;
    for (var i = 0; i < n; ++i)
      if (order[i] != i) { changed = true; break; }
    return changed ? [.. order] : null;
  }

  private static bool Conflicts(Instr a, Instr b) {
    if ((a.Writes & (b.Reads | b.Writes)) != 0 || (a.Reads & b.Writes) != 0)
      return true;                                                  // register RAW / WAR / WAW
    if ((a.WritesFlags && (b.ReadsFlags || b.WritesFlags)) || (a.ReadsFlags && b.WritesFlags))
      return true;                                                  // flags ordering
    if ((a.MemWrite && (b.MemRead || b.MemWrite)) || (a.MemRead && b.MemWrite))
      return MemMayAlias(a.MemKey, b.MemKey);                       // memory ordering (two reads never conflict)
    return false;
  }

  /// <summary>Two memory references may alias unless both are distinct, fully-known direct cells.</summary>
  private static bool MemMayAlias(string? a, string? b) {
    if (a is null || b is null || a == "?" || b == "?")
      return true;                                                  // an unknown/indexed reference aliases everything
    return a == b;                                                  // distinct named cells do not alias
  }

  #region single-instruction def/use model

  private static Instr? Describe(string rawLine, int original) {
    // strip a trailing comment; a multi-instruction or label/directive line is not schedulable
    var line = rawLine;
    var semi = line.IndexOf(';');
    if (semi >= 0)
      line = line[..semi];
    line = line.Trim();
    if (line.Length == 0 || line.Contains(':') || line.Contains('\n') || line.Contains('\r'))
      return null;

    var space = line.IndexOfAny([' ', '\t']);
    var mnemonic = (space < 0 ? line : line[..space]).ToUpperInvariant();
    var rest = space < 0 ? "" : line[(space + 1)..].Trim();
    var operands = rest.Length == 0 ? [] : rest.Split(',').Select(o => o.Trim()).ToArray();

    if (!_modelled.TryGetValue(mnemonic, out var shape) || operands.Length != shape.Arity)
      return null;

    ushort reads = 0, writes = 0;
    bool memRead = false, memWrite = false;
    string? memKey = null;

    bool ApplyOperand(string op, bool isRead, bool isWrite) {
      if (RegSlot(op) is { } slot) {
        if (slot < 0)
          return false;                       // a segment/FPU/SIMD register: not schedulable
        var bit = (ushort)(1 << slot);
        if (isRead || IsByteReg(op))           // a byte-register write is a partial (read-modify) update
          reads |= bit;
        if (isWrite)
          writes |= bit;
        return true;
      }
      if (IsImmediate(op))
        return true;                           // an immediate touches nothing
      // anything else is a memory reference: a [base+index] form (address regs read, unknown alias)
      // or a bare name (a variable cell -> a distinct key, or an equate we conservatively treat as memory)
      if (op.StartsWith('[')) {
        foreach (var r in ExtractRegisters(op)) {
          if (r < 0)
            return false;
          reads |= (ushort)(1 << r);
        }
        memKey = "?";                          // indexed: aliases everything
      } else {
        if (!IsPlainName(op))
          return false;                        // an unparseable operand -> not schedulable (block left verbatim)
        var key = op.ToUpperInvariant();
        memKey = memKey is null ? key : (memKey == key ? key : "?");
      }
      if (isRead)
        memRead = true;
      if (isWrite)
        memWrite = true;
      return true;
    }

    for (var k = 0; k < operands.Length; ++k) {
      var (r, w) = shape.OperandRw(k);
      if (!ApplyOperand(operands[k], r, w))
        return null;
    }

    return new Instr(original, reads, writes, shape.ReadsFlags, shape.WritesFlags, memRead, memWrite, memKey);
  }

  // mnemonic -> (operand count, per-operand read/write, flag effects). LEA/MOV* read their source
  // operand's value-or-address but never set flags; the ALU family sets flags; CMP/TEST only test.
  private readonly record struct Shape(int Arity, bool ReadsFlags, bool WritesFlags, byte[] Rw) {
    public (bool Read, bool Write) OperandRw(int i) => ((this.Rw[i] & 1) != 0, (this.Rw[i] & 2) != 0);
  }

  private static readonly byte[] _rwWriteRead = [2, 1];   // op0 written, op1 read       (MOV-like)
  private static readonly byte[] _rwRmwRead = [3, 1];     // op0 read+written, op1 read  (ADD-like)
  private static readonly byte[] _rwReadRead = [1, 1];    // op0 read, op1 read          (CMP/TEST)
  private static readonly byte[] _rwRmw1 = [3];           // op0 read+written            (INC/DEC/NEG/NOT)
  private static readonly byte[] _rwXchg = [3, 3];        // both read+written

  private static readonly Dictionary<string, Shape> _modelled = new() {
    ["MOV"] = new(2, false, false, _rwWriteRead),
    ["LEA"] = new(2, false, false, _rwWriteRead),   // op1 is [mem]: ExtractRegisters reads address regs, no MemRead implied below... see note
    ["MOVZX"] = new(2, false, false, _rwWriteRead),
    ["MOVSX"] = new(2, false, false, _rwWriteRead),
    ["ADD"] = new(2, false, true, _rwRmwRead),
    ["SUB"] = new(2, false, true, _rwRmwRead),
    ["AND"] = new(2, false, true, _rwRmwRead),
    ["OR"] = new(2, false, true, _rwRmwRead),
    ["XOR"] = new(2, false, true, _rwRmwRead),
    ["ADC"] = new(2, true, true, _rwRmwRead),
    ["SBB"] = new(2, true, true, _rwRmwRead),
    ["CMP"] = new(2, false, true, _rwReadRead),
    ["TEST"] = new(2, false, true, _rwReadRead),
    ["INC"] = new(1, false, true, _rwRmw1),
    ["DEC"] = new(1, false, true, _rwRmw1),
    ["NEG"] = new(1, false, true, _rwRmw1),
    ["NOT"] = new(1, false, false, _rwRmw1),
    ["XCHG"] = new(2, false, false, _rwXchg),
  };

  // register name -> word slot 0..7, -1 for a non-GP (segment/FPU/SIMD) register, null when not a register
  private static int? RegSlot(string op) {
    var u = op.ToUpperInvariant();
    return u switch {
      "AL" or "AH" or "AX" or "EAX" => 0,
      "CL" or "CH" or "CX" or "ECX" => 1,
      "DL" or "DH" or "DX" or "EDX" => 2,
      "BL" or "BH" or "BX" or "EBX" => 3,
      "SP" or "ESP" => 4,
      "BP" or "EBP" => 5,
      "SI" or "ESI" => 6,
      "DI" or "EDI" => 7,
      "CS" or "DS" or "ES" or "SS" or "FS" or "GS" => -1,
      _ when u.StartsWith("ST") || u.StartsWith("MM") || u.StartsWith("XMM") || u.StartsWith("YMM") || u.StartsWith("ZMM") => -1,
      _ => null,
    };
  }

  private static bool IsByteReg(string op) {
    var u = op.ToUpperInvariant();
    return u is "AL" or "AH" or "CL" or "CH" or "DL" or "DH" or "BL" or "BH";
  }

  private static bool IsImmediate(string op) {
    if (op.Length == 0)
      return false;
    var c = op[0];
    return char.IsAsciiDigit(c) || ((c == '-' || c == '+' || c == '&') && op.Length > 1);
  }

  private static bool IsPlainName(string op)
    => op.Length > 0 && (char.IsAsciiLetter(op[0]) || op[0] == '_')
       && op.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '%' or '$' or '?' or '&' or '#' or '.');

  // register slots referenced inside a [ ... ] memory operand (the address registers it reads)
  private static IEnumerable<int> ExtractRegisters(string bracket) {
    foreach (var tok in bracket.Trim('[', ']', ' ').Split(['+', '-', '*', ' ', '\t'], System.StringSplitOptions.RemoveEmptyEntries))
      if (RegSlot(tok) is { } s)
        yield return s;
  }

  #endregion
}
