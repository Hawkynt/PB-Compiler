' ===========================================================================
' Optimization battery - code generation (O1..O25 outside the range lattice)
' ===========================================================================
' Same contract as RANGES.BAS: one NOINLINE SUB per scenario, its header says
' what the optimizer should do, OptimizationBatteryTests checks the emitted
' bytes, and the whole file also runs (output must match CODEGEN.expected with
' the optimizer both on and off).
' ===========================================================================
$OPTIMIZE SPEED

DECLARE SUB ConstantExpressionFolded()
DECLARE SUB DeadStoreRemoved(BYVAL n%)
DECLARE SUB UnreachableCodeDropped(BYVAL n%)
DECLARE SUB CommonSubexpressionReused(BYVAL y%, BYVAL x%)
DECLARE SUB MultiplyByPowerOfTwoIsShifts(BYVAL n%)
DECLARE SUB MultiplyByFourBitConstIsShifts(BYVAL n%)
DECLARE SUB IntegerMaxFoldsWithoutFpu(BYVAL a%, BYVAL b%)
DECLARE SUB MinMaxDiamondFolds(BYVAL a%, BYVAL b%)
DECLARE SUB DivideByPowerOfTwoIsShifts(BYVAL n%)
DECLARE SUB DivideByConstantIsReciprocal(BYVAL n%)
DECLARE SUB TrivialFunctionInlined(BYVAL n%)
DECLARE SUB PureCallFoldedAtCompileTime()
DECLARE SUB ZeroUsesXorIdiom()
DECLARE SUB LoopInvariantHoisted(BYVAL k%)
DECLARE SUB SelfTailCallIsAJump(BYVAL n%, BYVAL acc%)
DECLARE SUB DenseSelectIsJumpTable(BYVAL n%)
DECLARE SUB FrameZeroingElided(BYVAL n%)
DECLARE SUB SmallLoopUnrolled()
DECLARE SUB ConstantArgumentPropagated(BYVAL n%)
DECLARE SUB StringChainSingleAllocation(BYVAL n%)
DECLARE SUB CopyPropagated(BYVAL n%)
DECLARE SUB TwoDimensionalIndexStrengthReduced()
DECLARE SUB LongArithmeticStaysOffTheFpu(BYVAL n%)
DECLARE SUB AccumulateOverArrayIsHandQuality(BYVAL n%)
DECLARE SUB ComparisonBranchesOnItsOwnFlags(BYVAL n%)
DECLARE SUB ConstantStoredAsImmediate(BYVAL n%)
DECLARE SUB MaxScanReadsEachElementOnce()
DECLARE SUB BoundedMultiplyStaysOffTheFpu(BYVAL n%)
DECLARE SUB HotAccumulatorWinsTheRegister(BYVAL n%)
DECLARE SUB AndOrConditionShortCircuits(BYVAL n%)
DECLARE SUB ParityTestIsAMask(BYVAL n%)
DECLARE SUB Callee(BYVAL fixed%, BYVAL varying%)
DECLARE FUNCTION Doubled%(BYVAL v%)
DECLARE FUNCTION PureSquare%(BYVAL v%)

ConstantExpressionFolded
DeadStoreRemoved 3
UnreachableCodeDropped 4
CommonSubexpressionReused 5, 6
MultiplyByPowerOfTwoIsShifts 7
MultiplyByFourBitConstIsShifts 3
IntegerMaxFoldsWithoutFpu 3, 8
MinMaxDiamondFolds 3, 8
DivideByPowerOfTwoIsShifts 64
DivideByConstantIsReciprocal 250
TrivialFunctionInlined 8
PureCallFoldedAtCompileTime
ZeroUsesXorIdiom
LoopInvariantHoisted 9
SelfTailCallIsAJump 5, 0
DenseSelectIsJumpTable 3
FrameZeroingElided 11
SmallLoopUnrolled
ConstantArgumentPropagated 2
StringChainSingleAllocation 4
CopyPropagated 12
TwoDimensionalIndexStrengthReduced
LongArithmeticStaysOffTheFpu 4
AccumulateOverArrayIsHandQuality 3
ComparisonBranchesOnItsOwnFlags 9
ConstantStoredAsImmediate 4
MaxScanReadsEachElementOnce
BoundedMultiplyStaysOffTheFpu 9
HotAccumulatorWinsTheRegister 7
AndOrConditionShortCircuits 42
ParityTestIsAMask -7
END

