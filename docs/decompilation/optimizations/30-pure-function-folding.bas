' @title: Pure-function folding (O25)
' @desc:  A pure FUNCTION called with constant arguments is evaluated at compile time; the call becomes its result literal.
FUNCTION Cube&(BYVAL n AS LONG)
  Cube& = n * n * n
END FUNCTION
PRINT Cube&(4)
