using PowerBasic.Compiler.Asm;

namespace PowerBasic.Compiler.Backend;

/// <summary>
/// The half of allocation that belongs to somebody else's registers: the ones an inline-assembly
/// statement puts somewhere for a LATER statement to read.
/// </summary>
public sealed partial class LinearScanAllocator {

  /// <summary>
  /// The FLAGS, carried by this dataflow exactly like a register. <c>! DEC CX</c> sets them and
  /// <c>! JNZ AddLoop</c> reads them, which is the same promise between two statements that <c>CX</c>
  /// itself is - but nothing can be ALLOCATED to them, so this value never leaves for the reservation
  /// map. It only ever produces the conflict that declines the function.
  /// </summary>
  private const Reg _flagsPseudoRegister = (Reg)0xFF;

  private static readonly IReadOnlyDictionary<int, IReadOnlyList<Reg>> _noReservations =
    new Dictionary<int, IReadOnlyList<Reg>>();

  /// <summary>
  /// Maps each global instruction index to the physical registers an inline-assembly statement is
  /// holding there for a later one, and reports the first place something destroys one.
  ///
  /// <para>
  /// This is <see cref="InFlightByIndex"/>'s idea over the whole control-flow graph, and it needs to be
  /// its own analysis for one reason: an asm block's clobber list is conservative. <c>InFlight</c> may
  /// read a clobber as "the old value ends here", because a <c>CALL</c> really does destroy what it
  /// declares; an asm block declares the whole file and writes almost none of it, so reading its
  /// clobbers as kills would end a promise the text is still keeping. The producer and consumer sets
  /// therefore come from the TEXT (<see cref="AsmRegisterEffect"/>, read by the assembler at
  /// selection), and the window between them is reserved the same way: refused to every interval that
  /// overlaps it.
  /// </para>
  ///
  /// <para>
  /// Something in between that destroys the register outright - a <c>CALL</c>, an ABI-pinned write - is
  /// not a reservation problem, because no allocation can answer it. It DECLINES, and the function goes
  /// to the direct emitter whole. The one exception is a read the analysis only INFERRED, from a
  /// statement it could not understand (<c>INT</c>, <c>CALL</c>, an unlisted mnemonic): that is the
  /// absence of information rather than evidence the text wanted the register, and declining on it
  /// would buy nothing - the direct emitter's own <c>PRINT</c> destroys the caller-saved file too. Such
  /// a read is therefore cut at the destroyer and protected only from there on.
  /// </para>
  ///
  /// <para>
  /// Segment registers are not carried at all. Neither path promises one survives a BASIC statement:
  /// both reload <c>ES</c> immediately in front of a far access, which is where the value would go.
  /// </para>
  /// </summary>
  private static IReadOnlyDictionary<int, IReadOnlyList<Reg>> AsmHeldByIndex(MFunction function, out string? conflict) {
    conflict = null;
    var blocks = function.Blocks;
    var total = 0;
    var hasAsm = false;
    foreach (var block in blocks)
      foreach (var instr in block.Instructions) {
        ++total;
        hasAsm |= instr.Opcode == MOpcode.InlineAsm;
      }
    if (!hasAsm)
      return _noReservations;

    var n = blocks.Count;
    var start = new int[n];
    var stop = new int[n];
    var blockOf = new Dictionary<string, int>(StringComparer.Ordinal);
    var next = 0;
    for (var b = 0; b < n; ++b) {
      blockOf[blocks[b].Label] = b;
      start[b] = next;
      next += blocks[b].Instructions.Count;
      stop[b] = next;
    }

    var facts = new InstructionFacts[total];
    next = 0;
    foreach (var block in blocks)
      foreach (var instr in block.Instructions)
        facts[next++] = InstructionFacts.Of(instr);

    Backwards(blocks, blockOf, start, stop, facts, out var afterPrecise, out var afterInferred);
    Forwards(blocks, blockOf, start, stop, facts, out var before);

    var map = new Dictionary<int, IReadOnlyList<Reg>>();
    for (var i = 0; i < total; ++i) {
      if (!facts[i].IsAsm)
        foreach (var register in facts[i].Destroys)
          if (afterPrecise[i].Contains(register) && before[i].Contains(register))
            conflict ??= Conflict(register);

      var held = new HashSet<Reg>(afterPrecise[i]);
      held.UnionWith(afterInferred[i]);
      held.IntersectWith(before[i]);
      held.Remove(_flagsPseudoRegister);
      if (held.Count > 0)
        map[i] = [.. held];
    }
    return map;
  }