' ---------------------------------------------------------------------------
' @scenario ConstantExpressionFolded
' @what     an expression over literals only
' @expect   O1 folds the whole thing at compile time - no arithmetic survives
' @assert   absent imul-bx
' @assert   absent-call rt_raise
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB ConstantExpressionFolded() NOINLINE
  x% = 2 * 3 + 4 * 5
  PRINT "const"; x%
END SUB

' ---------------------------------------------------------------------------
' @scenario DeadStoreRemoved
' @what     a local assigned twice with no read in between
' @expect   O2/SSA dead-store elimination drops the first store
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB DeadStoreRemoved(BYVAL n%) NOINLINE
  t% = n% + 111
  t% = n% + 222
  PRINT "deadstore"; t%
END SUB

' ---------------------------------------------------------------------------
' @scenario UnreachableCodeDropped
' @what     statements after an unconditional EXIT SUB
' @expect   the pruner never emits them
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB UnreachableCodeDropped(BYVAL n%) NOINLINE
  PRINT "unreach"; n%
  EXIT SUB
  PRINT "never"; n% * 3 + 7
  PRINT "never either"; n% * 5 + 9
END SUB

' ---------------------------------------------------------------------------
' @scenario CommonSubexpressionReused
' @what     y%*320+x% computed twice in a row
' @expect   O3 computes it once and reloads the slot
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB CommonSubexpressionReused(BYVAL y%, BYVAL x%) NOINLINE
  a% = y% * 320 + x%
  b% = y% * 320 + x%
  PRINT "cse"; a% + b%
END SUB

' ---------------------------------------------------------------------------
' @scenario MultiplyByPowerOfTwoIsShifts
' @what     n% * 8
' @expect   O4 lowers it to shifts - the 8086 IMUL is ~128 cycles
' @assert   absent imul-bx
' @status   done
' ---------------------------------------------------------------------------
SUB MultiplyByPowerOfTwoIsShifts(BYVAL n%) NOINLINE
  PRINT "mulshift"; n% * 8
END SUB

' ---------------------------------------------------------------------------
' @scenario MultiplyByFourBitConstIsShifts
' @what     n% * 85 - a four-set-bit multiplier (85 = 1010101b)
' @expect   O0078 threads x<<k through BX for one (shift, add) per term; the
'           O0174 cost model fires it only where the multiply is slow (the
'           default 8086 tier here), keeping the compact IMUL on a Pentium
' @assert   absent imul-bx
' @status   done
' ---------------------------------------------------------------------------
SUB MultiplyByFourBitConstIsShifts(BYVAL n%) NOINLINE
  PRINT "mul85"; n% * 85
END SUB

' ---------------------------------------------------------------------------
' @scenario IntegerMaxFoldsWithoutFpu
' @what     MAX% over two INTEGER arguments
' @expect   O0108/O0248 fold it with a signed integer compare - no x87 round-trip
' @assert   absent fild-dword
' @assert   present cmp-ax-bx
' @status   done
' ---------------------------------------------------------------------------
SUB IntegerMaxFoldsWithoutFpu(BYVAL a%, BYVAL b%) NOINLINE
  PRINT "imax"; MAX%(a%, b%)
  PRINT "imin"; MIN%(-5, a%)
  PRINT "itie"; MAX%(a%, a%)
END SUB

' ---------------------------------------------------------------------------
' @scenario MinMaxDiamondFolds
' @what     IF a% > b% THEN m% = a% ELSE m% = b% - the hand-written max diamond
' @expect   O0248 folds it to the integer CMP/keep the MAX% intrinsic emits
' @assert   present cmp-ax-bx
' @status   done
' ---------------------------------------------------------------------------
SUB MinMaxDiamondFolds(BYVAL a%, BYVAL b%) NOINLINE
  DIM m%
  IF a% > b% THEN m% = a% ELSE m% = b%
  PRINT "dmax"; m%
  IF a% < b% THEN m% = a% ELSE m% = b%
  PRINT "dmin"; m%
  IF a% >= -9 THEN m% = a% ELSE m% = -9
  PRINT "dclamp"; m%
