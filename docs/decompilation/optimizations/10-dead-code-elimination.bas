' @title: Dead-code elimination (O2)
' @desc:  Statements after an unconditional GOTO (until the next label) are unreachable; the optimizer drops them.
DIM X AS INTEGER
X = 1
GOTO Finish
X = 999
PRINT "never runs"
Finish:
PRINT X
