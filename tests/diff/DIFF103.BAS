' Differential battery 103: a LONG FOR loop whose body hoists a loop-invariant constant
' (here the divisor of an integer divide / MOD) must not corrupt the loop bound.
' The LICM preheader stashes the invariant into a frame CSE slot; that slot must be
' reserved BEFORE the loop's 4-byte limit/step temps are allocated, otherwise the hoisted
' value lands on top of the limit (a 4-byte LONG limit sits at the same frame offset as the
' first CSE slot) and the loop never terminates. Byte-identical to genuine PBC 3.50 under
' pb35 and pb36 - the loops run their exact trip counts and \ / MOD match for positive,
' negative and sign-crossing dividends with positive and negative constant divisors.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' positive dividend, positive divisor: divide and remainder
FOR a& = 0 TO 12
  PRINT #1, "p"; a&; a& \ 3; a& MOD 3
NEXT a&

' sign-crossing dividend, positive divisor
FOR b& = -7 TO 7
  PRINT #1, "s"; b&; b& \ 4; b& MOD 4
NEXT b&

' sign-crossing dividend, NEGATIVE divisor - the case the limit-slot collision broke
FOR c& = -6 TO 6
  PRINT #1, "n"; c&; c& \ -3; c& MOD -3
NEXT c&

CLOSE #1
END
