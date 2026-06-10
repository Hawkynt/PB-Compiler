' SUB/FUNCTION calling conventions: BYREF mutation, BYVAL isolation,
' recursion, STATIC accumulation, string results, EXIT FUNCTION.
DIM a AS INTEGER, b AS INTEGER
a = 1 : b = 1
CALL Mutate(a, b)
PRINT a; b
PRINT Fact&(6)
PRINT Counter%; Counter%; Counter%
PRINT Greet$("WORLD")
PRINT Clip%(5); Clip%(99)
END

SUB Mutate(x AS INTEGER, BYVAL y AS INTEGER)
  x = x + 41
  y = y + 41
END SUB

FUNCTION Fact&(BYVAL n AS INTEGER)
  IF n <= 1 THEN
    Fact& = 1
  ELSE
    Fact& = Fact&(n - 1) * n
  END IF
END FUNCTION

FUNCTION Counter%
  STATIC c AS INTEGER
  c = c + 1
  Counter% = c
END FUNCTION

FUNCTION Greet$(name AS STRING)
  Greet$ = "HELLO, " + name + "!"
END FUNCTION

FUNCTION Clip%(BYVAL v AS INTEGER)
  IF v > 10 THEN
    Clip% = 10
    EXIT FUNCTION
  END IF
  Clip% = v + 1
END FUNCTION