END SUB

' ---------------------------------------------------------------------------
' @scenario DivideByPowerOfTwoIsShifts
' @what     n% \ 8 with PB's truncate-toward-zero fix-up
' @expect   O4 lowers it to a bias + arithmetic shift, no divide instruction
' @assert   absent idiv-bx
' @status   done
' ---------------------------------------------------------------------------
SUB DivideByPowerOfTwoIsShifts(BYVAL n%) NOINLINE
  PRINT "divshift"; n% \ 8
END SUB

' ---------------------------------------------------------------------------
' @scenario DivideByConstantIsReciprocal
' @what     n% \ 10 - a non-power-of-two constant divisor
' @expect   the verified reciprocal multiply replaces IDIV entirely
' @assert   absent idiv-bx
' @assert   present imul-bx
' @status   done
' ---------------------------------------------------------------------------
SUB DivideByConstantIsReciprocal(BYVAL n%) NOINLINE
  PRINT "recip"; n% \ 10
END SUB

' ---------------------------------------------------------------------------
' @scenario TrivialFunctionInlined
' @what     a call to a one-expression FUNCTION
' @expect   O6 substitutes the body - no call survives in the caller
' @assert   absent-call Doubled
' @status   done
' ---------------------------------------------------------------------------
SUB TrivialFunctionInlined(BYVAL n%) NOINLINE
  PRINT "inline"; Doubled(n%)
END SUB

FUNCTION Doubled%(BYVAL v%)
  Doubled = v% + v%
END FUNCTION

' ---------------------------------------------------------------------------
' @scenario PureCallFoldedAtCompileTime
' @what     a pure FUNCTION called with a constant argument
' @expect   O25 interprets it at compile time and leaves only the literal
' @assert   absent-call PureSquare
' @status   done
' ---------------------------------------------------------------------------
SUB PureCallFoldedAtCompileTime() NOINLINE
  PRINT "purefold"; PureSquare(12)
END SUB

FUNCTION PureSquare%(BYVAL v%)
  PureSquare = v% * v%
END FUNCTION

' ---------------------------------------------------------------------------
' @scenario ZeroUsesXorIdiom
' @what     assigning the constant zero
' @expect   O8 emits XOR AX,AX rather than MOV AX,0
' @assert   present xor-ax-ax
' @status   done
' ---------------------------------------------------------------------------
SUB ZeroUsesXorIdiom() NOINLINE
  z% = 0
  PRINT "zero"; z%
END SUB

' ---------------------------------------------------------------------------
' @scenario LoopInvariantHoisted
' @what     k%*17 recomputed every iteration although nothing in the loop
'           writes k%
' @expect   O22 hoists it into the preheader
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB LoopInvariantHoisted(BYVAL k%) NOINLINE
  s% = 0
  FOR i% = 1 TO 10
    s% = s% + k% * 17
  NEXT i%
  PRINT "licm"; s%
END SUB

' ---------------------------------------------------------------------------
' @scenario SelfTailCallIsAJump
' @what     a self-call in tail position
' @expect   O14 turns the recursion into a jump back to frame entry, so the
'           SUB contains no call to itself and runs in constant stack space
' @assert   absent-call SelfTailCallIsAJump
' @status   done
' ---------------------------------------------------------------------------
SUB SelfTailCallIsAJump(BYVAL n%, BYVAL acc%) NOINLINE
  IF n% = 0 THEN
    PRINT "tailcall"; acc%
    EXIT SUB
  END IF
  SelfTailCallIsAJump n% - 1, acc% + n%
END SUB

' ---------------------------------------------------------------------------
' @scenario DenseSelectIsJumpTable
' @what     a SELECT CASE over a dense run of integer constants
' @expect   O23 dispatches through a word jump table instead of a compare chain,
'           so the arms cost one indexed jump rather than N comparisons
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB DenseSelectIsJumpTable(BYVAL n%) NOINLINE
  r% = 0
  SELECT CASE n%
    CASE 0 : r% = 10
    CASE 1 : r% = 20
    CASE 2 : r% = 30
    CASE 3 : r% = 40
    CASE 4 : r% = 50
    CASE 5 : r% = 60
    CASE ELSE : r% = 99
  END SELECT
  PRINT "jumptable"; r%
