TYPE T
  f AS INTEGER
  g AS SINGLE
  h AS LONG
END TYPE
DIM z%(0 TO 3), q&(1 TO 2), r AS T
OPEN "RESULT.TXT" FOR OUTPUT AS #1
z%(1) = 3
INCR z%(1), 5
DECR z%(1)
i% = 2
INCR z%(i%), z%(1)
q&(2) = 70000
INCR q&(2), 70000
r.f = 1
INCR r.f, 2
r.h = -5
DECR r.h, 100000
x! = 1.5
INCR x!
DECR x!, .25
r.g = 2
INCR r.g, 1.5
d# = 10
DECR d#, 2.5
PRINT #1, z%(1); z%(2); q&(2); r.f; r.h; x!; r.g; d#
CLOSE #1
SYSTEM
