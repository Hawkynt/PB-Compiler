' Differential battery 38: O18 IPCP. A parameter that is the same constant at
' every call site and never written folds inside the callee (enabling dead-arm
' elimination) - observable output must be unchanged from a normal call.
DECLARE SUB Plot(BYVAL mode%, BYVAL v%)
DECLARE FUNCTION Scaled%(BYVAL bs%, BYVAL n%)
DECLARE SUB Vary(BYVAL mode%, BYVAL v%)
OPEN "RESULT.TXT" FOR OUTPUT AS #1
Plot 1, 10
Plot 1, 20
Plot 1, 30
PRINT #1, Scaled%(100, 3); Scaled%(100, 4)
Vary 1, 5
Vary 2, 6
CLOSE #1
END
SUB Plot(BYVAL mode%, BYVAL v%)
  ' mode% is always 1 -> the ELSE arm is dead after IPCP
  IF mode% = 1 THEN
    PRINT #1, "on"; v%
  ELSE
    PRINT #1, "off"; v%
  END IF
END SUB
FUNCTION Scaled%(BYVAL bs%, BYVAL n%)
  ' bs% is always 100
  Scaled% = bs% * n% + n%
END FUNCTION
SUB Vary(BYVAL mode%, BYVAL v%)
  ' mode% varies (1 then 2) -> NOT IPCP, both arms live
  IF mode% = 1 THEN
    PRINT #1, "v-on"; v%
  ELSE
    PRINT #1, "v-off"; v%
  END IF
END SUB
