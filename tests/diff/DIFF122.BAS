' Differential battery 122: O0302 INSTR search-algorithm selection.
' The haystack is runtime data so the compiler cannot fold the searches away. Constant needles cover
' the single-byte scan, the short candidate-scan verifier and the long Horspool path, including
' repeated prefixes, explicit start positions, misses, and a needle longer than the remaining text.
' The final runtime needle keeps the generic path as a control.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

READ s$
PRINT #1, INSTR(s$, "A")
PRINT #1, INSTR(s$, "AB")
PRINT #1, INSTR(5, s$, "AB")
PRINT #1, INSTR(s$, "ABCD")
PRINT #1, INSTR(s$, "BEGIN")
PRINT #1, INSTR(12, s$, "BEGIN")
PRINT #1, INSTR(s$, "ABABA")
PRINT #1, INSTR(s$, "MISSING")
PRINT #1, INSTR(30, s$, "BEGINNING-IS-LONGER")

READ n$
PRINT #1, INSTR(s$, n$)

CLOSE #1
END

DATA "ZZABZZABCDZZBEGINZZABABABAZZBEGINNING"
DATA "BEGIN"
