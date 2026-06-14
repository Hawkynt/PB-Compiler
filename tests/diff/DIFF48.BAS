' Differential battery 48: SSA + SCCP mid-end (O17). pb36 propagates constants
' across statements and through provably-taken branches, folding reads the local
' folder cannot. Every folded value must equal what the program actually
' computes - including PB zero-init, signed wrap, and dead-arm pruning - so the
' output stays byte-identical to genuine PBC 3.50.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' ---- straight-line constant propagation into reads --------------------------
a% = 5
b% = a% + 1
PRINT #1, a%; b%; a% AND 3; a% OR 8; (a% = 5); (b% > a%)

' ---- propagation through a provably-true IF (the ELSE is dead) --------------
x% = 10
IF x% > 3 THEN
  y% = 100
ELSE
  y% = 200
END IF
PRINT #1, y%

' ---- provably-false condition takes the ELSE -------------------------------
g% = 0
IF g% THEN
  h% = 1
ELSE
  h% = 2
END IF
PRINT #1, h%

' ---- an uninitialized variable is provably zero ----------------------------
PRINT #1, u%; u% + 7; (u% = 0)

' ---- signed 16-bit wrap survives the fold ----------------------------------
w% = 30000
PRINT #1, w% + 30000; w% AND &H7FFF

' ---- LONG propagation ------------------------------------------------------
n& = 100000
PRINT #1, n& + 1; (n& = 100000); n& AND &HFFFF&

' ---- nested IFs, both conditions constant ----------------------------------
p% = 7
IF p% > 5 THEN
  IF p% < 10 THEN
    r% = 42
  ELSE
    r% = 43
  END IF
ELSE
  r% = 44
END IF
PRINT #1, r%

CLOSE #1
END
