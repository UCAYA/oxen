﻿namespace oxen

open System
open StackExchange.Redis
open System.Threading.Tasks
open Newtonsoft.Json

[<AutoOpen>]
module OxenConvenience = 
    let toValueStr (x:string) = RedisValue.op_Implicit(x:string)
    let toValueI64 (x:int64) = RedisValue.op_Implicit(x:int64)
    let toValueI32 (x:int32) = RedisValue.op_Implicit(x:int32)
    let fromValueStr (x:RedisValue):string = string(x)
    let fromValueI64 (x:RedisValue):int64 = int64(x)
    let fromValueI32 (x:RedisValue):int = int(x)
    let valueToKeyLong (x:RedisValue):RedisKey = RedisKey.op_Implicit(int64(x).ToString ())
    let (|?) (x: 'a option) (y: 'a) =  match x with | None -> y | Some z -> z

    let LOCK_RENEW_TIME = 5000.0

module Async =
    let inline awaitPlainTask (task: Task) = 
        // rethrow exception from preceding task if it fauled
        let continuation (t : Task) : unit =
            match t.IsFaulted with
            | true -> raise t.Exception
            | arg -> ()
        task.ContinueWith continuation |> Async.AwaitTask
 
    let inline startAsPlainTask (work : Async<unit>) = 
        Task.Factory.StartNew(fun () -> work |> Async.RunSynchronously)

type EventType = 
    | Completed
    | Progress
    | Failed
    | Paused
    | Resumed

type Job<'a> = 
    {
        queue: Queue<'a>
        data: 'a
        jobId: int64
        opts: Map<string,string> option
        _progress: int
    }
    member this.lockKey () = this.queue.toKey((this.jobId.ToString ()) + ":lock")
    member this.toData () =  
        let jsData = JsonConvert.SerializeObject (this.data)
        let jsOpts = JsonConvert.SerializeObject (this.opts)
        [|
            HashEntry(toValueStr "id", toValueI64 this.jobId)
            HashEntry(toValueStr "data", toValueStr jsData)
            HashEntry(toValueStr "opts", toValueStr jsOpts)
            HashEntry(toValueStr "progress", toValueI32 this._progress)
        |]
    member this.progress progress = 
        async {
            let client:IDatabase = this.queue.client
            do! client.HashSetAsync (
                    this.queue.toKey(this.jobId.ToString ()), 
                    [| HashEntry (toValueStr "progress", toValueI32 progress) |]
                ) |> Async.awaitPlainTask
            do! this.queue.emit(EventType.Progress, this, progress)
        }
    member this.remove () = async { raise (NotImplementedException ()) }
    member this.takeLock (token, ?renew) = async {
            let nx = match renew with 
                     | Some x -> When.NotExists
                     | None -> When.Always     
            let value = (token.ToString ()) |> toValueStr
            let client:IDatabase = this.queue.client
            return! 
                client.StringSetAsync (
                    this.lockKey (), 
                    value, 
                    Nullable (TimeSpan.FromMilliseconds (LOCK_RENEW_TIME)), 
                    nx 
                ) |> Async.AwaitTask
        }
    member this.renewLock token = this.takeLock (token, true)
    member this.releaseLock (token):Async<int> = async {
        let script = 
            "if redis.call(\"get\", KEYS[1]) == ARGV[1] \n\
            then \n\
            return redis.call(\"del\", KEYS[1]) \n\
            else \n\
            return 0 \n\
            end \n"

        let! result = 
            this.queue.client.ScriptEvaluateAsync (
                script, 
                [|this.lockKey()|], 
                [|(token.ToString ()) |> toValueStr|]
             ) |> Async.AwaitTask
        return result |> RedisResult.op_Explicit
    }
    member private this.moveToSet set =
        async {
            let queue = this.queue
            let activeList = queue.toKey("active")
            let dest = queue.toKey(set)
            let client:IDatabase = queue.client
            let multi = client.CreateTransaction()
            do! multi.ListRemoveAsync (activeList, toValueI64 this.jobId) 
                |> Async.AwaitTask 
                |> Async.Ignore
            do! multi.SetAddAsync (dest, toValueI64 this.jobId) |> Async.AwaitTask |> Async.Ignore
            return! multi.ExecuteAsync() |> Async.AwaitTask                       
        }
    member private this.isDone list =
        async {
            let client:IDatabase = this.queue.client
            return! client.SetContainsAsync(this.queue.toKey(list), toValueI64 this.jobId) |> Async.AwaitTask
        }
    member this.moveToCompleted () = this.moveToSet("completed")
    member this.moveToFailed () = this.moveToSet("failed")
    member this.isCompleted = this.isDone("completed");
    member this.isFailed = this.isDone("failed");
    
    static member create (queue, jobId, data:'a, opts) = 
        async { 
            let job = { queue = queue; data = data; jobId = jobId; opts = opts; _progress = 0 }
            let client:IDatabase = queue.client
            do! client.HashSetAsync (queue.toKey (jobId.ToString ()), job.toData ()) |> Async.awaitPlainTask
            return job 
        }
    static member fromId<'a> (queue: Queue<'a>, jobId: RedisKey) = 
        async {
            let client = queue.client
            let! job = client.HashGetAllAsync (jobId) |> Async.AwaitTask
            //staan hash values altijd op dezelfde volgorde
            return Job.fromData (
                queue, 
                job.[0].Value |> fromValueI64, 
                job.[1].Value |> fromValueStr, 
                job.[2].Value |> fromValueStr, 
                job.[3].Value |> fromValueI32) 
        }
    static member fromData (queue:Queue<'a>, jobId: Int64, data: string, opts: string, progress: int) =
        let sData = JsonConvert.DeserializeObject<'a>(data)
        let sOpts = JsonConvert.DeserializeObject<Map<string, string> option>(opts)
        { queue = queue; data = sData; jobId = jobId; opts = sOpts; _progress = progress }

and OxenEvent<'a> =
    {
        job: Job<'a> option
        progress: int option
        err: exn option
    }

and Events<'a> = {
    Completed: IEvent<OxenEvent<'a>>
    Progress: IEvent<OxenEvent<'a>>
    Failed: IEvent<OxenEvent<'a>>
    Paused: IEvent<OxenEvent<'a>>
    Resumed: IEvent<OxenEvent<'a>>
}