  private static string Conflict(Reg register) => register == _flagsPseudoRegister
    ? "inline asm: the flags one ! statement sets are read by a later one, and something between them writes flags"
    : $"inline asm: {register} is set by one ! statement and read by a later one, and an instruction between them destroys it";

  /// <summary>
  /// Backward liveness of the two kinds of asm read, to a fixpoint, over the graph INCLUDING the edges
  /// an asm jump makes - a countdown's last read is on the way round a loop no other edge draws. It
  /// yields each instruction's live-AFTER sets, the "somebody later still wants this" half of a
  /// reservation window.
  ///
  /// <para>
  /// An asm jump leaves from its own INSTRUCTION and not from the end of its block, and modelling it
  /// as a block edge is not merely imprecise - it is wrong in the shape this exists for. LOWLEVEL.BAS
  /// puts <c>AddLoop:</c> in front of the whole rest of the program, so the block is its own successor;
  /// read as a block edge, <c>CX</c> came out live at every instruction after the loop as well, and the
  /// first <c>PRINT</c> past it declined the function for destroying a register nothing wanted.
  /// </para>
  /// </summary>
  private static void Backwards(List<MBlock> blocks, Dictionary<string, int> blockOf,
      int[] start, int[] stop, InstructionFacts[] facts,
      out HashSet<Reg>[] afterPrecise, out HashSet<Reg>[] afterInferred) {
    var n = blocks.Count;
    var inPrecise = new HashSet<Reg>[n];
    var inInferred = new HashSet<Reg>[n];
    for (var b = 0; b < n; ++b) {
      inPrecise[b] = [];
      inInferred[b] = [];
    }

    afterPrecise = new HashSet<Reg>[facts.Length];
    afterInferred = new HashSet<Reg>[facts.Length];
    for (var changed = true; changed;) {
      changed = false;
      for (var b = n - 1; b >= 0; --b) {
        var precise = new HashSet<Reg>();
        var inferred = new HashSet<Reg>();
        foreach (var successor in blocks[b].Successors)
          if (blockOf.TryGetValue(successor, out var s)) {
            precise.UnionWith(inPrecise[s]);
            inferred.UnionWith(inInferred[s]);
          }

        for (var i = stop[b] - 1; i >= start[b]; --i) {
          var fact = facts[i];
          foreach (var target in fact.JumpsTo)          // the branch's other successor
            if (blockOf.TryGetValue(target, out var t)) {
              precise.UnionWith(inPrecise[t]);
              inferred.UnionWith(inInferred[t]);
            }

          afterPrecise[i] = [.. precise];
          afterInferred[i] = [.. inferred];
          if (fact.IsAsm) {
            precise.ExceptWith(fact.Kills);
            precise.UnionWith(fact.Uses);
            inferred.ExceptWith(fact.Kills);
            inferred.UnionWith(fact.InferredUses);
            continue;
          }
          // a destroyer ends an INFERRED promise (see the class comment) and never a precise one: the
          // precise case has to reach the conflict check, which is the whole point of keeping it alive
          inferred.ExceptWith(fact.Destroys);
        }

        if (precise.SetEquals(inPrecise[b]) && inferred.SetEquals(inInferred[b]))
          continue;

        inPrecise[b] = precise;
        inInferred[b] = inferred;
        changed = true;
      }
    }
  }

