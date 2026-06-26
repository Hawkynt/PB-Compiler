' @title: Nested local SUB/FUNCTION (stack capture)
' @desc:  A nested proc references an enclosing local; the capture is a hidden BYREF parameter.
FUNCTION Outer(n AS INTEGER) AS INTEGER
  DIM total AS INTEGER
  SUB Bump
    total = total + n
  END SUB
  Bump
  Bump
  Outer = total
END FUNCTION
PRINT Outer(7)
