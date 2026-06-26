' @title: Inline lambda bound to a typed procedure pointer
' @desc:  A lambda lifts to an anonymous top-level FUNCTION; its value is a code pointer.
DIM square AS FUNCTION(LONG) AS LONG
square = FUNCTION(BYVAL x AS LONG) AS LONG => x * x
PRINT square(9)
