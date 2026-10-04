' Differential battery 132: an array parameter's rank is the body's to say. SUB s(m#()) takes an
' array of any shape; the body indexing it with two subscripts makes it two-dimensional. Genuine
' PBC 3.50 answers -7.5 and 34; the parameter was bound one-dimensional and the program refused.
DECLARE FUNCTION Corner#(m#())
DECLARE SUB Mark(c%())
OPEN "RESULT.TXT" FOR OUTPUT AS #1
DIM m#(1 TO 3, 0 TO 4)
m#(3, 4) = 2.5: m#(1, 0) = -1
PRINT #1, Corner#(m#())
DIM c%(2, 1 TO 3, 4)
Mark c%()
PRINT #1, c%(1, 2, 3) + UBOUND(c%, 3) * 0
CLOSE #1

FUNCTION Corner#(m#())
  Corner# = m#(UBOUND(m#, 1), UBOUND(m#, 2)) + m#(LBOUND(m#, 1), LBOUND(m#, 2)) * 10
END FUNCTION

SUB Mark(c%())
  c%(1, 2, 3) = 30 + UBOUND(c%, 3)
END SUB
