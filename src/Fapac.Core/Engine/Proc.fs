module Fapac.Core.Engine

open System.Threading
open Fapac.Core.Selective
open Fapac.Core.Engine
open Fapac.Core.Flow

[<Sealed>]
type Proc() =
    inherit Alt<unit>()
    
    let [<VolatileField>] mutable State = 0
    let [<Literal>] Locked = -1
    let [<Literal>] Running = 0
    let [<Literal>] Terminated = 1
    
    let rec doJob (wr: Worker byref, uK: Cont<unit>) =
        let state = State
        if state < Running then
            doJob(&wr, uK)
        elif state > Running then
            Work.Do(uK, &wr)
        elif state <> Interlocked.CompareExchange(&State, Locked, state) then
            doJob(&wr, uK)
        else
            Wait  
        

    override this.DoJob(wr, uK) =
        let state = State
        if state < Running then
            this.DoJob(&wr, uK)
        
        
    override _.TryAlt(wr, i, uK, uE) = ()
