' Differential battery 128: a member of an indexed UDT ARRAY FIELD.
'
' ctx.Slots(i).Id parses as an index of a MEMBER rather than of a name, which is why it needs its own
' address form. A TYPE's array field carries no lower bound - it is subscripted from zero - and it is
' not range-checked, so the subscript is scaled straight off the field's own address.
'
' Head and Tail sit either side of the array on purpose: an element stride or a base off by one
' element writes over a neighbour rather than out of the record, and a test reading only the array
' back would agree with itself while disagreeing with genuine.
TYPE Slot
  Id AS INTEGER
  Amt AS LONG
END TYPE
TYPE Box
  Head AS INTEGER
  Slots(4) AS Slot
  Tail AS INTEGER
END TYPE
DIM b AS Box
OPEN "RESULT.TXT" FOR OUTPUT AS #1
b.Head = 11
b.Tail = 22
FOR i% = 0 TO 3
  b.Slots(i%).Id = i% + 1
  b.Slots(i%).Amt = (i% + 1) * 1000&
NEXT
FOR i% = 0 TO 3
  PRINT #1, b.Slots(i%).Id; b.Slots(i%).Amt
NEXT
PRINT #1, b.Head; b.Tail
j% = 2
PRINT #1, b.Slots(j%).Id; b.Slots(j% - 1).Amt
CLOSE #1
END
