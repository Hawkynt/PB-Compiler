OPEN "RESULT.TXT" FOR OUTPUT AS #1
s$ = "Narrow LONG arithmetic, proven in range!"
FOR i% = 1 TO LEN(s$) STEP 3
  a& = ASC(MID$(s$, i%, 1))
  b& = (a& MOD 9) + 1
  c& = -a&
  PRINT #1, a& \ b&; a& MOD b&; c& \ b&; c& MOD b&; c& \ 7; c& MOD 7;
  PRINT #1, (a& < b&); (c& < b&); (a& = b& * 5); (c& >= -100)
NEXT i%
d& = LEN(s$)
PRINT #1, d& \ 3; d& MOD 3; (d& > 40); (d& <= 41)
CLOSE #1
SYSTEM
