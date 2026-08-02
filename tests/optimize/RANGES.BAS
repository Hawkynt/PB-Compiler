' ===========================================================================
' Optimization battery - value ranges (O16 interval lattice)
' ===========================================================================
' Every SUB below is one scenario: its header says what the optimizer is
' expected to do with it, and OptimizationBatteryTests checks that against the
' bytes actually emitted for that SUB. The main body calls all of them, so the
' battery is also a runnable program whose output must match RANGES.expected
' with the optimizer both ON and OFF.
'
' Assertion vocabulary (see OptimizationBatteryTests):
'   present/absent <pattern>        a named byte pattern occurs (or not)
'   present-call/absent-call <lbl>  a near call to that label occurs (or not)
'   bytes<= <n>                     the SUB is at most n bytes of code
'   smaller-than-unoptimized        the SUB shrank versus $OPTIMIZE OFF
' Every runtime check (bounds, overflow, divide-by-zero) raises through
' rt_raise, so "absent-call rt_raise" means "no check survived in this SUB".
'
' @status roadmap parks an expectation we WANT but do not deliver yet: it is
' reported, never fails the build. Implement it, then flip to @status done.
' ===========================================================================
$OPTIMIZE SPEED
$ERROR BOUNDS ON
$ERROR OVERFLOW ON

DECLARE SUB IndexRangeFromCounter()
DECLARE SUB IndexRangeUnknownKeepsCheck(BYVAL k%)
DECLARE SUB DerivedRangeFromCounter()
DECLARE SUB MaskedIndexNeedsNoCheck(BYVAL h%)
DECLARE SUB ModuloIndexNeedsNoCheck()
DECLARE SUB ScaledIndexNeedsNoCheck()
DECLARE SUB OverflowCheckDroppedOverCounter()
DECLARE SUB DivideGuardDroppedOverCounter()
DECLARE SUB LongCompareNarrowedToWord()
DECLARE SUB InvariantComparisonFolded()
DECLARE SUB RangeRefinedByIfArm(BYVAL v%)
DECLARE SUB RangeRefinedByAndCondition(BYVAL v%)
DECLARE SUB RangeSurvivesOpaqueCall(BYVAL seed%)
DECLARE SUB RangeRefinedBySelectArm(BYVAL v%)
DECLARE SUB ZeroTripLoopDropped()
DECLARE SUB ImpossibleByBits(BYVAL n%)
DECLARE SUB ImpossibleByCongruence(BYVAL n%)
DECLARE SUB PossibleValuesSurvive(BYVAL n%)
DECLARE SUB RedundantOpsRemoved(BYVAL n%)
DECLARE SUB Opaque()

IndexRangeFromCounter
IndexRangeUnknownKeepsCheck 4
DerivedRangeFromCounter
MaskedIndexNeedsNoCheck 19
ModuloIndexNeedsNoCheck
ScaledIndexNeedsNoCheck
OverflowCheckDroppedOverCounter
DivideGuardDroppedOverCounter
LongCompareNarrowedToWord
InvariantComparisonFolded
RangeRefinedByIfArm 5
RangeRefinedByAndCondition 6
RangeSurvivesOpaqueCall 19
RangeRefinedBySelectArm 2
ZeroTripLoopDropped
ImpossibleByBits 7
ImpossibleByCongruence 7
PossibleValuesSurvive 8
RedundantOpsRemoved 9
END

' ---------------------------------------------------------------------------
' @scenario IndexRangeFromCounter
' @what     a%(i%) indexed by a FOR counter whose [0,9] range is the array's
' @expect   every Error-9 bounds check in the loop is proven dead and dropped
' @assert   absent-call rt_raise
' @status   done
' ---------------------------------------------------------------------------
SUB IndexRangeFromCounter() NOINLINE
  DIM a%(0 TO 9)
  FOR i% = 0 TO 9
    a%(i%) = i% + i%
  NEXT i%
  PRINT "counter"; a%(9)
END SUB

' ---------------------------------------------------------------------------
' @scenario IndexRangeUnknownKeepsCheck
' @what     the same store indexed by a parameter of unknown range
' @expect   the check MUST stay - this is the control proving the lattice is
'           not simply dropping every check it sees
' @assert   present-call rt_raise
' @status   done
' ---------------------------------------------------------------------------
SUB IndexRangeUnknownKeepsCheck(BYVAL k%) NOINLINE
  DIM a%(0 TO 9)
  a%(k%) = 7
  PRINT "unknown"; a%(4)
END SUB

' ---------------------------------------------------------------------------
' @scenario DerivedRangeFromCounter
' @what     j% = i% + 1 carries the counter's range into a derived variable
' @expect   a%(j%) over [1,10] is inside the array, so no check is emitted
' @assert   absent-call rt_raise
' @status   done
' ---------------------------------------------------------------------------
SUB DerivedRangeFromCounter() NOINLINE
  DIM a%(0 TO 10)
  FOR i% = 0 TO 9
    j% = i% + 1
    a%(j%) = j%
  NEXT i%
  PRINT "derived"; a%(10)
END SUB

