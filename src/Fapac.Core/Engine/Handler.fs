namespace Fapac.Core.Engine

open System
open System.Threading
open Fapac.Core.Util

type WorkerEvent =
    inherit ManualResetEventSlim
    val internal Next: int
    val internal Me: int

    new(me) =
        { inherit ManualResetEventSlim()
          Next = -1
          Me = me }

type [<AbstractClass>] Pick =
    inherit Else

and [<AbstractClass>] Else =
    val internal pK: Pick

[<Struct>]
type Worker =
    val mutable internal WorkStack: Work
#if TRAMPOLINE
    val internal StackLimit: nativeint

    [<ThreadStatic; DefaultValue>]
    static val mutable private ThreadStackLimit: nativeint
#endif

and [<AbstractClass>] Work =
    inherit Handler
    val mutable internal Next: Work voption
    new() = { inherit Handler(); Next = ValueNone }

    new(next) =
        { inherit Handler()
          Next = ValueSome next }

    abstract DoWork : wr: Worker byref -> unit
    abstract GetProc: wr: Worker byref -> Proc

    static member Do(work: Work, wr: Worker byref) =
#if TRAMPOLINE
        failwith "TODO"

        if Unsafe.getStackPtr () < wr.StackLimit then
            work.Next <- ValueSome wr.WorkStack
            wr.WorkStack <- work
        else
            work.DoWork &wr
#else
        work.DoWork &wr
#endif

and [<AbstractClass>] Handler() =
    abstract DoHandle : wr: Worker byref * e: exn -> unit

    static member internal DoHandle(hr: Handler voption, wr: Worker byref, e: exn) =
        if hr.IsNone then
            failwith "TODO"
        // Handler.DoHandleNull(&wr, e)
        else
            hr.Value.DoHandle(&wr, e)

//    static member internal DoHandleNull(wr: Worker byref, e: exn) =
//      let tlh = wr.Scheduler.TopLevelHandler
//      if (null == tlh) {
//        PrintExn("Unhandled exception: ", e);
//      } else {
//        var uK = new Cont();
//        wr.Handler = uK;
//        tlh.Invoke(e).DoJob(ref wr, uK);
//      }
//    }

and [<Sealed>] Proc() =
    inherit Alt<unit>()

    let [<VolatileField>] mutable State = 0
    let mutable Joiners: Cont<unit> voption = ValueNone
    let [<Literal>] Locked = -1
    let [<Literal>] Running = 0
    let [<Literal>] Terminated = 1

    let rec doJob (stateRef: _ byref, joiners: _ byref, wr: Worker byref, uK: Cont<unit>) =
        let state = stateRef
        if state < Running then
            doJob(&stateRef, &joiners, &wr, uK)
        elif state > Running then
            Work.Do(uK, &wr)
        elif state <> Interlocked.CompareExchange(&stateRef, Locked, state) then
            doJob(&stateRef, &joiners, &wr, uK)
        else
            Cont.addTaker (&joiners, uK)
            stateRef <- Running

    override this.DoJob(wr, uK) =
        doJob(&State, &Joiners, &wr, uK)

    override _.TryAlt(wr, i, uK, uE) = ()

and [<AbstractClass>] Job<'a>() =
    abstract DoJob: wr: Worker byref * aK: Cont<'a> -> unit

and [<AbstractClass>] Alt<'a>() =
    inherit Job<'a>()
    
    abstract TryAlt: wr: Worker byref * i: int * xK: Cont<'a> * xE: Else -> unit

and [<AbstractClass>]  Cont<'a> =
    inherit Work
    val Value: 'a voption

    abstract DoCont : wr: Worker byref * value: 'a -> unit
    new() = { inherit Work(); Value = ValueNone }

    static member addTaker (queue: Cont<'a> voption byref, xK: Cont<'a>): unit =
      let last = queue
      queue <- ValueSome xK
      if last.IsNone then
        xK.Next <- ValueSome (upcast xK)
      else
        xK.Next <- last.Value.Next
        last.Value.Next <- ValueSome (upcast xK)
