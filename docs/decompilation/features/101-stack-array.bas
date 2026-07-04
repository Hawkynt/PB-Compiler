' @title: Stack arrays
' @desc:  DIM STACK places a fixed-size local array in the procedure frame - reentrant scratch storage, freed on return; decompiles as a plain DIM.
SUB Work
  DIM STACK a(1 TO 5) AS INTEGER
  DIM i AS INTEGER
  FOR i = 1 TO 5
    a(i) = i * i
  NEXT
  PRINT a(1); a(5)
END SUB
Work
