' Differential battery 17: memory models - HUGE (DOS conventional memory,
' cross-64KiB indexing), VIRTUAL (EMS round trip, FRE(-11)), ABSOLUTE
' (AT segment readback through B800 text memory), REDIM PRESERVE.
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' ---- HUGE: 400000 bytes of LONGs spanning several segments ----------------
DIM HUGE h(0 TO 99999) AS LONG
h(0) = 11
h(16383) = 22
h(16384) = 33
h(50000) = 1234567
h(99999) = 999
PRINT #1, h(0); h(16383); h(16384); h(50000); h(99999)
t& = 0
FOR i& = 49998 TO 50002
  h(i&) = i&
  t& = t& + h(i&)
NEXT i&
PRINT #1, t&
PRINT #1, LBOUND(h); UBOUND(h)
ERASE h

' ---- VIRTUAL: 200000 bytes in EMS ------------------------------------------
e1& = FRE(-11)
PRINT #1, e1& > 0
DIM VIRTUAL v(1 TO 50000) AS LONG
e2& = FRE(-11)
PRINT #1, e1& - e2& >= 200000  ' the allocation shows up in the EMS pool
v(1) = 42
v(4096) = 4096
v(4097) = 4097
v(25000) = 7654321
v(50000) = 50
PRINT #1, v(1); v(4096); v(4097); v(25000); v(50000)
s& = 0
FOR i& = 24998 TO 25002
  v(i&) = i& * 2
  s& = s& + v(i&)
NEXT i&
PRINT #1, s&
PRINT #1, LBOUND(v); UBOUND(v)
ERASE v

' ---- ABSOLUTE: text screen memory at B800 ----------------------------------
DIM DYNAMIC ab(0 TO 7) AT &HB800
ab(0) = 16706      ' "AB" with attributes... actually raw cell values
ab(7) = 12345
PRINT #1, ab(0); ab(7)
ab(0) = 1843       ' restore something printable-ish; values just round-trip
PRINT #1, ab(0)

' ---- REDIM PRESERVE ---------------------------------------------------------
REDIM p$(1 TO 3)
p$(1) = "one"
p$(2) = "two"
p$(3) = "three"
REDIM PRESERVE p$(1 TO 5)
p$(5) = "five"
PRINT #1, p$(1); p$(2); p$(3); "["; p$(4); "]"; p$(5)
REDIM PRESERVE p$(1 TO 2)
PRINT #1, p$(1); p$(2)
PRINT #1, UBOUND(p$)

REDIM n%(1 TO 4)
FOR i% = 1 TO 4
  n%(i%) = i% * 10
NEXT i%
REDIM PRESERVE n%(1 TO 6)
PRINT #1, n%(1); n%(4); n%(5); n%(6)

CLOSE #1
END
