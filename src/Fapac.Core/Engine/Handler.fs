namespace Fapac.Core.Engine

open System
open System.Diagnostics
open System.Threading
open Fapac.Core.Util
open FSharp.NativeInterop

#nowarn "9"

module Promise =
    let [<Literal>] Delayed = 0
    let [<Literal>] Running = 1
    let [<Literal>] HasValue = 2
    let [<Literal>] HasExn = 3
    let [<Literal>] MakeLocked = 4

module SpinLock =
    let rec enter (state: int byref): unit =
        let st = state
        if (st < 0) then
            enter &state 
        if (Interlocked.Exchange(&state, ~~~st) < 0) then
            enter &state
    let exit (state: int byref) =
        Debug.Assert ((state = -1))
        state <- 0

type WorkerEvent =
    inherit ManualResetEventSlim
    val internal Next: int
    val internal Me: int

    new(me) =
        { inherit ManualResetEventSlim()
          Next = -1
          Me = me }

type [<AbstractClass>] Pick() =
    inherit Else()
    let [<Literal>] Claimed = -1
    let [<Literal>] Available = -1
    let [<Literal>] Picked = -1
    
    let [<VolatileField>] mutable State = 0
    let mutable Nacks: Nack option = None
    member inline _.TryPick() =
        Interlocked.CompareExchange(&State, Picked, Available)
        
    member this.SetNacks(wr: Worker byref, i: int) =
        let nk = Nacks
        if nk.IsSome then
            Nacks <- None
            Pick.SetNacks(&wr, i, nk.Value)
            
    static member SetNacks(wr: Worker byref, i: int, nk: Nack) =
        let mutable next = Some nk
        while next.IsSome do
            if nk.I0 > i || nk.I1 <= i then
                Nack.Signal(&wr, next.Value)
            next <- next.Value.Next
    
    static member SetNacks(wr: Worker byref, i: int, pk: Pick) =
        match pk.Nacks with
        | Some nk ->
            pk.Nacks <- None
            Pick.SetNacks(&wr, i, nk)
        | None -> ()
            
and [<AbstractClass>] Else() =
    member val internal pK: Pick option = None with get, set

and [<Struct>] Worker =
    
    [<DefaultValue(false)>] val mutable internal WorkStack: Work option
    [<DefaultValue(false)>] val mutable internal Handler: Handler
#if TRAMPOLINE
    [<DefaultValue>] val mutable internal StackLimit: nativeint

    [<ThreadStatic>]
    [<DefaultValue>] static val mutable private ThreadStackLimit: nativeint
#endif
    [<DefaultValue(false)>] val mutable internal Scheduler: Scheduler
    [<DefaultValue>] val mutable internal RandomLo: uint64
    [<DefaultValue>] val mutable internal RandomHi: uint64

    [<ThreadStatic>]
    [<DefaultValue>] static val mutable private RunningWork: int
    
    member this.Init(sr: Scheduler, bytes: int) =
        let sp = Unsafe.getStackPtr()
#if TRAMPOLINE
        let mutable limit = Worker.ThreadStackLimit
        if limit = IntPtr.Zero then
            limit <- sp - IntPtr bytes
            Worker.ThreadStackLimit <- limit
        this.StackLimit <- limit
#endif
        this.RandomLo <- (NativePtr.ofNativeInt sp) |> NativePtr.read
        this.Scheduler <- sr
    
    static member internal Push(wr: Worker byref, work: Work) =
        work.Next <- None
        Worker.PushNew(&wr, work, work)
        
    static member internal Push(sr: Scheduler, work: Work, last: Work, n: int) =
        Scheduler.Enter sr
        last.Next <- sr.WorkStack
        sr.WorkStack <- Some work
        sr.NumWorkStack <- sr.NumWorkStack + n
        Scheduler.UnsafeSignal sr
        Scheduler.Exit sr
        
    static member internal PushAll(sr: Scheduler, work: Work option) =
        match work with
        | None -> ()
        | Some work ->
        
        let mutable n = 1
        let mutable last = work
        let mutable next = last.Next
        while next.IsSome do
            n <- n + 1
            last <- next.Value
            next <- last.Next
        Worker.Push(sr, work, last, n)
        
    static member internal PushNew(wr: Worker byref, work: Work, last: Work) =
        Debug.Assert(last.Next.IsNone)
        let older = wr.WorkStack
        wr.WorkStack <- Some work
        if older.IsSome then
            let sr = wr.Scheduler
            match sr.WorkStack with
            | Some _ -> last.Next <- older
            | None -> Scheduler.PushAll(sr, older)
    
    static member RunOnThisThread(sr: Scheduler, work: Work) =
        let mutable wr = Worker()
        wr.Init(sr, 8000)
        let d = Worker.RunningWork
        Worker.RunningWork <- d + 1
        Scheduler.Inc(sr)
        try
            wr.Handler <- work
            work.DoWork &wr
        with e ->
            match wr.WorkStack with
            | Some workStack ->
                wr.WorkStack <- Some(upcast FailWork(workStack, e, wr.Handler))
            | None ->
                wr.WorkStack <- Some(upcast FailWork(e, wr.Handler))
        
        Scheduler.PushAllAndDec(sr, wr.WorkStack)
        Worker.RunningWork <- d

