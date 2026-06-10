' =============================================================================
' WEIRD.BAS - battery for PowerBASIC 3.5's odder corners, beyond the corpus:
' $IF metastatement conditionals, equate expression folding, DEF FN, GOSUB
' inside SUBs, EXIT forms, ON n GOSUB/GOTO, unsigned WORD semantics, SWAP,
' INCR/DECR with amounts, string SELECT CASE, SHIFT/BIT, CHR$/ASC round trips.
' =============================================================================
$INCLUDE "TESTLIB.BI"

%WEIRD_ON = 1
%WEIRD_OFF = 0
%BASE = 7
%DERIVED = %BASE * 3 + 1    ' equate expression folding -> 22

DEF FNDouble%(x AS INTEGER) = x * 2

SUB Test_MetaConditionals
    DIM picked AS INTEGER
$IF %WEIRD_ON
    picked = 1
$ELSE
    picked = 2
$ENDIF
$IF %WEIRD_OFF
    picked = picked + 100
$ENDIF
    CALL Test_Case("$IF/$ELSE/$ENDIF with equates")
    CALL Assert_IsEqual(picked, 1, "live branch kept, dead branches dropped")
    CALL Assert_IsEqual(%DERIVED, 22, "equate expression %BASE * 3 + 1 folds")
END SUB

SUB Test_DefFn
    CALL Test_Case("DEF FN single-line function")
    CALL Assert_IsEqual(FNDouble%(21), 42, "FNDouble doubles")
END SUB

SUB Test_GosubInsideSub
    DIM trace AS INTEGER
    CALL Test_Case("GOSUB/RETURN inside a SUB")
    trace = 1
    GOSUB bump
    GOSUB bump
    CALL Assert_IsEqual(trace, 111, "subroutine ran twice")
    EXIT SUB
bump:
    trace = trace * 10 + 1
    RETURN
END SUB

SUB Test_ExitForms
    DIM i AS INTEGER, total AS INTEGER
    CALL Test_Case("EXIT FOR / EXIT DO")
    total = 0
    FOR i = 1 TO 100
        IF i > 4 THEN EXIT FOR
        total = total + i
    NEXT i
    CALL Assert_IsEqual(total, 10, "EXIT FOR leaves at i=5")
    total = 0
    DO
        INCR total, 3
        IF total >= 9 THEN EXIT DO
    LOOP
    CALL Assert_IsEqual(total, 9, "EXIT DO leaves at 9")
END SUB

SUB Test_OnGosub
    DIM which AS INTEGER, hit AS INTEGER
    CALL Test_Case("ON n GOSUB list")
    which = 2
    hit = 0
    ON which GOSUB first, second, third
    CALL Assert_IsEqual(hit, 20, "second target taken")
    EXIT SUB
first:
    hit = 10
    RETURN
second:
    hit = 20
    RETURN
third:
    hit = 30
    RETURN
END SUB

SUB Test_UnsignedWord
    DIM w1 AS WORD, w2 AS WORD
    DIM sum AS LONG
    CALL Test_Case("unsigned WORD semantics")
    w1 = 50000
    w2 = 40000
    CALL Assert_IsTrue(w1 > w2, "50000 > 40000 compares unsigned")
    sum = w1 + w2
    CALL Assert_IsTrue(sum = 90000&, "WORD addition widens past 65535")
END SUB

SUB Test_SwapIncrDecr
    DIM a AS INTEGER, b AS INTEGER
    CALL Test_Case("SWAP and INCR/DECR with amounts")
    a = 3 : b = 8
    SWAP a, b
    CALL Assert_IsEqual(a, 8, "SWAP moved b into a")
    CALL Assert_IsEqual(b, 3, "SWAP moved a into b")
    INCR a, 5
    DECR b, 2
    CALL Assert_IsEqual(a, 13, "INCR by amount")
    CALL Assert_IsEqual(b, 1, "DECR by amount")
END SUB

SUB Test_StringSelect
    DIM cmd AS STRING, what AS INTEGER
    CALL Test_Case("SELECT CASE on strings")
    cmd = "MID"
    SELECT CASE cmd
        CASE "LOW"
            what = 1
        CASE "MID"
            what = 2
        CASE ELSE
            what = 3
    END SELECT
    CALL Assert_IsEqual(what, 2, "string CASE match")
END SUB

SUB Test_ShiftAndBit
    DIM v AS WORD
    CALL Test_Case("SHIFT and BIT")
    v = 1
    SHIFT LEFT v, 4
    CALL Assert_IsEqual(v, 16, "SHIFT LEFT 4")
    CALL Assert_IsEqual(BIT(v, 4), 1, "bit 4 set")
    CALL Assert_IsEqual(BIT(v, 3), 0, "bit 3 clear")
    SHIFT RIGHT v, 2
    CALL Assert_IsEqual(v, 4, "SHIFT RIGHT 2")
END SUB

SUB Test_StringRoundTrips
    DIM s AS STRING
    CALL Test_Case("CHR$/ASC/HEX$ round trips")
    s = CHR$(72) + CHR$(73)
    CALL Assert_AreEqualStr("HI", s, "CHR$ concat")
    CALL Assert_IsEqual(ASC(s), 72, "ASC of first char")
    CALL Assert_AreEqualStr("FF", HEX$(255), "HEX$ of 255")
    CALL Assert_AreEqualStr("1010", BIN$(10), "BIN$ of 10")
END SUB
