//
// System.Threading.StParker.cs
//
// Copyright 2011 Carlos Martins, Duarte Nunes
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// Author: Duarte Nunes (duarte.m.nunes@gmail.com)
//

#pragma warning disable 0420

namespace System.Threading 
{
    public class StParker 
	{
        private const int WAIT_IN_PROGRESS_BIT = 31;
        private const int WAIT_IN_PROGRESS = (1 << WAIT_IN_PROGRESS_BIT);
        private const int LOCK_COUNT_MASK = (1 << 16) - 1;

        //
        // The link field used when the parker is registered
        // with an alerter.
        //

        internal volatile StParker pnext;

        //
        // The parker state.
        //

        internal volatile int state;

        //
        // The park spot used to block the parker's owner thread.
        //

        internal StParkSpot parkSpot;

        //
        // The park wait status.
        //

        internal int waitStatus;

        internal StParker(int releasers) 
		{
            state = releasers | WAIT_IN_PROGRESS;
        }

        internal StParker() 
			: this (1) { }

        //
        // Resets the parker.
        //

        internal void Reset(int releasers) 
		{
            pnext = null;
            state = releasers | WAIT_IN_PROGRESS;
        }

        internal void Reset() 
		{
            Reset (1);
        }

        //
        // Tests and clears the wait-in-progress bit.
        //

        internal bool TestAndClearInProgress () 
		{
            do {
                int s;
                if ((s = state) >= 0) {
                    return false;
                }
                if (Interlocked.CompareExchange (ref state, (s & ~WAIT_IN_PROGRESS), s) == s) {
                    return true;
                }
            } while (true);
        }

        //
        // Returns true if the parker is locked.
        //

        internal bool IsLocked
        {
            get { return (state & LOCK_COUNT_MASK) == 0; }
        }

        //
        // Tries to lock the parker.
        //

        internal bool TryLock() 
		{
            do {

                //
                // If the parker is already locked, return false.
                //

                int s;
                if (((s = state) & LOCK_COUNT_MASK) == 0) {
                    return false;
                }

                //
                // Try to decrement the count down lock.
                //

                if (Interlocked.CompareExchange (ref state, s - 1, s) == s) {

                    //
                    // Return true if the count down lock reached zero.
                    //

                    return (s & LOCK_COUNT_MASK) == 1;
                }
            } while (true);
        }

        //
        // Tries to cancel the parker.
        //

        internal bool TryCancel() 
		{
            do {

                //
                // If the parker is already locked, return false.
                //

                int s;
                if (((s = state) & LOCK_COUNT_MASK) <= 0) {
                    return false;
                }

                //
                // Try to set the park's count down lock to zero, preserving 
                // the wait-in-progress bit. Return true on success. 
                //

                if (Interlocked.CompareExchange (ref state, (s & WAIT_IN_PROGRESS), s) == s) {
                    return true;
                }
            } while (true);
        }

        //
        // Cancels the parker.
        //
        // NOTE: This method should be called only by the parker's
        //       owner thread.
        //

        internal void SelfCancel() 
		{
            state = WAIT_IN_PROGRESS;
        }

        //
        // Unparks the parker's owner thread if the wait is still in progress.
        //

        internal bool UnparkInProgress(int ws) 
		{
            waitStatus = ws;
            return (state & WAIT_IN_PROGRESS) != 0 &&
                   (Interlocked.Exchange (ref state, 0) & WAIT_IN_PROGRESS) != 0;
        }

        //
        // Unparks the parker owner thread.
        //

        internal void Unpark(int status) 
		{
            if (UnparkInProgress (status)) {
                return;
            }

            parkSpot.Set ();
        }

        //
        // Unparks the parker's owner thread.
        //

        internal void UnparkSelf(int status) 
		{
            waitStatus = status;
            state = 0;
        }

        //
        // Parks the current thread until it is unparked, activating the
        // specified cancellers and spinning if specified.
        //

        internal int Park(int spinCount, StCancelArgs cargs) 
		{
#if NET_4_0 
			SpinWait spinWait;
#endif
						
            //
            // Spin the specified number of cycles before blocking
            // the current thread.
            //

            do {
                if (state == 0) {
                    return waitStatus;
                }
                if (cargs.Alerter != null && cargs.Alerter.IsSet && TryCancel ()) {
                    return StParkStatus.Alerted;
                }
                if (spinCount-- <= 0) {
                    break;
                }
#if NET_4_0 								
                spinWait.SpinOnce ();
#else
				Thread.SpinWait (1);
#endif								
            } while (true);

            //
            // Allocate a park spot to block the current thread.
            //

            parkSpot.Alloc ();

            //
            // Try to clear the wait-in-progress bit. If the bit was already
            // cleared, the thread was unparked. So, free the park spot and
            // return the wait status.
            //

            if (!TestAndClearInProgress ()) {
                parkSpot.Free ();
                return waitStatus;
            }

            //
            // If an alerter was specified, we register the parker with
            // the alerter before blocking the thread on the park spot.
            //

            bool unregister = false;
            if (cargs.Alerter != null) {
                if (!(unregister = cargs.Alerter.RegisterParker (this))) {

                    //
                    // The alerter is already set. So, we try to cancel the
                    // parker and, if successful, we free the park spot and 
                    // return an alerted wait status.
                    //

                    if (TryCancel ()) {
                        parkSpot.Free ();
                        return StParkStatus.Alerted;
                    }

                    //
                    // We can't cancel the parker because someone else acquired 
                    // the count down lock. So, we must wait unconditionally on 
                    // the park spot until it is set.
                    //

                    cargs = StCancelArgs.None;
                }
            }

            //
            // Wait on the park spot.
            //

            parkSpot.Wait (this, cargs);

            //
            // Free the park spot and deregister the parker from the
            // alerter, if it was registered.
            //

            parkSpot.Free ();
            if (unregister) {
                cargs.Alerter.DeregisterParker (this);
            }
            return waitStatus;
        }

        internal int Park(StCancelArgs cargs) 
		{
            return Park (0, cargs);
        }


        internal int Park() 
		{
            return Park (0, StCancelArgs.None);
        }

        //
        // Delays execution of the current thread, sensing
        // the specified cancellers.
        //

        internal static int Sleep(StCancelArgs cargs) 
        {
            var pk = new StParker ();
            int ws = pk.Park (0, cargs);
            StCancelArgs.ThrowIfException (ws);
            return ws;
        }

        //
        // CASes on the *pnext* field.
        //

        internal bool CasNext (StParker n, StParker nn) 
		{
            return pnext == n &&
                   Interlocked.CompareExchange (ref pnext, nn, n) == n;
        }
    }

    //
    // This class implements a parker that is used as sentinel.
    //

    internal class SentinelParker : StParker 
	{
        internal SentinelParker() 
		    : base(0) { }
    }
}