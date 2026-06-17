' Differential battery 95: O9 in-place variable self-append (rt_strcatvar).
' Under pb36 $OPTIMIZE SPEED `s$ = s$ + v$` (v$ a bare string variable) appends v$'s bytes in
' place when s$ is topmost (heap->heap copy, same handle, v$ left intact), covering self-double
' `s$ = s$ + s$` (destination starts where the source ends, REP MOVSB forward, no overlap), and
' falls back to StrDup+StrCat otherwise. Output is identical to genuine PBC 3.50 under pb35 and
' pb36 even though heap churn differs.
$OPTIMIZE SPEED
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' plain build: append a fixed variable each iteration (s$ stays topmost)
d$ = "-"
s$ = ""
FOR i% = 1 TO 10
  s$ = s$ + d$
NEXT i%
PRINT #1, "build "; s$; " "; LEN(s$)

' self-double: s$ = s$ + s$ (source == target, adjacent forward copy)
u$ = "ab"
FOR i% = 1 TO 5
  u$ = u$ + u$
NEXT i%
PRINT #1, "double "; u$; " "; LEN(u$)

' append a variable that CHANGES each iteration (source read fresh each time)
c$ = ""
p$ = "."
FOR i% = 1 TO 6
  p$ = p$ + "."
  c$ = c$ + p$
NEXT i%
PRINT #1, "vary "; c$; " "; LEN(c$)

' not topmost: another string allocated after s$ each iteration -> fallback path
v$ = "<"
FOR i% = 1 TO 7
  w$ = "[" + v$ + "]"
  x$ = "x"
  v$ = v$ + x$
NEXT i%
PRINT #1, "fb "; v$; " "; w$

' empty source and empty target edge cases
e$ = "keep"
z$ = ""
e$ = e$ + z$
PRINT #1, "emptysrc "; e$
g$ = ""
h$ = "hi"
g$ = g$ + h$
PRINT #1, "emptytgt "; g$

' read the growing string between appends
r$ = "0"
t$ = "#"
FOR i% = 1 TO 5
  r$ = r$ + t$
  PRINT #1, "r"; i%; LEN(r$); LEFT$(r$, 2);
NEXT i%
PRINT #1, ""

CLOSE #1
END
