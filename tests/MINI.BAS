' =============================================================================
' MINI.BAS - self-contained TESTLIB suite exercising strings, procedures and
' arrays through the PB-SvgaLibrary test harness. The driver at the bottom
' writes the results to UNITTEST.LOG.
' =============================================================================
$INCLUDE "TESTLIB.BI"

SUB Test_StringBasics
  DIM a AS STRING, b AS STRING
  CALL Test_Case("StringBasics")
  a = "Hello"
  b = a + ", " + "World!"
  CALL Assert_AreEqualStr(b, "Hello, World!", "concat")
  CALL Assert_IsEqual(LEN(b), 13, "length")
  CALL Assert_AreEqualStr(UCASE$(LEFT$(b, 5)), "HELLO", "left and ucase")
  CALL Assert_AreEqualStr(LCASE$(RIGHT$(b, 6)), "world!", "right and lcase")
  CALL Assert_AreEqualStr(MID$(b, 8, 5), "World", "mid")
  CALL Assert_IsEqual(INSTR(b, "World"), 8, "instr")
  CALL Assert_IsEqual(ASC(MID$(b, 1, 1)), 72, "asc")
  CALL Assert_AreEqualStr(STR$(42), " 42", "str of int")
  CALL Assert_IsEqual(VAL("123"), 123, "val")
  CALL Assert_IsEmpty("", "empty literal")
  CALL Assert_AreEqualStr(LTRIM$("  x "), "x ", "ltrim")
  CALL Assert_AreEqualStr(RTRIM$("  x "), "  x", "rtrim")
  CALL Assert_AreEqualStr(STRING$(3, "ab"), "aaa", "string fill")
  CALL Assert_IsTrue("apple" < "banana", "ordering")
END SUB

FUNCTION SumTo&(BYVAL n AS INTEGER)
  IF n <= 0 THEN
    SumTo& = 0
  ELSE
    SumTo& = SumTo&(n - 1) + n
  END IF
END FUNCTION

FUNCTION Bump%
  STATIC c AS INTEGER
  c = c + 1
  Bump% = c
END FUNCTION

FUNCTION Wrap$(s AS STRING)
  Wrap$ = "[" + s + "]"
END FUNCTION

SUB Poke2(x AS INTEGER, BYVAL y AS INTEGER)
  x = x + y
END SUB

SUB Test_Procs
  DIM total AS LONG, v AS INTEGER
  CALL Test_Case("Procs")
  total = SumTo&(10)
  CALL Assert_IsEqual(total, 55, "recursion")
  CALL Assert_IsEqual(Bump%, 1, "static first")
  CALL Assert_IsEqual(Bump%, 2, "static second")
  CALL Assert_AreEqualStr(Wrap$("x"), "[x]", "string function")
  v = 40
  CALL Poke2(v, 2)
  CALL Assert_IsEqual(v, 42, "byref mutation")
END SUB

SUB Test_Arrays
  DIM m(1 TO 2, 1 TO 3) AS INTEGER
  DIM i AS INTEGER, j AS INTEGER, sum AS INTEGER
  CALL Test_Case("Arrays")
  FOR i = 1 TO 2
    FOR j = 1 TO 3
      m(i, j) = i * j
    NEXT j
  NEXT i
  sum = 0
  FOR i = 1 TO 2
    FOR j = 1 TO 3
      sum = sum + m(i, j)
    NEXT j
  NEXT i
  CALL Assert_IsEqual(sum, 18, "static array sum")
  REDIM d(1 TO 4) AS LONG
  CALL Assert_IsEqual(LBOUND(d), 1, "lbound")
  CALL Assert_IsEqual(UBOUND(d), 4, "ubound")
  d(3) = 3000
  CALL Assert_IsEqual(d(3), 3000, "dynamic element")
  CALL Assert_IsZero(d(1), "fresh element is zero")
END SUB

' ---------------------------- driver -----------------------------------------
KILL "UNITTEST.LOG"
CALL Test_BeginSuite("MINI")
CALL Test_StringBasics
CALL Test_Procs
CALL Test_Arrays
CALL Test_EndSuite("MINI")
PRINT "MINI DONE"
END
