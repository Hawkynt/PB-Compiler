' @title: Typed procedure pointer (fat delegate)
' @desc:  DIM a FUNCTION-pointer variable, assign a named procedure's address, call through it.
DECLARE FUNCTION Triple&(BYVAL n AS LONG)
DIM f AS FUNCTION(LONG) AS LONG
f = CODEPTR32(Triple&)
PRINT f(8)
FUNCTION Triple&(BYVAL n AS LONG)
  Triple& = n * 3
END FUNCTION
