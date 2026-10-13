using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MwbTransfer
{
    internal enum NativeInputDisposition { Ordinary, PassToWindows, SuppressStale }

    // Does not install hooks or inject input. The existing hook calls Inspect
    // BEFORE its RealData/SkipMouseUpCount branches. Only our tagged LEFTUP is
    // bypassed; physical input and other applications' injection remain ordinary.
    //
    // v9.0.0.8 token namespace: tokens are consecutive positive 31-bit values from a
    // random origin. "Ours" is therefore decidable forever without remembering each
    // value: any value inside [origin, origin+issued) is a token we issued. A token
    // that is no longer live is refused (SuppressStale) however late it arrives, and
    // no value is ever issued twice (2^31 registrations would be needed to wrap).
    internal sealed class NativeInputTag
    {
        // v9.0.0.10: a Move token authorises our cleanup jiggle moves for ONE attempt; a
        // release (UP) token authorises exactly one UP. The kinds never substitute.
        sealed class Entry { internal Guid Leg; internal bool Accepted, Consumed, Retired, Move; internal DateTime RetiredAt; }
        readonly object gate=new object();
        readonly Dictionary<IntPtr,Entry> entries=new Dictionary<IntPtr,Entry>();
        // Values with another meaning (normal remote input, cleanup moves) are never issued.
        readonly IntPtr[] reserved;
        readonly long origin;
        long issued;
        internal Func<DateTime> Clock=delegate { return DateTime.UtcNow; };
        internal NativeInputTag() : this(new IntPtr[0]) { }
        internal NativeInputTag(params IntPtr[] reservedTokens)
        {
            reserved=reservedTokens??new IntPtr[0];
            byte[] bytes=new byte[4];
            using(RandomNumberGenerator random=RandomNumberGenerator.Create())random.GetBytes(bytes);
            origin=BitConverter.ToInt32(bytes,0)&Int32.MaxValue;
        }
        // Live (not yet retired, or recently retired) tokens only. Each cleanup retires
        // its token within ~0.3 s, so this bound is reached only by a defect.
        internal const int Limit=4096;
        internal static readonly TimeSpan Linger=TimeSpan.FromMinutes(2);
        internal int LiveCount { get { lock(gate) return entries.Count; } }

        internal IntPtr Register(Guid leg) { return Register(leg,false); }
        // Live immediately (Accepted) until Retire/RetireLeg; refused for ever after.
        internal IntPtr RegisterMove(Guid leg) { return Register(leg,true); }
        IntPtr Register(Guid leg,bool move)
        {
            if(leg==Guid.Empty) throw new ArgumentException("Missing native drag leg");
            lock(gate)
            {
                Prune();
                if(entries.Count>=Limit) throw new InvalidOperationException("Native input tag capacity reached");
                IntPtr token;
                do
                {
                    if(issued>=Int32.MaxValue) throw new InvalidOperationException("Native input tag space exhausted");
                    // Positive 31-bit values survive low-DWORD truncation and both
                    // zero and sign extension in the observed input chains.
                    token=new IntPtr((origin+issued)&Int32.MaxValue); issued++;
                }
                while(token==IntPtr.Zero || IsReserved(token));
                entries.Add(token,new Entry { Leg=leg, Move=move, Accepted=move }); return token;
            }
        }
        bool IsReserved(IntPtr token){foreach(IntPtr r in reserved)if(r==token)return true;return false;}
        // True for every value this instance ever issued, live or not.
        internal bool Issued(IntPtr token)
        {
            long value=token.ToInt64();
            if(value<=0||value>Int32.MaxValue||IsReserved(token)) return false;
            long offset=(value-origin)&Int32.MaxValue;
            return offset<issued;
        }

        void Prune()
        {
            DateTime now=Clock(); List<IntPtr> old=null;
            foreach(KeyValuePair<IntPtr,Entry> pair in entries)
                if(pair.Value.Retired && now-pair.Value.RetiredAt>Linger) { if(old==null)old=new List<IntPtr>(); old.Add(pair.Key); }
            if(old!=null) foreach(IntPtr token in old) entries.Remove(token);
        }

        // SendInput can synchronously invoke the hook before returning its count.
        // Arm before SendInput, then inspect its return and retire on failure.
        internal void Arm(IntPtr token) { lock(gate) { Entry e;if(entries.TryGetValue(token,out e)&&!e.Retired)e.Accepted=true; } }
        internal void Retire(IntPtr token) { lock(gate) { Entry e;if(entries.TryGetValue(token,out e))MarkRetired(e); } }
        internal bool RetireAndWasObserved(IntPtr token)
        { lock(gate) { Entry e;if(!entries.TryGetValue(token,out e))return false;MarkRetired(e);return e.Consumed; } }
        internal bool WasObserved(IntPtr token) { lock(gate) { Entry e;return entries.TryGetValue(token,out e)&&e.Consumed; } }
        void MarkRetired(Entry e) { if(!e.Retired){e.Retired=true;e.RetiredAt=Clock();} }
        // Retires every live token (UP and move) of a leg, e.g. when it is invalidated.
        internal void RetireLeg(Guid leg) { lock(gate) foreach(Entry e in entries.Values) if(e.Leg==leg) MarkRetired(e); }

        // Injected WM_MOUSEMOVE: our live move token passes; any other value we issued
        // (retired move, any UP token) is dropped; everything else is ordinary input.
        internal NativeInputDisposition InspectMove(int flags, IntPtr token)
        {
            if((flags&1)==0) return NativeInputDisposition.Ordinary;
            lock(gate)
            {
                Entry e;
                if(entries.TryGetValue(token,out e))
                    return e.Move&&e.Accepted&&!e.Retired ? NativeInputDisposition.PassToWindows : NativeInputDisposition.SuppressStale;
                return Issued(token) ? NativeInputDisposition.SuppressStale : NativeInputDisposition.Ordinary;
            }
        }

        internal NativeInputDisposition Inspect(int code, int message, IntPtr hookData, Guid currentEndingLeg)
        {
            if(code<0 || message!=0x202 || hookData==IntPtr.Zero) return NativeInputDisposition.Ordinary;
            int flags=Marshal.ReadInt32(hookData,12);
            IntPtr token=Marshal.ReadIntPtr(hookData,IntPtr.Size==8?24:20);
            return InspectValue(message,flags,token,currentEndingLeg);
        }

        internal NativeInputDisposition InspectValue(int message, int flags, IntPtr token, Guid currentEndingLeg)
        {
            if(message!=0x202 || (flags&1)==0) return NativeInputDisposition.Ordinary;
            lock(gate)
            {
                Entry e;
                if(!entries.TryGetValue(token,out e))
                    return Issued(token) ? NativeInputDisposition.SuppressStale : NativeInputDisposition.Ordinary;
                if(e.Move || !e.Accepted || e.Retired || e.Consumed || e.Leg!=currentEndingLeg || currentEndingLeg==Guid.Empty)
                    return NativeInputDisposition.SuppressStale;
                e.Consumed=true; return NativeInputDisposition.PassToWindows;
            }
        }
    }
}
