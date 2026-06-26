' @title: WITH block
' @desc:  Leading `.member` inside WITH rewrites to member access on the subject.
TYPE Point
  X AS INTEGER
  Y AS INTEGER
END TYPE
DIM P AS Point
WITH P
  .X = 3
  .Y = 4
END WITH
PRINT P.X; P.Y
