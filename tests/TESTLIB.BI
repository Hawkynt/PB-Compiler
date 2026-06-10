' =============================================================================
' TESTLIB.BI - xUnit-style test framework for the SVGA library suites
' =============================================================================
' Each <MODULE>.BAS suite is a self-contained PowerBASIC program: it $INCLUDEs
' this file plus the module(s) under test, defines one SUB per test case in the
' Arrange-Act-Assert shape, and a main that opens the suite, calls each test and
' closes it. Results are appended to UNITTEST.LOG in a parseable form that
' scripts/run-pb-tests.sh renders as an NUnit/xUnit-style summary:
'   [SUITE]  <name>
'     [PASS] <case> :: <assertion>
'     [FAIL] <case> :: <assertion> (expected=.. actual=..)
'   [RESULT] <name> assertions=N failed=M
'
' A suite normally needs NO manual entry point: scripts/run-pb-tests.sh scans the
' file for SUB Test_* and generates the driver (suite name = file name), running
' an optional SUB Test_Setup first and SUB Test_Teardown last. A suite that wants
' full control can still write its own top-level driver - if it calls
' Test_BeginSuite the runner leaves it untouched (e.g. VESAOPT.BAS).
'
' Lifecycle:  Test_BeginSuite -> (Test_Case + Assert_*)* -> Test_EndSuite
' Assertions: Assert_IsTrue / Assert_IsFalse / Assert_IsEqual / Assert_AreNotEqual
'             Assert_IsZero / Assert_IsNotZero
'             Assert_AreEqualStr / Assert_IsEmpty / Assert_IsNotEmpty
'             Assert_IsWhiteSpaceOrEmpty
'
' Every line is written open-append-close so a test that crashes or hangs mid-run
' (e.g. a primitive with an infinite loop) still leaves the results up to that
' point on disk - the missing [RESULT] line then makes the run fail loudly
' instead of silently "passing" with an empty log.
' =============================================================================
DIM tCount AS SHARED INTEGER       ' assertions made in the current suite
DIM tFail  AS SHARED INTEGER       ' assertions failed in the current suite
DIM tCaseName AS SHARED STRING     ' current test-case name

SUB Test_BeginSuite(suiteName AS STRING)
    OPEN "UNITTEST.LOG" FOR APPEND AS #9
    PRINT #9, "[SUITE] "; suiteName
    CLOSE #9
    tCount = 0 : tFail = 0
END SUB

SUB Test_Case(caseName AS STRING)
    tCaseName = caseName
END SUB

' --- internal: record one assertion result (PASS/FAIL) to the log --------------
SUB Test_Record(BYVAL ok AS INTEGER, what AS STRING, detail AS STRING)
    tCount = tCount + 1
    OPEN "UNITTEST.LOG" FOR APPEND AS #9
    IF ok <> 0 THEN
        PRINT #9, "  [PASS] "; tCaseName; " :: "; what
    ELSE
        tFail = tFail + 1
        PRINT #9, "  [FAIL] "; tCaseName; " :: "; what; detail
    END IF
    CLOSE #9
END SUB

' record an honestly-skipped block (e.g. a video mode this emulator lacks);
' the runner reports skips in the summary but does not fail on them
SUB Test_Skip(what AS STRING)
    OPEN "UNITTEST.LOG" FOR APPEND AS #9
    PRINT #9, "  [SKIP] "; what
    CLOSE #9
END SUB

SUB Test_EndSuite(suiteName AS STRING)
    OPEN "UNITTEST.LOG" FOR APPEND AS #9
    PRINT #9, "[RESULT] "; suiteName; " assertions="; tCount; " failed="; tFail
    CLOSE #9
END SUB

' --- boolean assertions --------------------------------------------------------
SUB Assert_IsTrue(BYVAL cond AS INTEGER, what AS STRING)
    CALL Test_Record(cond <> 0, what, "")
END SUB

SUB Assert_IsFalse(BYVAL cond AS INTEGER, what AS STRING)
    CALL Test_Record(cond = 0, what, "")
END SUB

' --- numeric assertions (WORD/BYTE/INTEGER widen to LONG) -----------------------
SUB Assert_IsEqual(BYVAL actual AS LONG, BYVAL expected AS LONG, what AS STRING)
    CALL Test_Record(actual = expected, what, " (expected=" + STR$(expected) + " actual=" + STR$(actual) + ")")
END SUB

SUB Assert_AreNotEqual(BYVAL actual AS LONG, BYVAL notExpected AS LONG, what AS STRING)
    CALL Test_Record(actual <> notExpected, what, " (did not expect=" + STR$(notExpected) + ")")
END SUB

SUB Assert_IsZero(BYVAL valu AS LONG, what AS STRING)
    CALL Test_Record(valu = 0, what, " (actual=" + STR$(valu) + ")")
END SUB

SUB Assert_IsNotZero(BYVAL valu AS LONG, what AS STRING)
    CALL Test_Record(valu <> 0, what, " (actual=" + STR$(valu) + ")")
END SUB

' --- string assertions ---------------------------------------------------------
SUB Assert_AreEqualStr(actual AS STRING, expected AS STRING, what AS STRING)
    CALL Test_Record(actual = expected, what, " (expected=[" + expected + "] actual=[" + actual + "])")
END SUB

SUB Assert_IsEmpty(s AS STRING, what AS STRING)
    CALL Test_Record(LEN(s) = 0, what, " (len=" + STR$(LEN(s)) + ")")
END SUB

SUB Assert_IsNotEmpty(s AS STRING, what AS STRING)
    CALL Test_Record(LEN(s) > 0, what, "")
END SUB

SUB Assert_IsWhiteSpaceOrEmpty(s AS STRING, what AS STRING)
    DIM i AS INTEGER, ch AS INTEGER, allWs AS INTEGER
    allWs = 1
    FOR i = 1 TO LEN(s)
        ch = ASC(MID$(s, i, 1))
        IF ch <> 32 AND ch <> 9 AND ch <> 13 AND ch <> 10 THEN allWs = 0
    NEXT i
    CALL Test_Record(allWs, what, " (len=" + STR$(LEN(s)) + ")")
END SUB
