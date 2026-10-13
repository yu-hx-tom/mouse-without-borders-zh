using System;

namespace MwbTransfer
{
    // Source-side lifecycle only. Calls are serialized by the runtime coordinator.
    // Windows owns all dragging and dropping while the pointer is at Source.
    internal sealed class NativeDragSession
    {
        internal enum Phase { Native, Capturing, Remote, Ending, Ended }
        internal readonly Guid Gesture;
        internal readonly uint Source, Controller;
        internal Phase State { get; private set; }
        internal Guid Leg { get; private set; }
        internal uint Target { get; private set; }
        internal string SourcePath { get; private set; }
        internal string PendingTransferPath { get; private set; }
        internal bool CleanupUpObserved { get; private set; }
        // A delayed timer retry may run before the first cleanup worker. Either
        // one may claim; the claim is per leg and idempotent (v9.0.0.7).
        internal bool CleanupClaimed { get { return cleanupClaimed; } }
        // v9.0.0.9: true once this leg's helper reply (any file count) arrived, i.e. an
        // OLE FileDrop drag is KNOWN to have existed. Such a leg may only be released
        // through OLE's own None confirmation, never through absence evidence.
        internal bool OleEvidence { get; private set; }
        bool captureConsumed, cleanupClaimed;

        internal NativeDragSession(Guid gesture, uint source, uint controller)
        {
            if(gesture==Guid.Empty || source<=1 || source==255 || controller<=1 || controller==255)
                throw new ArgumentException("Invalid native gesture identity");
            Gesture=gesture; Source=Target=source; Controller=controller; State=Phase.Native;
        }

        // A new leg is required on EVERY departure, even in the same button press.
        // A filename from the previous leg is not evidence that OLE is still alive.
        internal bool LeaveSource(Guid gesture, Guid leg, uint controller, uint target)
        {
            if(gesture!=Gesture || controller!=Controller || State!=Phase.Native ||
                leg==Guid.Empty || target<=1 || target==255 || target==Source) return false;
            Leg=leg; Target=target; SourcePath=PendingTransferPath=null; captureConsumed=false; cleanupClaimed=false;CleanupUpObserved=false;OleEvidence=false;
            State=Phase.Capturing; return true;
        }

        internal bool AcceptCapture(Guid leg, int count, string path)
        {
            if(State!=Phase.Capturing || Leg!=leg || captureConsumed) return false;
            captureConsumed=true; OleEvidence=true;
            // Unsupported input must never silently become a one-file transfer.
            if(count!=1 || String.IsNullOrWhiteSpace(path)) return false;
            SourcePath=path; State=Phase.Remote; return true;
        }

        // An unsupported capture (several items, a folder) consumes the single
        // helper reply without authorizing a transfer. Cleanup still follows.
        internal bool RejectCapture(Guid leg)
        {
            if(State!=Phase.Capturing || Leg!=leg || captureConsumed) return false;
            captureConsumed=true; OleEvidence=true; return true;
        }

        internal bool ReturnToSource(Guid leg, uint controller)
        {
            if(!MatchesRemote(leg,controller)) return false;
            // No injection, local transfer, or folder preview belongs here.
            State=Phase.Native; Target=Source; Leg=Guid.Empty; SourcePath=null;
            captureConsumed=true; return true;
        }

        // A null path means no transfer is authorized; native cleanup is STILL
        // required if release beat DragEnter, the file is unsupported, or Esc won.
        internal bool EndRemote(Guid leg, uint controller, bool dropped, out string transferPath)
        {
            transferPath=null;
            if(!MatchesRemote(leg,controller)) return false;
            if(dropped && State==Phase.Remote) transferPath=SourcePath;
            PendingTransferPath=transferPath;
            SourcePath=null; captureConsumed=true; State=Phase.Ending; return true;
        }

        internal bool ClaimCleanup(Guid leg)
        {
            if(State!=Phase.Ending || Leg!=leg || cleanupClaimed) return false;
            cleanupClaimed=true; return true;
        }

        internal bool CleanupCompleted(Guid leg, bool systemReleaseObserved)
        {
            if(State!=Phase.Ending || Leg!=leg || !cleanupClaimed || !CleanupUpObserved || !systemReleaseObserved) return false;
            State=Phase.Ended; Leg=Guid.Empty; PendingTransferPath=null; return true;
        }
        internal void CancelPendingTransfer() { PendingTransferPath=null; }
        // Esc/physical release can end OLE before our synthetic UP. This is an
        // aborted transfer, never permission to send a previously captured file.
        internal bool CleanupExternallyCompleted(Guid leg,bool releaseConfirmed)
        {
            if(State!=Phase.Ending || Leg!=leg || !cleanupClaimed || !releaseConfirmed)return false;
            PendingTransferPath=null;State=Phase.Ended;Leg=Guid.Empty;return true;
        }
        // Bounded escape hatch (v9.0.0.7): stop waiting for a release that cannot
        // be proven. Never authorizes a transfer; the source may still need Esc.
        internal bool Abandon(Guid leg)
        {
            if(State!=Phase.Ending || Leg!=leg) return false;
            PendingTransferPath=null; State=Phase.Ended; Leg=Guid.Empty; return true;
        }
        internal bool ObserveCleanupUp(Guid leg)
        {
            if(State!=Phase.Ending||Leg!=leg||!cleanupClaimed||CleanupUpObserved)return false;
            CleanupUpObserved=true;return true;
        }

        internal bool EndNative(Guid gesture)
        {
            if(gesture!=Gesture || State!=Phase.Native) return false;
            State=Phase.Ended; SourcePath=null; return true;
        }

        bool MatchesRemote(Guid leg, uint controller)
        {
            return leg!=Guid.Empty && Leg==leg && Controller==controller && Target!=Source &&
                (State==Phase.Capturing || State==Phase.Remote);
        }
    }
}
