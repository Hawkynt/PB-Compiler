' @title: Compound assignment operators
' @desc:  `target OP= value` desugars in the parser to `target = target OP value`.
DIM N AS INTEGER
N = 1
N += 4
N *= 3
DIM S AS STRING
S = "a"
S &= "bc"
PRINT N; S
