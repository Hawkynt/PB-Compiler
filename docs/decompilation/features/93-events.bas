' @title: Events (EVENT / RAISE with += / -=)
' @desc:  EVENT lowers to a DWORD handler array + count; += appends, -= compacts, RAISE loops CALL DWORD.
DECLARE SUB ClickProc(BYVAL x AS LONG)
DECLARE SUB Log1(BYVAL x AS LONG)
EVENT OnClick AS ClickProc
OnClick += CODEPTR32(Log1)
OnClick += CODEPTR32(Log1)
RAISE OnClick(42)
OnClick -= CODEPTR32(Log1)
RAISE OnClick(7)
SUB Log1(BYVAL x AS LONG)
  PRINT x
END SUB
