' @title: TYPE operator overload
' @desc:  OPERATOR + (rhs) lifts to op_Add; THIS is the left operand, RESULT holds the result.
TYPE Vec
  X AS LONG
  OPERATOR + (o AS Vec) AS Vec
    RESULT.X = THIS.X + o.X
  END OPERATOR
END TYPE
DIM p AS Vec, q AS Vec, s AS Vec
p.X = 3 : q.X = 4
s = p + q
PRINT s.X
