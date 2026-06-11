' PB 3.0 quirk battery - activates when a genuine PBC 3.00 lands in
' tools/pb30/ (run-diff-tests.sh compiles this with `--dialect pb30`).
' Exercises: signed radix without the 3.1 leading-zero escape (FAQ 2.1/2.2),
' the equate constant-folding bug (FAQ 2.26), and 16-bit HEX$/OCT$/BIN$.
%K = -20-4
OPEN "RESULT.TXT" FOR OUTPUT AS #1
PRINT #1, &HA000
PRINT #1, &H0A000
PRINT #1, &H0FFFF
PRINT #1, %K
PRINT #1, HEX$(-1)
PRINT #1, OCT$(-1)
i% = -2
PRINT #1, HEX$(i%)
w?? = 40000
PRINT #1, w??
CLOSE #1
END
