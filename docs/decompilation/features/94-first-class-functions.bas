' @title: First-class functions (implicit address, direct invocation, event-call)
' @desc:  Bare proc names become CODEPTR32 where a pointer is expected; delegates and events invoke like SUBs.
DECLARE SUB ClickProc(BYVAL x AS LONG)
DECLARE SUB Log1(BYVAL x AS LONG)
EVENT OnClick AS ClickProc
OnClick += Log1
OnClick(1)
OnClick 2
CALL OnClick(3)
DIM x = SUB(y AS INTEGER) PRINT y * 2
x 15
CALL x(20)
SUB Log1(BYVAL x AS LONG)
  PRINT x
END SUB