' ---------------------------------------------------------------------------
' @scenario MaskedIndexNeedsNoCheck
' @what     a%(h% AND 7) - the mask bounds the index for ANY h%, even negative
' @expect   no check, without knowing anything about h%
' @assert   absent-call rt_raise
' @status   done
' ---------------------------------------------------------------------------
SUB MaskedIndexNeedsNoCheck(BYVAL h%) NOINLINE
  DIM a%(0 TO 7)
  a%(h% AND 7) = 3
  PRINT "masked"; a%(3)
END SUB

' ---------------------------------------------------------------------------
' @scenario ModuloIndexNeedsNoCheck
' @what     a%(i% MOD 8) with i% a non-negative counter: MOD keeps the sign of
'           the dividend, so the result is in [0,7]
' @expect   no check
' @assert   absent-call rt_raise
' @status   done
' ---------------------------------------------------------------------------
SUB ModuloIndexNeedsNoCheck() NOINLINE
  DIM a%(0 TO 7)
  FOR i% = 0 TO 100
    a%(i% MOD 8) = i%
  NEXT i%
  PRINT "modulo"; a%(7)
END SUB

' ---------------------------------------------------------------------------
' @scenario ScaledIndexNeedsNoCheck
' @what     a%(i% \ 4) - truncated divide is monotonic, so [0,39] scales to [0,9]
' @expect   no check
' @assert   absent-call rt_raise
' @status   done
' ---------------------------------------------------------------------------
SUB ScaledIndexNeedsNoCheck() NOINLINE
  DIM a%(0 TO 9)
  FOR i% = 0 TO 39
    a%(i% \ 4) = i%
  NEXT i%
  PRINT "scaled"; a%(9)
END SUB

' ---------------------------------------------------------------------------
' @scenario OverflowCheckDroppedOverCounter
' @what     t% = i% + 1 over [1,100] can never leave 16 bits
' @expect   the $ERROR OVERFLOW Error-6 trap is dead and dropped
' @assert   absent-call rt_raise
' @status   done
' ---------------------------------------------------------------------------
SUB OverflowCheckDroppedOverCounter() NOINLINE
  FOR i% = 1 TO 100
    t% = i% + 1
  NEXT i%
  PRINT "overflow"; t%
END SUB

' ---------------------------------------------------------------------------
' @scenario DivideGuardDroppedOverCounter
' @what     100 \ i% with i% in [1,10] - the divisor provably excludes zero
' @expect   the always-emitted Error-11 divide-by-zero guard disappears
' @assert   absent-call rt_raise
' @status   done
' ---------------------------------------------------------------------------
SUB DivideGuardDroppedOverCounter() NOINLINE
  FOR i% = 1 TO 10
    q% = 100 \ i%
  NEXT i%
  PRINT "divguard"; q%
END SUB

' ---------------------------------------------------------------------------
' @scenario LongCompareNarrowedToWord
' @what     a LONG comparison whose operands both provably fit one word
' @expect   the nine-instruction 32-bit compare collapses to one CMP AX,BX
' @assert   absent sbb-dx-cx
' @assert   present cmp-ax-bx
' @status   done
' ---------------------------------------------------------------------------
SUB LongCompareNarrowedToWord() NOINLINE
  n& = 0
  FOR i& = 1 TO 100
    IF i& < 50& THEN n& = n& + 1
  NEXT i&
  PRINT "narrowcmp"; n&
END SUB

' ---------------------------------------------------------------------------
' @scenario InvariantComparisonFolded
' @what     i% < 1000 is true for every value the counter can take
' @expect   the comparison folds to a constant, so the SUB shrinks
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB InvariantComparisonFolded() NOINLINE
  n% = 0
  FOR i% = 1 TO 10
    IF i% < 1000 THEN n% = n% + 1
  NEXT i%
  PRINT "invariant"; n%
END SUB

' ---------------------------------------------------------------------------
' @scenario RangeRefinedByIfArm
' @what     inside IF v% <= 7 the parameter is known to be in [.., 7], and the
'           ELSE-free arm also knows v% >= 0 from the outer test
' @expect   the guarded store needs no bounds check
' @assert   absent-call rt_raise
' @status   done
' ---------------------------------------------------------------------------
SUB RangeRefinedByIfArm(BYVAL v%) NOINLINE
  DIM a%(0 TO 7)
  IF v% >= 0 THEN
    IF v% <= 7 THEN
      a%(v%) = 1
    END IF
  END IF
  PRINT "ifarm"; a%(5)
END SUB

' ---------------------------------------------------------------------------
' @scenario RangeRefinedByAndCondition
' @what     one IF whose condition ANDs both bounds - the everyday spelling of a
'           guarded index, where each half decides a different endpoint
' @expect   both halves refine the same variable, so the guarded store is proven
'           in range and needs no check
' @assert   absent-call rt_raise
' @status   done
' ---------------------------------------------------------------------------
SUB RangeRefinedByAndCondition(BYVAL v%) NOINLINE
  DIM a%(0 TO 7)
  IF v% >= 0 AND v% <= 7 THEN
    a%(v%) = 6
  END IF
  PRINT "andguard"; a%(6)
END SUB

