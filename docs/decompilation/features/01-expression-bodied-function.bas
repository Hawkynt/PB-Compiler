' @title: Expression-bodied FUNCTION
' @desc:  `FUNCTION F(...) [AS T] = expr` desugars to a single result-assignment body.
DECLARE FUNCTION Square%(BYVAL N%)
PRINT Square%(7)
FUNCTION Square%(BYVAL N%) = N% * N%