  /// <summary>
  /// Which asm definitions REACH each instruction, forward to a fixpoint over the same graph. A read
  /// with no definition reaching it is a register nobody here put anything in, and reserving one would
  /// only cost somebody else a register: this is the intersection that bounds the window at both ends.
  /// A destroyer ends a definition's reach - after a <c>CALL</c> there is nothing left to preserve, and
  /// whether that mattered was already decided by the conflict check at the call itself.
  /// </summary>
  private static void Forwards(List<MBlock> blocks, Dictionary<string, int> blockOf,
      int[] start, int[] stop, InstructionFacts[] facts, out HashSet<Reg>[] before) {
    var n = blocks.Count;
    var predecessors = new List<int>[n];
    var reachOut = new HashSet<Reg>[n];
    var jumpedIn = new HashSet<Reg>[n];               // what an asm jump brings in, edge by edge
    for (var b = 0; b < n; ++b) {
      predecessors[b] = [];
      reachOut[b] = [];
      jumpedIn[b] = [];
    }
    for (var b = 0; b < n; ++b)
      foreach (var successor in blocks[b].Successors)
        if (blockOf.TryGetValue(successor, out var s))
          predecessors[s].Add(b);

    before = new HashSet<Reg>[facts.Length];
    for (var changed = true; changed;) {
      changed = false;
      for (var b = 0; b < n; ++b) {
        var reaching = new HashSet<Reg>(jumpedIn[b]);
        foreach (var predecessor in predecessors[b])
          reaching.UnionWith(reachOut[predecessor]);

        for (var i = start[b]; i < stop[b]; ++i) {
          before[i] = [.. reaching];
          var fact = facts[i];
          if (fact.IsAsm)
            reaching.UnionWith(fact.Defines);
          else
            reaching.ExceptWith(fact.Destroys);
          foreach (var target in fact.JumpsTo)
            if (blockOf.TryGetValue(target, out var t) && Grow(jumpedIn[t], reaching))
              changed = true;
        }

        if (reaching.SetEquals(reachOut[b]))
          continue;

        reachOut[b] = reaching;
        changed = true;
      }
    }
  }

  /// <summary>Unions <paramref name="values"/> into <paramref name="set"/>, reporting whether it grew.</summary>
  private static bool Grow(HashSet<Reg> set, HashSet<Reg> values) {
    var size = set.Count;
    set.UnionWith(values);
    return set.Count != size;
  }

  /// <summary>
  /// One instruction's part in the flow: what an asm statement reads, defines, certainly overwrites
  /// and jumps to, or - for everything else - which registers it destroys outright.
  /// </summary>
  private readonly record struct InstructionFacts(bool IsAsm, HashSet<Reg> Uses, HashSet<Reg> InferredUses,
      HashSet<Reg> Defines, HashSet<Reg> Kills, HashSet<Reg> Destroys, IReadOnlyList<string> JumpsTo) {

    public static InstructionFacts Of(MInstr instr) {
      if (instr.Opcode == MOpcode.InlineAsm && instr.Operands.Count > 0
          && instr.Operands[0] is MOperand.InlineAsmText descriptor) {
        var effect = descriptor.Effect;
        var reads = new HashSet<Reg>(effect.Reads);
        if (effect.ReadsFlags)
          reads.Add(_flagsPseudoRegister);
        var defines = new HashSet<Reg>(effect.Defines);
        var kills = new HashSet<Reg>(effect.Kills);
        if (effect.WritesFlags) {
          defines.Add(_flagsPseudoRegister);
          kills.Add(_flagsPseudoRegister);
        }
        List<string>? targets = null;
        foreach (var operand in instr.Operands)
          if (operand is MOperand.BlockOffset target)
            (targets ??= []).Add(target.Block);
        return effect.IsOpaque
          ? new(true, [], reads, defines, kills, [], targets ?? [])
          : new(true, reads, [], defines, kills, [], targets ?? []);
      }

      var destroys = new HashSet<Reg>(PhysicalWrites(instr));
      if (instr.Effect.WritesFlags)
        destroys.Add(_flagsPseudoRegister);
      return new(false, [], [], [], [], destroys, []);
    }
  }
}
