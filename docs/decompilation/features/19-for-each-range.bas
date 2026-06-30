' @title: FOR EACH over a bracketed range
' @desc:  FOR EACH v IN [lo..hi] desugars straight to a counted FOR v = lo TO hi (no array, no index temp).
DIM v AS INTEGER
FOR EACH v IN [10..13]
  PRINT v
NEXT
