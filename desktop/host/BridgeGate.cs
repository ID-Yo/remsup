using System;
using System.Collections.Generic;
internal sealed class BridgeGate {
 internal readonly string Context=Guid.NewGuid().ToString("N");
 readonly HashSet<string> ids=new HashSet<string>();readonly Queue<string> recent=new Queue<string>();long lastSequence;bool fatal,ready;
 internal void Invalidate(){fatal=true;}
 internal void Accept(string context,string requestId,long sequence,string kind){
  if(fatal)throw new InvalidOperationException("Renderer context is no longer active.");
  Guid parsed;if(context!=Context||!Guid.TryParse(requestId,out parsed)||sequence<=lastSequence||ids.Contains(requestId))throw new ArgumentException("Stale or repeated request.");
  if(kind=="ready"){if(ready)throw new ArgumentException("Renderer already ready.");ready=true;}else if(!ready)throw new ArgumentException("Renderer is not ready.");
  lastSequence=sequence;ids.Add(requestId);recent.Enqueue(requestId);while(recent.Count>256)ids.Remove(recent.Dequeue());
 }
}
