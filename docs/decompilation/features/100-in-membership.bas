' @title: IN membership test + bare range FOR EACH
' @desc:  x IN lo TO hi lowers to bounds comparisons, x IN {a, b, lo TO hi} to an OR chain; FOR EACH over a bare range needs no brackets.
DIM i AS INTEGER
FOR i = 1 TO 10
  IF i IN {1, 4, 7 TO 9} THEN PRINT i;
NEXT
PRINT
DIM v AS INTEGER
FOR EACH v IN 2 TO 8 STEP 3
  PRINT v;
NEXT
