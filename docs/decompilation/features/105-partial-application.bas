' @title: Partial application + composition
' @desc:  BIND(f, consts...) pre-fills leading parameters; COMPOSE(f, g) builds h(x) = g(f(x)); both lower to thunk FUNCTIONs addressed via CODEPTR32.
DECLARE FUNCTION Add(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG
DECLARE FUNCTION Twice(BYVAL x AS LONG) AS LONG
DECLARE FUNCTION Inc2(BYVAL x AS LONG) AS LONG
DIM add5 AS FUNCTION(LONG) AS LONG
add5 = BIND(Add, 5)
DIM h AS FUNCTION(LONG) AS LONG
h = COMPOSE(Twice, Inc2)
PRINT add5(10); h(20)
FUNCTION Add(BYVAL a AS LONG, BYVAL b AS LONG) AS LONG
  Add = a + b
END FUNCTION
FUNCTION Twice(BYVAL x AS LONG) AS LONG
  Twice = x * 2
END FUNCTION
FUNCTION Inc2(BYVAL x AS LONG) AS LONG
  Inc2 = x + 1
END FUNCTION
