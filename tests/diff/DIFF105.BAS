' Differential battery 105: $ERROR OVERFLOW signed-LONG multiply model.
' Genuine PBC 3.50 computes a signed-LONG multiply in a WIDE type and traps (error 6)
' only when the wide product is narrowed into a LONG store - a wide use (PRINT) shows the
' full product with no trap; an in-range store is fine; a DWORD multiply wraps and never
' traps. (Also pins the prerequisite fix: under $ERROR OVERFLOW the SCCP-proven constant
' stores a&/b& are NOT folded into their reads, so dead-store elimination must keep them -
' the un-folded multiply reads them from memory.)
'
' The narrowing store RANGE-CHECKS against the LONG limits before FISTP rather than reading
' the x87 Invalid-Operation flag after it: FISTP does store the sentinel (8000_0000h) when
' the value will not fit, but the IE bit it should set alongside does not survive to a FSTSW
' under emulation, so the flag-based form stored the sentinel and trapped nothing. The
' LONG +/- half of the same model is DIFF113.
$ERROR OVERFLOW ON
ON ERROR GOTO trap
OPEN "RESULT.TXT" FOR OUTPUT AS #1

a& = 100000
b& = 100000
PRINT #1, "wide"; a& * b&          ' wide product shown in full (10000000000), no trap
c& = a& * b&                       ' narrowing the overflowing product into a LONG -> error 6
PRINT #1, "store"; c&              ' the assignment never completed, so c& stayed 0

a& = 1000
b& = 1000
d& = a& * b&                       ' 1000000 fits a LONG -> no trap
PRINT #1, "ok"; d&

e??? = 100000
f??? = 100000
g??? = e??? * f???                 ' DWORD multiply wraps (1410065408), never traps
PRINT #1, "dword"; g???

CLOSE #1
END

trap:
PRINT #1, "ERR"; ERR
RESUME NEXT
