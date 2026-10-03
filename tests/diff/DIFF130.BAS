' Differential battery 130: counted loops that do NOT run their full count.
'
' A FOR loop whose bounds are literals has a trip count the compiler can work out, and two
' optimizations rest on that number: an accumulator's final value is start + step * trips, and a
' loop nothing reads the result of can be deleted. Both are answers about a loop that ran every
' iteration. An EXIT FOR, a GOTO out of the body or an EXIT SUB stops it early, and then the trip
' count is an upper bound rather than the number of times the body ran.
'
' Every case below leaves its loop early and prints what the accumulator and the counter hold
' afterwards, so a closed form computed from the full count prints a number genuine PBC 3.50 does
' not. It also prints the loops that DO run to completion beside them, because the fix must not be
' "stop optimizing loops".
DECLARE FUNCTION Feed%(BYVAL k%)
DECLARE SUB Walk(BYVAL cap%, r&)
DIM i AS INTEGER, j AS INTEGER, acc AS LONG, r AS LONG
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' EXIT FOR on the fifth iteration: four increments happened, the counter stands at 5
acc = 0
FOR i = 1 TO 8
  IF Feed%(i) > 4 THEN EXIT FOR
  acc = acc + 1
NEXT i
PRINT #1, "exitfor"; acc; i

' the same loop with a step, so the counter lands where the step put it
acc = 0
FOR i = 0 TO 20 STEP 4
  IF Feed%(i) > 9 THEN EXIT FOR
  acc = acc + 2
NEXT i
PRINT #1, "exitstep"; acc; i

' a downward loop left early
acc = 0
FOR i = 10 TO 1 STEP -1
  IF Feed%(i) < 7 THEN EXIT FOR
  acc = acc + 3
NEXT i
PRINT #1, "exitdown"; acc; i

' a GOTO out of the body - the same escape in different syntax
acc = 0
FOR i = 1 TO 12
  IF Feed%(i) > 3 THEN GOTO AfterLoop
  acc = acc + 5
NEXT i
AfterLoop:
PRINT #1, "goto"; acc; i

' an EXIT SUB out of a loop inside a procedure
r = 0
Walk Feed%(6), r
PRINT #1, "exitsub"; r
r = 0
Walk Feed%(99), r
PRINT #1, "exitsub2"; r

' the test never fires, so the loop DOES run its full count and the closed form is right
acc = 0
FOR i = 1 TO 8
  IF Feed%(i) > 100 THEN EXIT FOR
  acc = acc + 1
NEXT i
PRINT #1, "never"; acc; i

' the test fires immediately: no increment at all
acc = 0
FOR i = 1 TO 8
  IF Feed%(i) > 0 THEN EXIT FOR
  acc = acc + 1
NEXT i
PRINT #1, "atonce"; acc; i

' a plain counted accumulator beside them, which the closed form may still take
acc = 0
FOR i = 1 TO 30
  acc = acc + 7
NEXT i
PRINT #1, "plain"; acc; i

' an inner loop left early inside an outer one that runs to completion
acc = 0
FOR j = 1 TO 3
  FOR i = 1 TO 6
    IF Feed%(i) > j THEN EXIT FOR
    acc = acc + 10
  NEXT i
  acc = acc + i
NEXT j
PRINT #1, "nested"; acc; j

' a loop with no result anyone reads, left early: deleting it would drop the calls
FOR i = 1 TO 9
  IF Feed%(i) > 2 THEN EXIT FOR
NEXT i
PRINT #1, "empty"; i

CLOSE #1
END

FUNCTION Feed%(BYVAL k%)
  Feed% = k%
END FUNCTION

SUB Walk(BYVAL cap%, r&)
  DIM k AS INTEGER
  FOR k = 1 TO 10
    IF k > cap% THEN EXIT SUB
    r& = r& + 100
  NEXT k
  r& = r& - 1
END SUB
