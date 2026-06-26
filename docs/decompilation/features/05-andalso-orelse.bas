' @title: Short-circuit ANDALSO / ORELSE
' @desc:  `a ANDALSO b` -> `IF(a, b<>0, 0)`, `a ORELSE b` -> `IF(a, -1, b<>0)`; b is skipped when known.
DIM A AS INTEGER
DIM B AS INTEGER
A = 0
B = 5
PRINT (A <> 0) ANDALSO (B \ A = 0)
PRINT (B <> 0) ORELSE (B \ A = 0)
