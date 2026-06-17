' Differential battery 94: O9 in-place literal self-append (rt_strcatlit).
' Under pb36 $OPTIMIZE SPEED `s$ = s$ + "literal"` appends the literal bytes in place when s$ is
' the topmost heap block (growing it, same handle), else falls back to StrMem+StrCat. The string
' value is identical either way, so output must stay byte-identical to genuine PBC 3.50 under both
' pb35 and pb36 - even though the heap churn differs. The cases below stress topmost vs not-topmost,
' interleaved allocations, compaction-length, the empty literal, and reads between appends.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' plain build loop: s$ grows in place each iteration (s$ stays topmost)
s$ = ""
FOR i% = 1 TO 12
  s$ = s$ + "ab"
NEXT i%
PRINT #1, "build "; s$; " "; LEN(s$)

' build interleaved with another string allocation each iteration: s$ is NOT topmost when the
' append runs (w$ was allocated after it), so the fallback path must produce the same result
v$ = "<"
FOR i% = 1 TO 8
  w$ = "[" + v$ + "]"
  v$ = v$ + "."
NEXT i%
PRINT #1, "intlv "; v$; " "; w$

' empty literal append is a no-op
e$ = "keep"
e$ = e$ + ""
PRINT #1, "empty "; e$

' start from an empty target then append a literal
z$ = ""
z$ = z$ + "first"
z$ = z$ + "-second"
PRINT #1, "fromempty "; z$

' read the growing string between appends (LEN / MID$ touch the heap)
r$ = "0"
FOR i% = 1 TO 6
  r$ = r$ + "#"
  PRINT #1, "r"; i%; LEN(r$); MID$(r$, 1, 2);
NEXT i%
PRINT #1, ""

' long build to push past compaction thresholds (still topmost each step)
g$ = ""
FOR i% = 1 TO 200
  g$ = g$ + "xyz"
NEXT i%
PRINT #1, "long "; LEN(g$); " "; LEFT$(g$, 6); " "; RIGHT$(g$, 6)

' two independent builders alternating (each loses topmost to the other, both fall back)
p$ = "p"
q$ = "q"
FOR i% = 1 TO 10
  p$ = p$ + "A"
  q$ = q$ + "B"
NEXT i%
PRINT #1, "two "; p$; " "; q$

CLOSE #1
END
