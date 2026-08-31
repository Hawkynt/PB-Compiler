namespace PowerBasic.Compiler.CodeGen;

/// <summary>The microarchitecture floor a program is compiled for (the <c>$CPU</c> family).</summary>
/// <remarks>
/// Ordered by generation, so ordinal comparison is meaningful (<c>tier &gt;= CpuTier.I80486</c>).
/// The genuine PBC 3.50 <c>$CPU</c> metastatement selects only 8086/80386/80486/80586; 80286 and the
/// P6 core are modelled so a cost query can reason about the whole span the emitters could target, even
/// where no metastatement names them today.
/// </remarks>
public enum CpuTier {
  I8086 = 0,
  I80286 = 1,
  I80386 = 2,
  I80486 = 3,
  Pentium = 4,   // 80586, in-order U/V pairing
  P6 = 5,        // Pentium Pro / II / III core: out-of-order, move elimination, deep misprediction penalty
}

/// <summary>What the optimizer is being asked to minimise (the <c>$OPTIMIZE SIZE|SPEED</c> objective).</summary>
public enum CostObjective {
  /// <summary>No explicit objective - the neutral default that keeps faithful output.</summary>
  Balanced = 0,
  Size = 1,
  Speed = 2,
}

/// <summary>
/// O0174 - the per-target cost model. An optimization is only an optimization on a particular machine,
/// and the targets this compiler emits for span four decades of contradictory rules (docs/optimizations/O0174):
/// 8-bit sub-register packing wins on an 8086 and stalls a P6; keeping a CMP adjacent to its branch is
/// required on a Pentium and wasted on an 8086. This is the single query interface the profitability-gated
/// passes call instead of hard-coding a threshold - <see cref="PreferBranchless"/>, <see cref="Mul16Cycles"/>,
/// <see cref="SubRegisterPackingProfitable"/> and friends - parameterised by the <see cref="CpuTier"/> floor
/// and the <see cref="CostObjective"/>.
/// </summary>
/// <remarks>
/// The model emits nothing; it only answers questions, so consulting it can never change output on its own -
/// the golden gate is unaffected until a pass acts on an answer, and every such pass is itself
/// <c>Optimize</c>-gated. Cycle figures are representative period numbers for the named core (Intel timing
/// tables): exactness is not the point, the ordering of the trade-offs across tiers is.
/// </remarks>
public sealed class TargetCost {
  public CpuTier Tier { get; }
  public CostObjective Objective { get; }

  public TargetCost(CpuTier tier, CostObjective objective) {
    this.Tier = tier;
    this.Objective = objective;
  }

  /// <summary>True while the machine has no dynamic branch predictor - every conditional branch resolves the
  /// same whether taken or not, so a not-taken branch is nearly free and branchless mask arithmetic buys
  /// nothing but extra bytes. The 8086/286/386 are all statically, cheaply mispredicted; the 486 gains a
  /// tiny BTB, the Pentium a real predictor.</summary>
  public bool NoBranchPredictor => this.Tier <= CpuTier.I80386;

  /// <summary>Cycles thrown away by a mispredicted conditional branch on this core - the payoff a branchless
  /// form has to recover. Zero where there is no predictor (a taken branch just refills the prefetch queue,
  /// already counted as bytes); a few cycles on the in-order Pentium; a deep flush on the out-of-order P6.</summary>
  public int BranchMispredictPenalty => this.Tier switch {
    <= CpuTier.I80386 => 0,
    CpuTier.I80486 => 2,
    CpuTier.Pentium => 4,
    _ => 15,
  };

  /// <summary>True when the front end is bounded by instruction-fetch bandwidth, so fewer instruction bytes
  /// are the dominant win - the 8086/186/286 4- to 6-byte prefetch queue behind a slow bus. From the 386 on,
  /// the wider bus and caches move the bottleneck to execution.</summary>
  public bool PrefetchBound => this.Tier <= CpuTier.I80286;

