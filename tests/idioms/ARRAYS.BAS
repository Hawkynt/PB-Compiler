' ARRAY SORT and ARRAY SCAN over every element kind, with ranges, DESCEND, TAGARRAY and FROM/TO
DIM a(1 TO 8) AS INTEGER
FOR i% = 1 TO 8: READ a(i%): NEXT
DATA 5, 3, 9, 1, 7, 2, 8, 6
ARRAY SORT a()
FOR i% = 1 TO 8: PRINT a(i%);: NEXT: PRINT
ARRAY SORT a(), DESCEND
FOR i% = 1 TO 8: PRINT a(i%);: NEXT: PRINT
ARRAY SCAN a(), = 7, TO k%
PRINT k%
ARRAY SCAN a(3), < 5, TO k%
PRINT k%
ARRAY SCAN a(), > 100, TO k%
PRINT k%
ARRAY SORT a(3) FOR 4
FOR i% = 1 TO 8: PRINT a(i%);: NEXT: PRINT

DIM s$(3)
s$(0) = "pear": s$(1) = "Apple": s$(2) = "fig": s$(3) = "banana"
ARRAY SORT s$()
FOR i% = 0 TO 3: PRINT s$(i%); " ";: NEXT: PRINT
ARRAY SCAN s$(), = "fig", TO k%
PRINT k%
ARRAY SCAN s$(), >= "c", TO k%
PRINT k%
ARRAY SORT s$(), DESCEND
FOR i% = 0 TO 3: PRINT s$(i%); " ";: NEXT: PRINT

' sorting by the second and third characters only
DIM w$(4)
w$(0) = "xcz": w$(1) = "abq": w$(2) = "zaa": w$(3) = "mbb": w$(4) = "qca"
ARRAY SORT w$(), FROM 2 TO 3
FOR i% = 0 TO 4: PRINT w$(i%); " ";: NEXT: PRINT
ARRAY SCAN w$(), FROM 2 TO 2, = "c", TO k%
PRINT k%

' a key array carrying a tag array along, ties keeping their order
DIM ky(1 TO 6) AS DOUBLE, tg(1 TO 6) AS INTEGER
FOR i% = 1 TO 6: READ ky(i%): tg(i%) = i%: NEXT
DATA 2.5, -1.25, 2.5, 0, 1E10, -3.75
ARRAY SORT ky(), TAGARRAY tg()
FOR i% = 1 TO 6: PRINT ky(i%); tg(i%); "|";: NEXT: PRINT
ARRAY SORT ky(), DESCEND, TAGARRAY tg()
FOR i% = 1 TO 6: PRINT tg(i%);: NEXT: PRINT
ARRAY SCAN ky(), <= 0, TO k%
PRINT k%