END SUB

' ---------------------------------------------------------------------------
' @scenario FrameZeroingElided
' @what     a SUB whose every local is assigned before it is read
' @expect   O19 drops the per-invocation frame zero fill (REP STOSW)
' @assert   absent rep-stosw
' @status   done
' ---------------------------------------------------------------------------
SUB FrameZeroingElided(BYVAL n%) NOINLINE
  a% = n% + 1
  b% = a% * 2
  PRINT "framezero"; b%
END SUB

' ---------------------------------------------------------------------------
' @scenario SmallLoopUnrolled
' @what     a constant-trip INTEGER FOR loop with a tiny body
' @expect   O7 unrolls it under $OPTIMIZE SPEED - no loop-back branch remains
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB SmallLoopUnrolled() NOINLINE
  s% = 0
  FOR i% = 1 TO 4
    s% = s% + i%
  NEXT i%
  PRINT "unroll"; s%
END SUB

' ---------------------------------------------------------------------------
' @scenario ConstantArgumentPropagated
' @what     the caller hands Callee the same constant 100 at every call site
' @expect   nothing here: the win lands in the callee (see ConstantParameterIsALiteral).
'           $OPTIMIZE SPEED may even grow this SUB - trading size for cycles is its contract
' @assert   present-call Callee
' @status   done
' ---------------------------------------------------------------------------
SUB ConstantArgumentPropagated(BYVAL n%) NOINLINE
  Callee 100, n%
  Callee 100, n% + 1
END SUB

' ---------------------------------------------------------------------------
' @scenario Callee
' @what     a parameter that is the same constant at every call site and never
'           written in the body
' @expect   O18 IPCP reads it as that literal, so the parameter slot is never
'           loaded - the body shrinks
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB Callee(BYVAL fixed%, BYVAL varying%) NOINLINE
  PRINT "ipcp"; fixed% + varying%
END SUB

' ---------------------------------------------------------------------------
' @scenario StringChainSingleAllocation
' @what     a three-operand concatenation chain
' @expect   O24 builds it in one allocation instead of one per node: the chain
'           goes through rt_strcatn (sum the lengths, allocate once, copy) rather
'           than a pairwise rt_strcat per operator
' @assert   present-call rt_strcatn
' @status   done
' ---------------------------------------------------------------------------
SUB StringChainSingleAllocation(BYVAL n%) NOINLINE
  a$ = "one"
  b$ = "two"
  c$ = a$ + b$ + "three"
  PRINT "concat"; c$; n%
END SUB

' ---------------------------------------------------------------------------
' @scenario CopyPropagated
' @what     y% = x% then only y% is read
' @expect   O21 redirects the reads to x% and drops the copy entirely
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB CopyPropagated(BYVAL n%) NOINLINE
  x% = n% * 3
  y% = x%
  PRINT "copyprop"; y% + y%
END SUB

' ---------------------------------------------------------------------------
' @scenario TwoDimensionalIndexStrengthReduced
' @what     a rank-2 array walked by two nested counters
' @expect   the per-element address math is reduced to pointer stepping rather
'           than a multiply per access
' @assert   absent imul-bx
' @status   done
' ---------------------------------------------------------------------------
SUB TwoDimensionalIndexStrengthReduced() NOINLINE
  DIM g%(0 TO 3, 0 TO 3)
  FOR r% = 0 TO 3
    FOR c% = 0 TO 3
      g%(r%, c%) = r% + c%
    NEXT c%
  NEXT r%
  PRINT "twodim"; g%(3, 3)
END SUB

