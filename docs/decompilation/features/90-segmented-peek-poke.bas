' @title: Segmented PEEK / POKE
' @desc:  POKE seg:offset / PEEK(seg:offset) lower to DEF SEG = seg followed by the plain PEEK/POKE.
POKE &H4000:100, 65
DIM v AS INTEGER
v = PEEK(&H4000:100)
PRINT v
