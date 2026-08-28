' Differential battery 118: the DATA pool - where its items come from, where a label points,
' and what happens at the end of it. Verified against genuine PBC 3.50.
'
' DATA is not executable, so a DATA statement written inside an IF, FOR or SELECT block still
' contributes to the module's one pool, in source order, and a label written there still names
' the offset it stands at. The direct emitter used to walk only the top level of the module
' body, so every nested item was silently absent: this program's first READ answered 1 2 9 11
' where PBC answers 1 2 7 8, and RESTORE Inner was refused outright as unsupported.
'
' Running off the end is PB error 4, and the target keeps the value it already had - which is
' what the last block reads back. The routed lowering had no bound at all and walked into
' whatever global followed the pool, taking two of its bytes as an item length.
DIM x AS INTEGER
DIM a AS INTEGER, b AS INTEGER, c AS INTEGER, d AS INTEGER
OPEN "RESULT.TXT" FOR OUTPUT AS #1

DATA 1, 2
x = 0
IF x = 0 THEN
Inner:
  DATA 7, 8
END IF
FOR x = 1 TO 1
  DATA 9
NEXT x
SELECT CASE x
  CASE 2
    DATA 11
END SELECT

READ a, b, c, d
PRINT #1, "seq"; a; b; c; d
READ a, b
PRINT #1, "rest"; a; b
RESTORE Inner
READ a, b
PRINT #1, "lbl"; a; b
RESTORE
READ a
PRINT #1, "top"; a

' past the end: error 4, and c is left holding what the READ before it stored
ON ERROR GOTO Trap
RESTORE
FOR x = 1 TO 7
  READ c
NEXT x
PRINT #1, "after"; c
CLOSE #1
END

Trap:
  PRINT #1, "err"; ERR
  RESUME NEXT