  /// <summary>Representative latency of a 16-bit <c>MUL</c>/<c>IMUL</c> on this core, in cycles. The early
  /// parts pay dearly (the 8086 microcodes it into three digits), the 386 an order of magnitude less, the P6
  /// a few - which is exactly why replacing a multiply by shift/add (O0078) is a large win on an 8086 and a
  /// wash on a P6.</summary>
  public int Mul16Cycles => this.Tier switch {
    CpuTier.I8086 => 124,
    CpuTier.I80286 => 21,
    CpuTier.I80386 => 12,
    CpuTier.I80486 => 15,
    CpuTier.Pentium => 11,
    _ => 4,
  };

  /// <summary>Representative latency of a 16-bit <c>DIV</c>/<c>IDIV</c> on this core, in cycles. Never cheap,
  /// and the divide-by-constant reciprocal rewrite (O0056) pays back on every tier.</summary>
  public int Div16Cycles => this.Tier switch {
    CpuTier.I8086 => 154,
    CpuTier.I80286 => 22,
    CpuTier.I80386 => 27,
    CpuTier.I80486 => 40,
    CpuTier.Pentium => 25,
    _ => 20,
  };

  /// <summary>Representative cost of a shift/add pair, the currency multiply decomposition trades a
  /// <c>MUL</c> for. One cycle on everything from the 386 up; the 8086's barrel-less shifter makes even this
  /// several cycles, but still an order below its multiply.</summary>
  public int ShiftAddCycles => this.Tier <= CpuTier.I80286 ? 4 : 2;

  /// <summary>True when a constant multiplier with <paramref name="setBits"/> one-bits is cheaper as a
  /// shift/add chain than a hardware <c>IMUL</c> on this tier (O0078). The chain emits roughly a shift and an
  /// add per set bit, so it wins where the multiply is expensive (the 8086's ~124-cycle microcoded <c>MUL</c>)
  /// and loses where the multiply is a handful of cycles (Pentium/P6) - the decomposition should back off to
  /// the compact single <c>IMUL</c> there. A pure power of two (one set bit) is a single shift and always wins;
  /// this prices the multi-term chains.</summary>
  /// <remarks>
  /// The <c>2 *</c> is unexplained and load-bearing, so leave it alone without measurement.
  /// <see cref="ShiftAddCycles"/> is already a shift/add PAIR and the summary above prices the chain
  /// at one pair per set bit, which makes the natural form <c>setBits * ShiftAddCycles</c>; the
  /// factor doubles that. It is what keeps the four-bit chain 8086-only (its shipped behaviour), and
  /// it is also why this query would decline the three-bit chain on the 286/386/Pentium - which is
  /// why O0078's two- and three-bit forms are NOT wired through here, despite that being recorded as
  /// a mechanical follow-up. See docs/optimizations/O0078 for the table.
  /// </remarks>
  public bool PreferShiftAddMultiply(int setBits) =>
    setBits >= 1 && 2 * setBits * this.ShiftAddCycles < this.Mul16Cycles;

  /// <summary>True when writing an 8-bit sub-register (packing two BYTE locals into <c>AL</c>/<c>AH</c>,
  /// O0058) is a net win: on the byte-starved early parts it saves real bytes and a full-width move, but on a
  /// P6 the partial-register write introduces a false dependency / merge stall that costs more than it saves.</summary>
  public bool SubRegisterPackingProfitable => this.Tier < CpuTier.P6;

  /// <summary>True when the <c>CMP</c> that feeds a conditional branch should be kept physically adjacent to
  /// it (macro-fusion / avoiding an AGI-style stall, O0109): required from the Pentium on, and counterproductive
  /// on an 8086 where the slot is better filled with independent work while the bus catches up.</summary>
  public bool MacroFusionMatters => this.Tier >= CpuTier.Pentium;

  /// <summary>True when a hot loop's top is worth NOP-padding to a 16-byte instruction-fetch boundary (O0104
  /// block placement / C2): a speed objective on a core with a cache line to align to. The early parts fetch
  /// too little ahead to benefit, and a size objective never pays bytes for it.</summary>
  public bool AlignHotLoops => this.Objective == CostObjective.Speed && this.Tier >= CpuTier.I80486;