' ---------------------------------------------------------------------------
' @scenario LongArithmeticStaysOffTheFpu
' @what     an accumulator loop over LONG values - PB promotes +,-,* over
'           integral operands to floating point, so the naive lowering pays
'           FILD / the x87 op / FISTP and a memory staging cell at each end
' @expect   O13's 32-bit form computes it on the integer ALU instead; the only
'           thing left of the promotion is the overflow guard that reproduces
'           the x87's out-of-range sentinel
' @assert   absent fild-dword
' @assert   absent fistp-dword
' @status   done
' ---------------------------------------------------------------------------
SUB LongArithmeticStaysOffTheFpu(BYVAL n%) NOINLINE
  DIM acc AS LONG, step AS LONG
  step = n% AND 7
  acc = 0
  FOR i% = 1 TO 20
    acc = acc + step
    acc = acc - 1
  NEXT i%
  PRINT "longacc"; acc
END SUB

' ---------------------------------------------------------------------------
' @scenario AccumulateOverArrayIsHandQuality
' @what     the commonest loop there is: accumulate over an array
' @expect   what a person writing this by hand would produce - the counter, the
'           accumulator and the element pointer all live in registers, so the
'           whole loop is CMP/JG, ADD DI,[BX], ADD BX,2, ADD SI,1, JMP. There is
'           no subscript arithmetic left at all: the address is not rebuilt from
'           the counter (no MOV BX,AX), it is stepped, and the element is read
'           straight into the ALU op rather than staged through a register with
'           a PUSH/POP pair around it
' @assert   absent imul-bx
' @assert   absent push-ax-pop-ax
' @assert   absent mov-bx-ax
' @assert   present add-di-mem-bx
' @assert   present add-bx-2
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB AccumulateOverArrayIsHandQuality(BYVAL n%) NOINLINE
  DIM a%(0 TO 49)
  a%(7) = n%
  s% = 0
  FOR i% = 0 TO 49
    s% = s% + a%(i%)
  NEXT i%
  PRINT "accumulate"; s%
END SUB

' ---------------------------------------------------------------------------
' @scenario ComparisonBranchesOnItsOwnFlags
' @what     the shape almost every conditional has: IF <comparison> THEN
' @expect   the CMP's flags drive the branch. PB's truth value is -1/0, but when
'           the comparison IS the condition nothing ever reads that value, so
'           materializing it (MOV AX,-1 / Jcc / MOV AX,0) and then testing it
'           (TEST AX,AX) is five dead instructions per conditional - a person
'           writing this by hand would emit CMP / Jcc and nothing else. The
'           closing jump of a THEN arm with no ELSE is a jump to the next
'           instruction and disappears too
' @assert   absent mov-ax-minus1
' @assert   absent test-ax-ax
' @assert   absent jmp-to-next
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB ComparisonBranchesOnItsOwnFlags(BYVAL n%) NOINLINE
  DIM hits%, i%
  FOR i% = 1 TO 20
    IF i% > n% THEN
      hits% = hits% + 1
    END IF
  NEXT i%
  PRINT "cmpbranch"; hits%
END SUB

' ---------------------------------------------------------------------------
' @scenario ConstantStoredAsImmediate
' @what     assigning a compile-time constant to a local
' @expect   one store of an immediate (MOV WORD PTR [bp-x],imm), not a load into
'           the accumulator and a copy out of it. -32768 is the interesting one:
'           the literal is LONG and the negation is float-promoted, so the plain
'           path drives it through the x87 (FILD / FCHS / FISTP) to land two
'           bytes in a cell whose value is known at compile time
' @assert   absent fild-dword
' @assert   present mov-mem-imm
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB ConstantStoredAsImmediate(BYVAL n%) NOINLINE
  DIM low%, high%
  low% = -32768
  high% = 32767
  IF n% > 0 THEN low% = low% + 1
  PRINT "constimm"; low%; high%
END SUB

' ---------------------------------------------------------------------------
' @scenario MaxScanReadsEachElementOnce
' @what     the max-scan idiom, where the condition and the arm name the same
'           array element
' @expect   the element is fetched once. Cross-block CSE keeps the value the
'           condition loaded available to the arm the condition dominates, and
'           redundant-load elimination then drops the reload of the slot as
'           well, because the register still holds it: one MOV AX,[BX+disp]
'           per iteration - the arm reuses the register, not the slot
' @assert   count mov-ax-mem-bx 1
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB MaxScanReadsEachElementOnce() NOINLINE
  DIM a%(0 TO 19), m%, i%
  FOR i% = 0 TO 19
    a%(i%) = (i% * 7) MOD 23
  NEXT i%
  m% = -32768
  FOR i% = 0 TO 19
    IF a%(i%) > m% THEN m% = a%(i%)
  NEXT i%
  PRINT "maxscan"; m%