and Queue<'a> (name, db:IDatabase) as this =
    let token = Guid.NewGuid ()
    
    let completedEvent = new Event<OxenEvent<'a>> ()
    let progressEvent = new Event<OxenEvent<'a>> ()
    let failedEvent = new Event<OxenEvent<'a>> ()
    let pausedEvent = new Event<OxenEvent<'a>> ()
    let resumedEvent = new Event<OxenEvent<'a>> ()

    let processJob job = async { 
            // if paused then return else
            // processing <- true; job.renewLock(); renewlocktimeout <- settimeout;
            //try -> handler(); job.moveToCompleted; emit "completed"
            // catch -> job.moveToFailed; job.releaseLock; emit "failed"
            //cleartimeout(lockrenewtimeout); processing <- false 
            ()
        }
    let processStalledJob (job:Job<'a>) = 
        async { 
            let! lock = job.takeLock token
            match lock with
            | true -> 
                let key = this.toKey("completed");
                let! contains = 
                    this.client.SetContainsAsync (key, job.jobId |> toValueI64) 
                    |> Async.AwaitTask
                if contains then 
                    do! processJob(job)
            | false -> ()
        }
    let processStalledJobs () = 
        async {
            let! range = db.ListRangeAsync (this.toKey ("active"), 0L, -1L) |> Async.AwaitTask
            let jobs = 
                range 
                |> Seq.map fromValueStr
                |> Seq.map RedisKey.op_Implicit
                |> Seq.map (fun x -> Job.fromId (this, x))
            return! jobs |> Async.Parallel
        }
    member internal x.emit (eventType, job:Job<'a>, ?value) = 
        async {
            let eventData = 
                {
                    job = Some job
                    progress = value
                    err = None
                }
            
            match eventType with 
            | Progress -> progressEvent.Trigger(eventData)
            | _ -> failwith "It'sNotYetBeenImplemented"
        }
    member x.toKey (kind:string) = RedisKey.op_Implicit ("bull:" + name + ":" + kind)
    member x.client = db
    member x.process (handler:(Job<'a> * unit -> unit) -> unit) = () //process is reserved for future use
    member x.add (data, ?opts:Map<string,string>) = 
        async {
            let! jobId = this.client.StringIncrementAsync (this.toKey "id") |> Async.AwaitTask
            let! job = Job<'a>.create (this, jobId, data, opts) 
            let key = this.toKey "wait"
            let! res = 
                Async.AwaitTask <|
                match opts with
                | None -> this.client.ListLeftPushAsync (key, toValueI64 jobId) 
                | Some opts -> 
                    if opts.ContainsKey "lifo" && bool.Parse (opts.Item "lifo") 
                    then this.client.ListRightPushAsync (key, toValueI64 jobId) 
                    else this.client.ListLeftPushAsync (key, toValueI64 jobId) 

            return job
        }
    member x.pause () = async { raise (NotImplementedException ()) }
    member x.resume () = async { raise (NotImplementedException ()) }
    member x.count () = async { raise (NotImplementedException ()) }
    member x.empty () = async { raise (NotImplementedException ()) }
    member x.getJob id = Job<'a>.fromId (this, id)
    member x.getJobs (queueType, ?isList, ?start, ?stop) =
        async {
            let key = this.toKey("queueType")
            let! jobsIds = 
                match isList |? false with 
                | true -> this.client.ListRangeAsync(key, (start |? 0L), (stop |? -1L)) |> Async.AwaitTask
                | false -> this.client.SetMembersAsync(key) |> Async.AwaitTask

            return!
                jobsIds
                |> Seq.map valueToKeyLong
                |> Seq.map this.getJob
                |> Async.Parallel
        }
    member x.getFailed () = this.getJobs "failed"
    member x.getCompleted () = this.getJobs "completed"
    member x.getWaiting () = this.getJobs "wait", true
    member x.getActive () = this.getJobs "active", true
        
    //Events
    member x.on = { 
        Completed = completedEvent.Publish
        Progress = progressEvent.Publish
        Failed = failedEvent.Publish
        Paused = pausedEvent.Publish
        Resumed = resumedEvent.Publish
    }