  /// <summary>True when the one-byte <c>LOOP</c> instruction is preferable to the <c>DEC CX</c>/<c>JNZ</c>
  /// pair. It is smaller everywhere, but from the 486 on it is microcoded and slower than the two simple ops,
  /// so only a size objective (or a fetch-bound early part) should still choose it there.</summary>
  public bool PreferLoopInstruction => this.Tier < CpuTier.I80486 || this.Objective == CostObjective.Size || this.PrefetchBound;

  /// <summary>
  /// The central profitability question for O0094/O0108/O0248: should a short data-dependent branch whose
  /// arms are <paramref name="armInstrBytes"/> bytes of straight-line, side-effect-free code be replaced by
  /// branchless mask/`CMOV` arithmetic? Only when a real predictor exists to be defeated (so the saved
  /// misprediction pays for the always-executed second arm) and the arms are small enough that executing both
  /// is cheaper than a flush. A size objective always keeps the branch (branchless is never smaller here).
  /// </summary>
  public bool PreferBranchless(int armInstrBytes, bool sideEffectFree) {
    if (!sideEffectFree || this.Objective == CostObjective.Size)
      return false;
    if (this.NoBranchPredictor)
      return false; // a not-taken branch is nearly free; mask arithmetic is pure overhead
    // Both arms execute, so the mask form is worth it only when doubling the arm work stays under the
    // misprediction it removes. Roughly one cycle per instruction byte executed on the always-run arm.
    return armInstrBytes <= this.BranchMispredictPenalty;
  }

  /// <summary>The largest constant trip count a tiny FOR loop is worth *fully* unrolling (O7/O0129). A
  /// fetch-bound early part (≤386) keeps the conservative four copies - more instruction bytes throttle the
  /// prefetch queue that is its bottleneck; a 486 or later, with a real instruction cache, tolerates a wider
  /// unroll that deletes more of the per-iteration compare/branch. Only consulted under a speed objective (the
  /// unroller is speed-gated), so it is never a size regression.</summary>
  public int MaxFullUnrollTrips => this.Tier >= CpuTier.I80486 ? 8 : 4;

  /// <summary>
  /// The loop-unroll factor O0129 should apply to a body of <paramref name="bodyInstrBytes"/> bytes with the
  /// given <paramref name="tripCount"/> (0 = unknown at compile time). A size objective never unrolls; a
  /// fetch-bound early part unrolls at most 2x (more bytes throttle the prefetch queue that is the bottleneck);
  /// out-of-order cores tolerate wider unrolling to expose parallelism, but never past a small byte budget or
  /// a known short trip count.
  /// </summary>
  public int UnrollFactor(int tripCount, int bodyInstrBytes) {
    if (this.Objective == CostObjective.Size || bodyInstrBytes <= 0)
      return 1;
    if (tripCount is > 0 and < 4)
      return 1; // too few iterations to amortise the tail
    var byTier = this.Tier switch {
      <= CpuTier.I80286 => 2,   // fetch-bound: a little unrolling, then bytes hurt
      <= CpuTier.I80486 => 4,
      _ => 8,                    // superscalar: expose more independent work
    };
    // Keep the unrolled body inside a reasonable instruction-fetch window.
    while (byTier > 1 && (long)byTier * bodyInstrBytes > 128)
      byTier /= 2;
    if (tripCount > 0)
      while (byTier > 1 && tripCount % byTier != 0)
        --byTier; // prefer a factor that divides the trip count so no remainder tail is needed
    return byTier < 1 ? 1 : byTier;
  }

  /// <summary>The cost model implied by a <c>$CPU</c> floor and an <c>$OPTIMIZE</c> objective. The four
  /// metastatement CPU spellings map to their tier; anything else is the 8086 default.</summary>
  public static TargetCost For(int cpuLevel, bool optimizeSpeed, bool optimizeSize) {
    var tier = cpuLevel switch {
      >= 686 => CpuTier.P6,
      >= 586 => CpuTier.Pentium,
      >= 486 => CpuTier.I80486,
      >= 386 => CpuTier.I80386,
      >= 286 => CpuTier.I80286,
      _ => CpuTier.I8086,
    };
    var objective =
      optimizeSize ? CostObjective.Size :
      optimizeSpeed ? CostObjective.Speed :
      CostObjective.Balanced;
    return new TargetCost(tier, objective);
  }
}
