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

    CancelSaveRestore(blocks, blockOf, start, stop, facts);
    Backwards(blocks, blockOf, start, stop, facts, out var afterPrecise, out var afterInferred);
    Forwards(blocks, blockOf, start, stop, facts, out var before);

    var map = new Dictionary<int, IReadOnlyList<Reg>>();
    for (var i = 0; i < total; ++i) {
      if (!facts[i].IsAsm)
        foreach (var live in afterPrecise[i])
          if (Destroyed(facts[i].Destroys, live) && Reaches(before[i], live))
            conflict ??= Conflict(live);

      var held = new HashSet<Reg>(afterPrecise[i]);
      held.UnionWith(afterInferred[i]);
      held.RemoveWhere(value => !Reaches(before[i], value));
      held.Remove(_flagsPseudoRegister);
      if (held.Count > 0)
        // the reservation is refused to an INTERVAL, and intervals are the word registers the
        // allocator hands out - a half held is the whole word withheld
        map[i] = [.. held.Select(AsmRegisterEffect.WordOf).Distinct()];
    }
    return map;
  }

  /// <summary>Whether anything in <paramref name="writes"/> touches a byte of <paramref name="value"/>.</summary>
  private static bool Destroyed(HashSet<Reg> writes, Reg value) {
    foreach (var write in writes)
      if (AsmRegisterEffect.Overlaps(write, value))
        return true;
    return false;
  }

  /// <summary>Whether some asm definition reaching here put anything into <paramref name="value"/>.</summary>
  private static bool Reaches(HashSet<Reg> definitions, Reg value) => Destroyed(definitions, value);

  /// <summary>
  /// Takes out of <paramref name="live"/> what <paramref name="writes"/> supplies, which is a
  /// NARROWING rather than a removal.
  ///
  /// <para>
  /// A claim on <c>AX</c> met by a write of <c>AL</c> is not ended and is not untouched either: what
  /// remains wanted is <c>AH</c>, and saying so is what lets the two halves satisfy a word between
  /// them. Written as <c>ExceptWith</c> the write did nothing, and <c>! MOV AL, 4</c> + <c>! MOV AH,
  /// 0</c> + <c>! MOV v, AX</c> still reported a promise stretching back past the last BASIC statement
  /// - the same false conflict the canonicalization used to produce, one step further along.
  /// </para>
  /// </summary>
  private static void RemoveCovered(HashSet<Reg> live, HashSet<Reg> writes) {
    if (writes.Count == 0)
      return;

    foreach (var write in writes) {
      live.RemoveWhere(value => AsmRegisterEffect.Covers(write, value));
      if (!write.IsByte())
        continue;
      // the other half of the word is still wanted, and is now the whole of what is
      if (live.Remove(AsmRegisterEffect.WordOf(write)))
        live.Add(OtherHalf(write));
    }
  }

  /// <summary>The byte half sharing a word with this one - <c>AL</c> answers <c>AH</c>.</summary>
  private static Reg OtherHalf(Reg half) => (Reg)((int)half ^ 0x04);

  /// <summary>
  /// Cancels the promise a matched <c>! PUSH r</c> / <c>! POP r</c> pair only APPEARS to make.
  ///
  /// <para>
  /// Read instruction by instruction, the push USES <c>r</c> and the pop DEFINES it, so the analysis
  /// concluded that some earlier <c>!</c> statement had put a value there for the push to consume and
  /// that the pop had left one for a later statement. Neither is true of the idiom. The pair hands the
  /// register back exactly as it found it, which is the whole reason a body writes one: it is how an
  /// <c>!</c> block borrows a register the COMPILER is using without disturbing it.
  /// </para>
  /// <para>
  /// Taking the instructions literally is what declined <c>Vga_PatternFill</c> in the SVGA corpus, and
  /// the shape is worth keeping in mind because nothing about it looks like an asm promise. Its
  /// <c>! PUSH DI … ! POP DI</c> sits inside a loop whose BASIC half calls <c>ASC(MID$(…))</c>; the
  /// pop's "definition" reached round the back edge to the next iteration's push, the runtime call in
  /// between destroys <c>DI</c>, and a promise nobody made was reported broken.
  /// </para>
  /// <para>
  /// Cancelling BOTH halves - not just the push - is what makes the model right rather than merely
  /// quieter. A pair is TRANSPARENT: a register set before the push and read after the pop really does
  /// survive, because the pop puts it back, and with both halves gone the analysis carries that promise
  /// straight through the pair instead of stopping at either end of it.
  /// </para>
  /// <para>
  /// Pairing is by stack DEPTH, which is why <see cref="AsmRegisterEffect.StackDelta"/> exists: a push
  /// and a pop naming the same register are not a pair unless nothing between them left the stack
  /// somewhere else. A statement the assembler could not read moves the stack by an unknown amount and
  /// abandons every pending save, and a machine instruction between the two ends the run outright -
  /// not because it unbalances the stack, but because the depth argument is a claim about ONE run of
  /// hand-written assembly and stops being one as soon as the compiler's own code is in the middle.
  /// </para>
  /// <para>
  /// The run may still span several BLOCKS, and refusing to pair across them left most of the class
  /// open: a <c>! PUSH DI</c> whose <c>! POP DI</c> is eight statements later, with
  /// <c>V800HLAligned:</c> and a <c>! JZ</c> between them, is one run written with its own control
  /// flow - it is what every <c>Vesa*_HLine</c> in the corpus looks like. What the depth argument
  /// actually needs is that the region be CLOSED, and <see cref="IsClosedRegion"/> asks exactly that:
  /// nothing jumps INTO the span except at its head, and nothing jumps OUT of it except past its end.
  /// A region like that has no path that reaches the pop other than through the push, so what the
  /// linear scan counted is what every execution counts.
  /// </para>
  /// <para>
  /// Which leaves one piece of the compiler's own code that may sit inside the run after all: the
  /// BLOCK BOUNDARY. A label in the middle of an asm run ends a machine block, and the block ends with
  /// a <c>JMP</c> to the next one - the compiler's instruction, in the middle of the run, ending it.
  /// Every <c>Vesa*_HLine</c> in the corpus is shaped that way and every one of them declined for it,
  /// thirty-three times over, with an unpaired <c>! PUSH DI</c> reported as a promise a later
  /// <c>CALL</c> destroys. A branch moves no data and leaves <c>SP</c> exactly where it found it, so
  /// the depth argument survives it untouched; where the branch GOES is the closed-region question,
  /// already asked and answered above. Only an unconditional or conditional jump qualifies - a
  /// computed one goes somewhere <see cref="IsClosedRegion"/> cannot see, and a <c>CALL</c> pushes.
  /// </para>
  /// </summary>
  private static void CancelSaveRestore(List<MBlock> blocks, Dictionary<string, int> blockOf,
      int[] start, int[] stop, InstructionFacts[] facts) {
    var saved = new Stack<(int Index, Reg? Register)>();
    for (var i = 0; i < facts.Length; ++i) {
      var fact = facts[i];
      if (!fact.IsAsm) {
        if (!fact.IsPlainBranch)
          saved.Clear();                            // the compiler's own code ends the run
        continue;
      }

      switch (fact.StackDelta) {
        case null:
          saved.Clear();
          break;
        case > 0:
          saved.Push((i, fact.Saves));
          break;
        case < 0 when saved.Count > 0: {
          var (push, register) = saved.Pop();
          if (register is not { } r || fact.Restores != r)
            break;                                  // a pop of something else: the depth matched, the register did not
          if (!IsClosedRegion(blocks, blockOf, start, stop, facts, push, i))
            break;

          facts[push].Uses.Remove(r);
          facts[push].InferredUses.Remove(r);
          facts[i].Defines.Remove(r);
          facts[i].Kills.Remove(r);
          break;
        }
      }
    }
  }

  /// <summary>
  /// Whether every path through <c>[from, to]</c> enters at <paramref name="from"/> and leaves past
  /// <paramref name="to"/> - the property that makes a linear count of pushes and pops true of every
  /// execution and not merely of the text.
  ///
  /// <para>
  /// Two ways it can fail, and both are real shapes rather than defensive padding. A label inside the
  /// span that something OUTSIDE jumps to is a second entry, and an execution arriving there never
  /// pushed. A jump out of the span to somewhere before it or after it is an exit that skips the pop.
  /// Either one means some path sees a different depth than the scan did, and the pair it thought it
  /// had matched is a pop of somebody else's word.
  /// </para>
  /// </summary>
  private static bool IsClosedRegion(List<MBlock> blocks, Dictionary<string, int> blockOf,
      int[] start, int[] stop, InstructionFacts[] facts, int from, int to) {
    for (var b = 0; b < blocks.Count; ++b) {
      var overlaps = stop[b] > from && start[b] <= to;
      var contained = start[b] > from && stop[b] <= to + 1;

      foreach (var successor in blocks[b].Successors) {
        if (!blockOf.TryGetValue(successor, out var s))
          return false;                             // a successor this analysis cannot place
        if (start[s] > from && start[s] <= to && !overlaps)
          return false;                             // an entry into the middle of the span
        if (contained && !(start[s] > from && start[s] <= to + 1))
          return false;                             // an exit that skips the pop
      }

      for (var i = Math.Max(start[b], from); i < Math.Min(stop[b], to + 1); ++i)
        foreach (var target in facts[i].JumpsTo) {
          if (!blockOf.TryGetValue(target, out var t))
            return false;
          if (!(start[t] > from && start[t] <= to + 1))
            return false;                           // an asm jump out of the span
        }
    }

    // an asm jump from OUTSIDE the span into the middle of it is the same second entry as above
    for (var i = 0; i < facts.Length; ++i) {
      if (i >= from && i <= to)
        continue;
      foreach (var target in facts[i].JumpsTo)
        if (blockOf.TryGetValue(target, out var t) && start[t] > from && start[t] <= to)
          return false;
    }
    return true;
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
            RemoveCovered(precise, fact.Kills);
            precise.UnionWith(fact.Uses);
            RemoveCovered(inferred, fact.Kills);
            inferred.UnionWith(fact.InferredUses);
            continue;
          }
          // a destroyer ends an INFERRED promise (see the class comment) and never a precise one: the
          // precise case has to reach the conflict check, which is the whole point of keeping it alive
          RemoveCovered(inferred, fact.Destroys);
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
            RemoveCovered(reaching, fact.Destroys);
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

    /// <summary>
    /// A compiler-emitted branch to a label of this function - the BLOCK BOUNDARY rather than anything
    /// in the middle of the run. See <see cref="CancelSaveRestore"/>: it is the one piece of the
    /// compiler's own code that may sit between a save and its restore.
    /// </summary>
    public bool IsPlainBranch { get; init; }

    /// <summary>The save/restore half, read straight off the effect - see <see cref="CancelSaveRestore"/>.</summary>
    public Reg? Saves { get; init; }

    public Reg? Restores { get; init; }

    public int? StackDelta { get; init; }

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
        var facts = effect.IsOpaque
          ? new InstructionFacts(true, [], reads, defines, kills, [], targets ?? [])
          : new InstructionFacts(true, reads, [], defines, kills, [], targets ?? []);
        return facts with {
          Saves = effect.Saves, Restores = effect.Restores, StackDelta = effect.StackDelta,
        };
      }

      var destroys = new HashSet<Reg>(PhysicalWrites(instr));
      if (instr.Effect.WritesFlags)
        destroys.Add(_flagsPseudoRegister);
      return new InstructionFacts(false, [], [], [], [], destroys, []) {
        IsPlainBranch = instr.Opcode is MOpcode.Jmp or MOpcode.Jcc,
      };
    }
  }
}
