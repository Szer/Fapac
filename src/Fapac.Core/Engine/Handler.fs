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