and [<AbstractClass>] Work =
    inherit Handler
    val mutable internal Next: Work option
    new() = { inherit Handler(); Next = None }

    new(next) =
        { inherit Handler()
          Next = Some next }

    abstract DoWork : wr: Worker byref -> unit

    static member Do(work: Work, wr: Worker byref) =
#if TRAMPOLINE
        if Unsafe.getStackPtr () < wr.StackLimit then
            work.Next <- wr.WorkStack
            wr.WorkStack <- Some work
        else
            work.DoWork &wr
#else
        work.DoWork &wr
#endif

and [<Sealed>] FailWork =
    inherit Work
    val e: Exception
    val hr: Handler

    new (next: Work, e, hr) =
        { inherit Work(next)
          e = e
          hr = hr }
    new (e, hr) =
        { inherit Work()
          e = e
          hr = hr }
        
    override this.DoWork wr =
        Handler.DoHandle(Some this.hr, &wr, this.e)
        
    override this.DoHandle(wr, e) =
        Handler.DoHandle(Some this.hr, &wr, e)
    
    override this.GetProc wr =
        raise (NotImplementedException())

and [<AbstractClass>] Handler() =
    abstract DoHandle : wr: Worker byref * e: exn -> unit
    abstract GetProc : wr: Worker byref -> Proc

    static member internal DoHandle(hr: Handler option, wr: Worker byref, e: exn) =
        if hr.IsNone then
            failwith "TODO DoHandleNull"
            // Handler.DoHandleNull(&wr, e)
        else
            hr.Value.DoHandle(&wr, e)

    static member internal GetProc(wr: Worker byref, xKr: Cont<'a> option byref): Proc =
        match xKr with
        | Some xK ->
            xK.GetProc &wr
        | None ->
            Handler.AllocProc(&wr, &xKr)
            
    static member internal AllocProc(wr: Worker byref, xKr: Cont<'a> option byref) =
        let mutable xKn = Some(ProcFinalizer(wr.Scheduler, Proc()) :> Cont<'a>)
        let xK = Interlocked.CompareExchange<Option<Cont<'a>>>(&xKr, xKn, None)
        if xK.IsSome then
            GC.SuppressFinalize xKn
            xKn <- xK
        xKn.Value.GetProc &wr

    static member internal DoHandleNull(wr: Worker byref, e: exn) =
        let tlh = wr.Scheduler.TopLevelHandler
        match tlh with
        | None ->
            Handler.PrintExn("Unhandled exception: ", e)
        | Some tlh ->
            let uK = Cont()
            wr.Handler <- uK
            (tlh e).DoJob(&wr, uK)
            
    static member internal PrintExn(header: string, e: exn) =
        let mutable first = true
        StaticData.WriteLine (header + (e.ToString()))
        let mutable inner = e.InnerException
        while not(isNull inner) do
            StaticData.WriteLine ("Caused by: " + (e.ToString()))
        StaticData.WriteLine "No other causes."


and [<Sealed>] Proc() =
    inherit Alt<unit>()

    let [<VolatileField>] mutable State = 0
    let mutable Joiners: Cont<unit> option = None
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
            
    let rec terminatePick(wr: Worker byref, cursorRef: Work option byref, joinersRef: Cont<unit> byref, me: int byref) =
        let mutable cursor = cursorRef
        cursor <- cursor.Next
        match joinersRef.GetPick &me with
        | None ->
            Worker.Push(&wr, joinersRef)
            if not <| Object.ReferenceEquals(cursor, joinersRef) then
                terminatePick (&wr, &cursor, &me)
        | Some pk ->
            let rec pick (pk: Pick) =
                let st = pk.TryPick()
                if st < 0 then pick pk
                else st
            let st = pick pk
            
            if st > 0 then
                if not <| Object.ReferenceEquals(cursor, joinersRef) then
                    failwith "TODO rec call"
            else
                Pick.SetNacks(&wr, me, pk)
                Worker.Push(&wr, joinersRef)
                if not <| Object.ReferenceEquals(cursor, joinersRef) then
                    failwith "TODO rec call"
        
    let rec terminate(stateRef: _ byref, joinersRef: _ byref, wr: Worker byref) =
        let state = stateRef
        if state < Running then
            terminate(&stateRef, &joinersRef, &wr)
        elif state <> Interlocked.CompareExchange(&stateRef, Terminated, state) then
            terminate(&stateRef, &joinersRef, &wr)
        else
            match joinersRef with
            | None -> ()
            | Some joiners ->
                joinersRef <- None
                let mutable me = 0
                let mutable cursor = joiners
                
                
            

    override this.DoJob(wr, uK) =
        doJob(&State, &Joiners, &wr, uK)

    override _.TryAlt(wr, i, uK, uE) = ()
    

    
    member internal this.Terminate(wr: Worker byref) =
        
        ()
    
and ProcFinalizer<'a>(sr: Scheduler, pr: Proc) =
    inherit Cont<'a>()
    override this.Finalize() =
        Worker.RunOnThisThread(sr, this)
        
    override this.GetProc wr = pr
    override this.DoHandle(wr, e) =
        GC.SuppressFinalize this
        pr.Terminate &wr
        Handler.DoHandleNull(&wr, e)

and [<AbstractClass>] Job<'a>() =
    abstract DoJob: wr: Worker byref * aK: Cont<'a> -> unit

and [<AbstractClass>] Alt<'a>() =
    inherit Job<'a>()
    
    abstract TryAlt: wr: Worker byref * i: int * xK: Cont<'a> * xE: Else -> unit

and [<AbstractClass>] Cont<'a> =
    inherit Work
    val mutable Value: 'a option

    abstract DoCont : wr: Worker byref * value: 'a -> unit
    abstract GetPick: me: int byref -> Pick option
    default _.GetPick _ = None
    new() = { inherit Work(); Value = None }
    


    static member addTaker (queue: Cont<'a> option byref, xK: Cont<'a>): unit =
      let last = queue
      queue <- Some xK
      if last.IsNone then
        xK.Next <- Some (upcast xK)
      else
        xK.Next <- last.Value.Next
        last.Value.Next <- Some (upcast xK)
        
    static member pickReader<'a>(readersVar: Cont<'a> option byref, value: 'a option, wr: Worker byref): unit =
      
      let rec pickReader(cursorRef: Work option byref, me: int byref, value: 'a option, readers: Cont<'a>) =
        let reader =
            if cursorRef.IsSome then
                cursorRef <- cursorRef.Value.Next
                cursorRef.Value :?> Cont<'a>
            else
                failwith "imposibru" // No idea why it is impossible
        
        match reader.GetPick &me with
        | None ->
            reader.Value <- value
            Worker.Push(&wr, reader)
            
            if not <| obj.ReferenceEquals(cursorRef, readers) then
                pickReader(&cursorRef, &me, value, readers)
        | Some pk ->
            let rec tryPick(pk: Pick) =
                let st = pk.TryPick()
                if st < 0 then tryPick(pk)
                else st
            
            let st = tryPick pk
            if st > 0 then
                if not <| obj.ReferenceEquals(cursorRef, readers) then
                    pickReader(&cursorRef, &me, value, readers)
            else
                Pick.SetNacks(&wr, me, pk)
                reader.Value <- value
                Worker.Push(&wr, reader)
                
                if not <| obj.ReferenceEquals(cursorRef, readers) then
                    pickReader(&cursorRef, &me, value, readers)
      
      let readers = readersVar
      if readers.IsSome then
          readersVar <- None
          let mutable me = 0
          let mutable cursor = Some (readers.Value :> Work)
          pickReader(&cursor, &me, value, readers.Value)
          
    
and [<Sealed>] private Cont() = class
    inherit Cont<unit>()
    end

and Scheduler internal() =
    
    [<DefaultValue>] val mutable internal WorkStack: Work option
    [<DefaultValue>] val mutable internal Lock: int
    [<DefaultValue>] val mutable internal NumWorkStack: int
    [<DefaultValue>] val mutable internal WaiterStack: int
    [<DefaultValue>] val mutable internal NumActive: int
    [<DefaultValue>] val mutable internal NumPulseWaiters: int
    [<DefaultValue>] val mutable internal Events: WorkerEvent array
    [<DefaultValue>] val mutable internal TopLevelHandler: (exn -> Job<unit>) option
    [<DefaultValue>] val mutable internal IdleHandler: Job<int>
    
    static member internal Enter(sr: Scheduler) =
        SpinLock.enter &sr.Lock
    
    static member internal Exit(sr: Scheduler) =
        SpinLock.exit &sr.Lock
        
    static member internal UnsafeSignal(sr: Scheduler) =
        let waiter = sr.WaiterStack
        if waiter >= 0 then
            let ev = sr.Events.[waiter]
            sr.WaiterStack <- ev.Next
            Debug.Assert((sr.NumActive >= 0))
            sr.NumActive <- sr.NumActive + 1
            ev.Set()
    
    static member internal Inc(sr: Scheduler) =
        Scheduler.Enter sr
        Debug.Assert((sr.NumActive >= 0))
        sr.NumActive <- sr.NumActive + 1
        Scheduler.Exit sr

    static member internal UnsafeDec(sr: Scheduler) =
        let numActive = sr.NumActive - 1
        sr.NumActive <- numActive
        Debug.Assert((sr.NumActive >= 0))
        if (numActive = 0 && sr.NumPulseWaiters <> 0) then
            Monitor.Enter sr
            Monitor.PulseAll sr
            Monitor.Exit sr
    
    static member internal Dec(sr: Scheduler) =
        Scheduler.Enter sr
        Scheduler.UnsafeDec sr
        Scheduler.Exit sr
        
    static member internal Push(sr: Scheduler, work: Work, last: Work, n: int) =
        Scheduler.Enter sr
        last.Next <- sr.WorkStack
        sr.WorkStack <- Some work
        sr.NumWorkStack <- sr.NumWorkStack + n
        Scheduler.UnsafeSignal sr
        Scheduler.Exit sr
        
    static member internal PushAll(sr: Scheduler, work: Work option) =
        if work.IsSome then
            let mutable n = 1
            let mutable last = work.Value
            let mutable next = last.Next
            while next.IsSome do
                n <- n + 1
                last <- next.Value
                next <- last.Next
            Scheduler.Push(sr, work.Value, last, n)
    
    static member internal PushAndDec(sr: Scheduler, work: Work, last: Work, n: int) =
        Scheduler.Enter sr
        last.Next <- sr.WorkStack
        sr.WorkStack <- Some work
        sr.NumActive <- sr.NumActive - 1
        Debug.Assert((sr.NumActive >= 0))
        Scheduler.UnsafeSignal sr
        Scheduler.Exit sr
    
    static member internal PushAllAndDec(sr: Scheduler, work: Work option) =
        match work with
        | None -> Scheduler.Dec(sr)
        | Some work ->
            let mutable n = 1
            let mutable last = work
            let mutable next = last.Next
            while next.IsSome do
                n <- n + 1
                last <- next.Value
                next <- last.Next
            Scheduler.PushAndDec(sr, work, last, n)

and StaticData() =
    static member val WriteLine: string -> unit = ignore with get, set

and [<Sealed>] Nack =
    inherit Promise<unit>
    val mutable internal Next: Nack option
    val mutable internal I0: int
    val mutable internal I1: int
    new (next, i0) as this =
        { inherit Promise<unit>()
          I0 = i0
          I1 = Int32.MaxValue
          Next = next }
        
    static member internal Signal(wr: Worker byref, nk: Nack) =
        let rec signal(wr: Worker byref, nk: Nack) =
            let state = nk.State
            if state < Promise.Delayed then
                signal(&wr, nk)
            elif Promise.Running <> Interlocked.CompareExchange(&nk.State, state+1, state) then
                signal(&wr, nk)
            else
                Cont.pickReader<unit>(&nk.Readers, None, &wr);
        signal(&wr, nk)
            
and Promise<'a>() =
    inherit Alt<'a>()
    let [<VolatileField>] mutable state = Promise.Running
    [<DefaultValue>] val mutable internal Readers: Cont<'a>
    
    member _.State: int byref = &state