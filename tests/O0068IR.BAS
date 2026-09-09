$DYNAMIC
DIM i AS INTEGER
DIM a%(1 TO 4)
FOR i = 1 TO 4
  a%(i) = i * 3
NEXT i
PRINT a%(1); a%(4)
END
