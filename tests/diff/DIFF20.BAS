' Differential battery 20: runtime completeness - FIELD buffers on RANDOM
' files with LSET/RSET and bare GET/PUT, ERL line bookkeeping, the $STRING
' length limit (Error 14), and the documented string-manager asm ABI
' (GetStrLoc: push handle, call -> DX:AX = data pointer, CX = length).
$STRING 8
DECLARE FUNCTION GetStrLoc(BYVAL handle AS INTEGER) AS DWORD
OPEN "RESULT.TXT" FOR OUTPUT AS #1

' ---- FIELD + LSET/RSET + PUT/GET round trip ---------------------------------
OPEN "FLD.DAT" FOR RANDOM AS #2 LEN = 16
FIELD #2, 6 AS f1$, 10 AS f2$
LSET f1$ = "abc"
RSET f2$ = "42"
PUT #2, 1
LSET f1$ = "zzzzzzz"
LSET f2$ = "wiped"
PUT #2, 2
GET #2, 1
PRINT #1, "["; f1$; "]["; f2$; "]"
GET #2, 2
PRINT #1, "["; f1$; "]["; f2$; "]"
CLOSE #2
KILL "FLD.DAT"

' ---- ERL: numeric line labels are tracked, alpha labels are not -------------
ON ERROR GOTO TrapE
e% = 0
l% = 0
100 ERROR 5
AfterE:
PRINT #1, e%; l%
ON ERROR GOTO 0

' ---- $STRING 8: strings cap at 8174 usable bytes -> Error 14 ----------------
ON ERROR GOTO TrapS
e% = 0
ok$ = STRING$(8174, "x")
PRINT #1, LEN(ok$)
big$ = STRING$(8175, "x")
AfterS:
PRINT #1, e%; LEN(big$)
ON ERROR GOTO 0

' ---- GetStrLoc from inline assembly (manual ABI) ----------------------------
a$ = "XYZZY"
r% = 0
c% = 0
! push Word Ptr a$        ; string handle on the stack
! call GetStrLoc          ; DX:AX = far data pointer, CX = length
! mov  ES, DX
! mov  BX, AX
! mov  AL, ES:[BX]        ; first character
! xor  AH, AH
! mov  r%, AX
! mov  c%, CX
PRINT #1, r%; c%

CLOSE #1
END

TrapE:
e% = ERR
l% = ERL
RESUME AfterE

TrapS:
e% = ERR
RESUME AfterS
