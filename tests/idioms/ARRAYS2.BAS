' ARRAY SORT and ARRAY SCAN over each numeric element kind: signed, unsigned and real, two to ten bytes
DIM u(4) AS WORD, l(4) AS LONG, q(4) AS QUAD, sg(4) AS SINGLE, e(4) AS EXT, by(4) AS BYTE, dw(4) AS DWORD
FOR i% = 0 TO 4
  READ v&
  u(i%) = v& AND &HFFFF&: l(i%) = v& * 1000: q(i%) = v& * 1000000000: sg(i%) = v& / 4: e(i%) = v& / 3
  by(i%) = v& AND 255: dw(i%) = v& * 7
NEXT
DATA 40000, 7, 300, 65535, 12
ARRAY SORT u(): ARRAY SORT l(), DESCEND: ARRAY SORT q(): ARRAY SORT sg(): ARRAY SORT e(), DESCEND
ARRAY SORT by(): ARRAY SORT dw()
FOR i% = 0 TO 4: PRINT u(i%); l(i%); q(i%); sg(i%); by(i%); dw(i%): PRINT e(i%): NEXT
ARRAY SCAN u(), = 65535, TO k%
PRINT k%
ARRAY SCAN q(), <> 7000000000, TO k%
PRINT k%
