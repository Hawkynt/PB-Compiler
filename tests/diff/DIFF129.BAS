' Differential battery 129: GET / PUT of a FIXED-WIDTH field of a record.
'
' A STRING * 3 keeps its characters IN the record, so this is the address-and-size form - the same
' LEA-and-move every non-string variable gets - and not the handle form a dynamic STRING needs. The
' two look alike in the source and are different statements underneath.
'
' The field after the strings is read back for the reason DIFF128 reads Head and Tail: a width taken
' from the wrong place moves the file position by the wrong amount, and the damage shows up in the
' NEXT field rather than in the one that caused it.
TYPE Hdr
  Sig AS STRING * 3
  Ver AS STRING * 3
  W AS INTEGER
END TYPE
DIM h AS Hdr, g AS Hdr
OPEN "RESULT.TXT" FOR OUTPUT AS #2
h.Sig = "GIF"
h.Ver = "89a"
h.W = 320
OPEN "H.TMP" FOR BINARY AS #1
PUT #1, , h.Sig
PUT #1, , h.Ver
PUT #1, , h.W
CLOSE #1
OPEN "H.TMP" FOR BINARY AS #1
GET #1, , g.Sig
GET #1, , g.Ver
GET #1, , g.W
CLOSE #1
PRINT #2, g.Sig; "/"; g.Ver; "/"; g.W
PRINT #2, LEN(g.Sig); LEN(g.Ver)
CLOSE #2
KILL "H.TMP"
END
