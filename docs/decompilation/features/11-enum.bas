' @title: ENUM blocks
' @desc:  ENUM members fold to integer-constant literals at their use sites.
ENUM Color
  Red
  Green = 5
  Blue
END ENUM
PRINT Red; Green; Blue
