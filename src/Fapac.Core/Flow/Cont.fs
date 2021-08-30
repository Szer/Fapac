namespace Fapac.Core.Flow

open Fapac.Core.Engine

[<AbstractClass>] 
type Cont<'a> =
    inherit Work
    val Value: 'a voption

    abstract GetPick: me: int byref -> Pick option
    default _.GetPick _ = None
    abstract DoCont : wr: Worker byref * value: 'a -> unit
    new() = { inherit Work(); Value = ValueNone }

[<AbstractClass>]
type Cont_State<'a, 's1, 's2, 's3> =
    inherit Cont<'a>
    val mutable State1: 's1 voption
    val mutable State2: 's2 voption
    val mutable State3: 's3 voption

    new() =
        { inherit Cont<'a>()
          State1 = ValueNone
          State2 = ValueNone
          State3 = ValueNone }

    member inline this.Init(s1, s2, s3) : Cont<'a> =
        this.State1 <- ValueSome s1
        this.State2 <- ValueSome s2
        this.State3 <- ValueSome s3
        upcast this
