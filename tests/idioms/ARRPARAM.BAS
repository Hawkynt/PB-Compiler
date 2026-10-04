' arrays passed to procedures: static and dynamic, numeric and string, two dimensions, forwarded on,
' and REDIMmed or ERASEd by the callee on the caller's behalf
DECLARE FUNCTION Total&(a%())
DECLARE SUB Fill(a%(), v%)
DECLARE SUB Grow(a%())
DECLARE SUB Keep(a%())
DECLARE SUB Shout(s$())
DECLARE SUB Forward(a%())
DECLARE FUNCTION Corner#(m#())

DIM st%(1 TO 5)
Fill st%(), 3
PRINT Total&(st%()); LBOUND(st%); UBOUND(st%)

DIM dy%(0)
REDIM dy%(-2 TO 2)
Fill dy%(), 7
PRINT Total&(dy%())
Forward dy%()
PRINT Total&(dy%()); dy%(-2); dy%(2)

Grow dy%()
PRINT LBOUND(dy%); UBOUND(dy%); dy%(9); Total&(dy%())
Keep dy%()
PRINT LBOUND(dy%); UBOUND(dy%); dy%(1); dy%(6)

DIM w$(2)
w$(0) = "alpha": w$(1) = "beta": w$(2) = "gamma"
Shout w$()
PRINT w$(0); " "; w$(1); " "; w$(2)

DIM m#(1 TO 3, 0 TO 4)
m#(3, 4) = 2.5: m#(1, 0) = -1
PRINT Corner#(m#())

FUNCTION Total&(a%())
  t& = 0
  FOR i% = LBOUND(a%) TO UBOUND(a%)
    t& = t& + a%(i%)
  NEXT
  Total& = t&
END FUNCTION

SUB Fill(a%(), v%)
  FOR i% = LBOUND(a%) TO UBOUND(a%)
    a%(i%) = v% * i%
  NEXT
END SUB

SUB Forward(a%())
  Fill a%(), 10
END SUB

SUB Grow(a%())
  REDIM a%(1 TO 9)
  a%(9) = 42
END SUB

SUB Keep(a%())
  a%(1) = 11
  REDIM PRESERVE a%(1 TO 6)
  a%(6) = 66
END SUB

SUB Shout(s$())
  FOR i% = LBOUND(s$) TO UBOUND(s$)
    s$(i%) = UCASE$(s$(i%)) + "!"
  NEXT
END SUB

FUNCTION Corner#(m#())
  Corner# = m#(UBOUND(m#, 1), UBOUND(m#, 2)) + m#(LBOUND(m#, 1), LBOUND(m#, 2)) * 10
END FUNCTION
