' Heap churn: ~800 KiB of temporaries through the 64 KiB string heap forces
' repeated compactions; long-lived strings must survive relocation.
DIM keep AS STRING, t AS STRING, i AS INTEGER
keep = "KEEP-" + STRING$(100, "K")
FOR i = 1 TO 2000
  t = STRING$(100, "x") + STRING$(100, "y")
NEXT i
PRINT LEFT$(keep, 8); LEN(keep)
PRINT MID$(t, 100, 2); LEN(t)
END
