' @title: TYPE method
' @desc:  A TYPE method lifts to a procedure taking the instance BYREF as THIS.
TYPE Counter
  Value AS LONG
  SUB Bump(BYVAL by AS LONG)
    THIS.Value = THIS.Value + by
  END SUB
END TYPE
DIM c AS Counter
c.Value = 10
c.Bump(5)
PRINT c.Value
