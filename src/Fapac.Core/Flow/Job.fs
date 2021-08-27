namespace Fapac.Core.Flow

open Fapac.Core.Engine

type [<AbstractClass>] Job<'a>() =
    abstract DoJob: wr: Worker byref * aK: Cont<'a> -> unit