END SUB

' ---------------------------------------------------------------------------
' @scenario BoundedMultiplyStaysOffTheFpu
' @what     a LONG-typed product whose operands have provably small ranges
' @expect   the optimizer proves the product fits int32 from the operand ranges
'           (each masked to <= 255, so the product is <= 65025 << 2^31) and
'           computes it in the native 32-bit ALU. PB promotes an integer multiply
'           to floating point, so without the range proof this pays the full x87
'           round-trip - FILD each operand, FMUL, FISTP the result - for a value
'           that never needed the FPU at all. Range facts avoid the whole thing.
' @assert   absent fild-dword
' @assert   absent fistp-dword
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB BoundedMultiplyStaysOffTheFpu(BYVAL n%) NOINLINE
  DIM p AS LONG, acc AS LONG, i%
  acc = 0
  FOR i% = 0 TO 40
    p = (i% AND 255) * (n% AND 255)
    acc = acc + p
  NEXT i%
  PRINT "boundedmul"; acc
END SUB

' ---------------------------------------------------------------------------
' @scenario HotAccumulatorWinsTheRegister
' @what     a loop with both a throwaway scratch and a real accumulator
' @expect   the register (DI) goes to the value carried ACROSS iterations - the
'           self-referential accumulator acc = acc + x - not to the scratch that
'           is recomputed and dies each pass. The scratch reaches the ALU op from
'           memory (add di,[scratch]); parking the scratch in DI instead would
'           leave the hot accumulator in memory, load/store every iteration. So
'           the accumulate is a memory-source add straight into the resident
'           register, and there is no per-iteration load+store of the accumulator
' @assert   present add-di-mem-bp
' @assert   absent push-ax-pop-ax
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB HotAccumulatorWinsTheRegister(BYVAL n%) NOINLINE
  DIM acc%, scratch%, i%
  acc% = 0
  FOR i% = 1 TO 100
    scratch% = i% AND n%
    acc% = acc% + scratch%
  NEXT i%
  PRINT "hotacc"; acc%
END SUB

' ---------------------------------------------------------------------------
' @scenario AndOrConditionShortCircuits
' @what     IF <cmp> AND <cmp> (and OR) as the whole condition
' @expect   the comparisons short-circuit into conditional branches, the way a
'           person writes IF x>0 AND x<100 by hand: CMP / JLE skip / CMP / JGE
'           skip. PB's AND/OR are bitwise, but over comparison results (-1/0)
'           they equal the logical operators, and pure operands have no side
'           effect to skip - so materializing each comparison as -1/0, bitwise-
'           ANDing them and testing the result (four instructions of truth-value
'           machinery per operand, plus a PUSH/POP to stage the two) is all dead
' @assert   absent mov-ax-minus1
' @assert   absent push-ax-pop-ax
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB AndOrConditionShortCircuits(BYVAL n%) NOINLINE
  DIM r%
  r% = 0
  IF n% > 0 AND n% < 100 THEN r% = r% + 1
  IF n% = 5 OR n% = 42 THEN r% = r% + 10
  PRINT "shortcircuit"; r%
END SUB

' ---------------------------------------------------------------------------
' @scenario ParityTestIsAMask
' @what     n MOD 2^j compared to zero (the even/odd test)
' @expect   n MOD 2 = 0 tests only whether the low bit is zero, which n AND 1
'           answers directly - the signed-modulo fixup (CWD then a five-op sign
'           correction for a negative dividend) never changes whether the result
'           is zero, so it is dead here. The condition becomes AND ax,1 driving
'           the branch on its own flags, not a full IDIV-style modulo followed by
'           a compare
' @assert   absent cwd
' @assert   absent idiv-bx
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB ParityTestIsAMask(BYVAL n%) NOINLINE
  DIM r%
  r% = 0
  IF n% MOD 2 = 0 THEN r% = r% + 1
  IF n% MOD 4 <> 0 THEN r% = r% + 10
  PRINT "parity"; r%
END SUB