' ---------------------------------------------------------------------------
' @scenario RangeSurvivesOpaqueCall
' @what     k% is a LOCAL bounded by a mask; an opaque SUB call cannot reach it
' @expect   the call must not erase what is known about k% - the store after it
'           still needs no bounds check
' @assert   absent-call rt_raise
' @status   done
' ---------------------------------------------------------------------------
SUB RangeSurvivesOpaqueCall(BYVAL seed%) NOINLINE
  DIM a%(0 TO 7)
  k% = seed% AND 7
  Opaque
  a%(k%) = 2
  PRINT "acrosscall"; a%(3)
END SUB

' ---------------------------------------------------------------------------
' @scenario RangeRefinedBySelectArm
' @what     a SELECT CASE arm constrains the subject exactly like an IF arm
' @expect   inside CASE 0 TO 7 the subject is in [0,7], so no bounds check
' @assert   absent-call rt_raise
' @status   done
' ---------------------------------------------------------------------------
SUB RangeRefinedBySelectArm(BYVAL v%) NOINLINE
  DIM a%(0 TO 7)
  SELECT CASE v%
    CASE 0 TO 7
      a%(v%) = 4
  END SELECT
  PRINT "selectarm"; a%(2)
END SUB

' ---------------------------------------------------------------------------
' @scenario ZeroTripLoopDropped
' @what     FOR i% = 5 TO 1 with the default STEP 1 never runs its body
' @expect   the body is not emitted at all (the counter still ends up assigned)
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB ZeroTripLoopDropped() NOINLINE
  n% = 7
  FOR i% = 5 TO 1
    n% = n% + 1
  NEXT i%
  PRINT "zerotrip"; n%
END SUB

' ---------------------------------------------------------------------------
' @scenario ImpossibleByBits
' @what     tests an even value against an odd one, and a masked value against
'           a constant outside the mask - neither is expressible as an interval
' @expect   both comparisons fold away: (n\2)*2 always has bit 0 clear, and
'           n AND 12 can only ever set bits 2 and 3. (The SUB still calls
'           rt_raise: this battery runs under $ERROR OVERFLOW, and the sum in
'           the PRINT genuinely can overflow.)
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB ImpossibleByBits(BYVAL n%) NOINLINE
  a% = (n% \ 2) * 2
  IF a% = 1 THEN PRINT "unreachable-even"
  b% = n% AND 12
  IF b% = 5 THEN PRINT "unreachable-mask"
  c% = n% OR 1
  IF c% = 8 THEN PRINT "unreachable-or"
  PRINT "bits"; a% + b% + c%
END SUB

' ---------------------------------------------------------------------------
' @scenario ImpossibleByCongruence
' @what     a multiple of ten tested against a value that is not one
' @expect   folds away - divisibility by a non-power-of-two is exactly what the
'           congruence domain adds over ranges and bits
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB ImpossibleByCongruence(BYVAL n%) NOINLINE
  t% = n% * 10
  IF t% = 25 THEN PRINT "unreachable-mul10"
  IF t% = 33 THEN PRINT "unreachable-mul10b"
  PRINT "congruence"; t%
END SUB

' ---------------------------------------------------------------------------
' @scenario PossibleValuesSurvive
' @what     the same shapes tested against values that ARE reachable
' @expect   nothing folds - the domains must exclude only what is truly
'           impossible, so these comparisons stay in the emitted code (against a
'           constant, so CMP AX,imm rather than CMP AX,BX)
' @assert   present cmp-ax-imm
' @status   done
' ---------------------------------------------------------------------------
SUB PossibleValuesSurvive(BYVAL n%) NOINLINE
  a% = (n% \ 2) * 2
  IF a% = 8 THEN PRINT "reachable-even"
  t% = n% * 10
  IF t% = 80 THEN PRINT "reachable-mul10"
  PRINT "possible"; a% + t%
END SUB

' ---------------------------------------------------------------------------
' @scenario RedundantOpsRemoved
' @what     operations whose result the facts already know: an AND that clears
'           bits already clear, a MOD of a value already inside the modulus, a
'           MOD of a provable multiple, and a divide whose result cannot be
'           anything but zero
' @expect   each collapses to its operand or to a constant - the divide leaves
'           no IDIV at all, and only the operands' side effects remain
' @assert   absent idiv-bx
' @assert   smaller-than-unoptimized
' @status   done
' ---------------------------------------------------------------------------
SUB RedundantOpsRemoved(BYVAL n%) NOINLINE
  a% = (n% AND 15) AND 255
  b% = (n% * 10) MOD 5
  c% = (n% AND 7) MOD 16
  d% = (n% AND 3) \ 8
  m% = n% OR 255                 ' not a constant, but its low byte is provably all ones
  e% = (n% AND 15) AND m%        ' ... so this AND cannot clear anything either
  f% = 15 AND (n% AND 3)         ' operands the other way round - the op is symmetric
  PRINT "redundant"; a%; b%; c%; d%; e%; f%
END SUB

' An opaque callee: it exists only so a call site cannot be reasoned through.
SUB Opaque() NOINLINE
  gNoise% = gNoise% + 1
END SUB
