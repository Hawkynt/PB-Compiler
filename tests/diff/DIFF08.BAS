' Differential battery 8: small PB 3.5 surface - TRIM$, ASC statement/position,
' CVx start offsets, SIZEOF, ERRCLEAR, RND(a,z) range, CONSIN/CONSOUT.
OPEN "RESULT.TXT" FOR OUTPUT AS #1
s$ = "  padded  "
PRINT #1, "[" + TRIM$(s$) + "]"
PRINT #1, "[" + LTRIM$(s$) + "]"
PRINT #1, "[" + RTRIM$(s$) + "]"

t$ = "ABC"
ASC(t$, 1) = 90   ' position is mandatory in real PBC 3.50
PRINT #1, t$
ASC(t$, 2) = 88
PRINT #1, t$
PRINT #1, ASC(t$, 3)

x$ = MKI$(100) + MKL$(70000)
PRINT #1, CVI(x$)
PRINT #1, CVL(x$, 3)
PRINT #1, CVI(x$, 1)

i% = 1
l& = 2
d# = 3
f$ = "dyn"
DIM fx AS STRING * 7
PRINT #1, SIZEOF(i%)
PRINT #1, SIZEOF(l&)
PRINT #1, SIZEOF(d#)
PRINT #1, SIZEOF(f$)
PRINT #1, SIZEOF(fx)
PRINT #1, LEN(f$)

ok% = -1
FOR n% = 1 TO 20
  r& = RND(5, 10)
  IF r& < 5 OR r& > 10 THEN ok% = 0
NEXT n%
PRINT #1, ok%

PRINT #1, CONSIN
PRINT #1, CONSOUT
CLOSE #1
END
