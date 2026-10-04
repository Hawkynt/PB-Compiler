' records: TYPE and UNION, nested records, array fields, record parameters BYREF, record arrays and SWAP
TYPE Pt
  x AS INTEGER
  y AS INTEGER
END TYPE
TYPE Rec
  nm AS STRING * 8
  p AS Pt
  v(1 TO 3) AS DOUBLE
  f AS SINGLE
END TYPE
UNION Both
  l AS LONG
  w(1) AS WORD
END UNION
DIM r AS Rec, rs(2) AS Rec, u AS Both
r.nm = "abc": r.p.x = 3: r.p.y = -4: r.v(2) = 1.5: r.f = 2.25
rs(1) = r
rs(1).p.y = 9
PRINT "["; rs(1).nm; "]"; rs(1).p.x; rs(1).p.y; rs(1).v(2); rs(1).f; r.p.y; LEN(r)
u.l = &H12345678
PRINT HEX$(u.w(0)); " "; HEX$(u.w(1))
SUB Move(q AS Pt, BYVAL dx%)
  q.x = q.x + dx%
END SUB
Move r.p, 10
PRINT r.p.x
FUNCTION Dist&(a AS Pt)
  Dist& = a.x * a.x + a.y * a.y
END FUNCTION
PRINT Dist&(r.p)
SWAP rs(0), rs(1)
PRINT rs(0).p.y; rs(1).p.y
