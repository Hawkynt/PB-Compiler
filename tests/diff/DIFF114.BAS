' Differential battery 114: DIM ... AT segment - an ABSOLUTE array is a VIEW of
' memory the program does not own, here the text screen at B800. Everything is
' written through the array and read back with PEEK, which reaches the same
' bytes by a different route (DEF SEG plus a byte read), so a store that landed
' in the program's own data instead of the named segment reads back as zero.
DIM DYNAMIC vid%(0 TO 7) AT &HB800

vid%(0) = 16706          ' 42h 41h - "AB" as the two bytes of one text cell
vid%(7) = 12345          ' the last element, so the extent is exercised too
FOR i% = 1 TO 6
  vid%(i%) = i% * 3 - 1  ' a subscript that is not a compile-time constant
NEXT i%

DEF SEG = &HB800
PRINT PEEK(0); PEEK(1); PEEK(2); PEEK(3); PEEK(14); PEEK(15)
DEF SEG

PRINT vid%(0); vid%(7)
FOR i% = 1 TO 6
  PRINT vid%(i%);
NEXT i%
PRINT
PRINT LBOUND(vid%); UBOUND(vid%)

' a second view of the same bytes with a different lower bound: alt%(4) is the
' element at offset 0, which is vid%(0) - so the two must disagree about names
' and agree about memory
DIM DYNAMIC alt%(4 TO 7) AT &HB800
PRINT alt%(4); alt%(7)
alt%(4) = 999
PRINT vid%(0)
END
