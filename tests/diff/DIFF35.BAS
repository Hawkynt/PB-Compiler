' Differential battery 35: O13 fixed-point FOR counters. Power-of-two-fraction
' steps run as scaled integers; every counter value, sum and end value must
' match the genuine x87 FADD loop bit for bit. Non-dyadic steps stay generic.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1
s! = 0
FOR x! = 0 TO 1 STEP 0.25
  PRINT #1, x!;
  s! = s! + x!
NEXT x!
PRINT #1, "|"; s!; x!
t# = 0
FOR y# = -2 TO 2 STEP 0.5
  t# = t# + y#
NEXT y#
PRINT #1, t#; y#
FOR z! = 1 TO 0 STEP -0.125
  PRINT #1, z!;
NEXT z!
PRINT #1, "|"; z!
' counter not read in body (pure trip control)
c% = 0
FOR w! = 0 TO 10 STEP 0.5
  c% = c% + 1
NEXT w!
PRINT #1, c%; w!
' zero-trip
FOR e! = 5 TO 1 STEP 0.5
  PRINT #1, "X";
NEXT e!
PRINT #1, e!
' non-dyadic step stays on the generic float path
g! = 0
FOR n! = 0 TO 1 STEP 0.3
  g! = g! + n!
NEXT n!
PRINT #1, g!; n!
CLOSE #1
END
