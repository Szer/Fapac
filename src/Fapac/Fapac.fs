namespace Fapac

open System
open Fapac.Core.Engine
open Fapac.Core.Flow

module Scheduler =
    let run (sr: Scheduler) (xJ: Job<'x>) =
    let xK' = {new Cont_State<_, _, _, Cont<unit>>() with
     override xK'.GetProc (wr) =
      Handler.GetProc (&wr, &xK'.State3)
     override xK'.DoHandle (wr, e) =
      Handler.Terminate (&wr, xK'.State3)
      xK'.State1 <- e
      Condition.Pulse (xK', &xK'.State2)
     override xK'.DoWork (wr) =
      Handler.Terminate (&wr, xK'.State3)
      Condition.Pulse (xK', &xK'.State2)
     override xK'.DoCont (wr, x) =
      Handler.Terminate (&wr, xK'.State3)
      xK'.Value <- x
      Condition.Pulse (xK', &xK'.State2)}
    Worker.RunOnThisThread (sr, xJ, xK')
    Condition.Wait (xK', &xK'.State2)
    match xK'.State1 with
     | null -> xK'.Value
     | e -> raise ^ Exception ("Exception raised by job", e)

[<AutoOpen>]
module Fapac =
//  let job = JobBuilder ()
//  let onMain = Extensions.Async.Global.onMain ()

  let run xJ = Scheduler.run (initGlobalScheduler ()) xJ
//  let inline runDelay u2xJ = run <| Job.delay u2xJ
//  let startIgnore x = Job.Global.startIgnore x
//  let startDelay x = Job.Global.startIgnore <| Job.delay x
//  let start x = Job.Global.start x
//  let queueIgnore x = Job.Global.queueIgnore x
//  let queueDelay x = Job.Global.queueIgnore <| Job.delay x
//  let queue x = Job.Global.queue x
//  let server x = Job.Global.server x

//  let queueAsTask x = Job.Global.queueAsTask x
//  let startAsTask x = Job.Global.startAsTask x
//  let startWithActions e2u x2u xJ = Job.Global.startWithActions e2u x2u xJ

//  let inline asAlt (xA: Alt<'x>) = xA
//  let inline asJob (xJ: Job<'x>) = xJ

//  let timeOut x = Timer.Global.timeOut x
//  let timeOutMillis x = Timer.Global.timeOutMillis x
//  let idle = Timer.Global.idle

//  let inline memo (xJ: Job<'x>) = Promise<'x> xJ
