' Differential battery 101: O16 lattice-driven dead-arm elimination.
' When the interval lattice proves an IF/ELSEIF condition invariant over a variable's range, the
' unreachable arm is not emitted at all (whole-branch dead-code elimination). The LIVE arm always
' runs, so output is identical to genuine PBC 3.50 under pb35 and pb36 even though the dead code
' is gone.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' always-true: p% in [5,8] is < 20 -> the ELSE arm is dead, the THEN runs
p% = 5
IF q% > 0 THEN p% = 8
IF p% < 20 THEN
  PRINT #1, "true"; p%
ELSE
  PRINT #1, "unreached-a"
END IF

' always-false: r% in [10,13] is NOT < 5 -> the THEN is dead, the ELSE runs
r% = 10
IF q% > 0 THEN r% = 13
IF r% < 5 THEN
  PRINT #1, "unreached-b"
ELSE
  PRINT #1, "false"; r%
END IF

' ELSEIF chain where the lattice resolves the first to false and the second to true
s% = 7
IF q% > 0 THEN s% = 9
IF s% > 20 THEN
  PRINT #1, "big"
ELSEIF s% >= 5 THEN
  PRINT #1, "mid"; s%
ELSE
  PRINT #1, "small"
END IF

CLOSE #1
END
