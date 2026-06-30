' @title: Capturing lambda (stack closure)
' @desc:  A lambda references an outer local; the capture is reached by reference through the closure env pointer.
SUB Demo
  DIM base AS LONG
  base = 10
  DIM add AS FUNCTION(LONG) AS LONG
  add = FUNCTION(BYVAL x AS LONG) AS LONG => x + base
  PRINT add(5)
END SUB
Demo
