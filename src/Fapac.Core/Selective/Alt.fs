namespace Fapac.Core.Selective

open Fapac.Core.Engine
open Fapac.Core.Flow

type [<AbstractClass>] Pick =
    inherit Else

and [<AbstractClass>] Else =
    val internal pK: Pick

type [<AbstractClass>] Alt<'a>() =
    inherit Job<'a>()
    
    abstract TryAlt: wr: Worker byref * i: int * xK: Cont<'a> * xE: Else -> unit