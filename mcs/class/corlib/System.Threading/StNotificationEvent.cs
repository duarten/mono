//
// System.Threading.StNotificationEvent.cs
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
    internal struct NotificationEvent
    {
        //
        // The value of the *state* field when the event is signalled.
        //

        internal static readonly StWaitBlock SET = StWaitBlock.SENTINEL;

        //
        // The state of the event and the event's queue (i.e., a non-blocking
        // stack) are stored on the *state* field as follows:
        // - *state* == SET: the event is signalled;
        // - *state* == null: the event is non-signalled the queue is empty;
        // - *state* != null && *state* != SET: the event is non-signalled
        //                                      and its queue is non-empty.
        //

        internal volatile StWaitBlock state;

        //
        // The number of spin cycles executed by the first waiter
        // thread before it blocks on the park spot.
        //

        internal readonly int spinCount;

        internal NotificationEvent (bool initialState, int sc)
        {
            state = initialState ? SET : null;
            spinCount = Environment.ProcessorCount > 0 ? sc : 0;
        }

        internal NotificationEvent (bool initialState)
            : this (initialState, 0) { }

        //
        // Returns true if the event is set.
        //

        internal bool IsSet {
            get { return state == SET; }
        }

        //
        // Sets the event to the signalled state.
        //

        internal bool Set ()
        {
            //
            // If the event is already signalled, return true.
            //

            if (state == SET) {
                return true;
            }

            //
            // Atomically signal the event and grab the wait queue.
            //

            StWaitBlock p = Interlocked.Exchange (ref state, SET);

            //
            // If the event queue is empty, return the previous state of the event.
            //

            if (p == null || p == SET) {
                return p == SET;
            }

            //
            // If spinning is configured and there is more than one thread in the
            // wait queue, we first release the thread that is spinning. As only 
            // one thread spins, we maximize the chances of unparking that thread
            // before it blocks.
            //

            StParker pk;
            if (spinCount != 0 && p.next != null) {
                StWaitBlock pv = p, n;
                while ((n = pv.next) != null && n.next != null) {
                    pv = n;
                }

                if (n != null) {
                    pv.next = null;
                    if ((pk = n.parker).TryLock ()) {
                        pk.Unpark (n.waitKey);
                    }
                }
            }

            //
            // Lock and unpark all waiting threads.
            //

            do {
                if ((pk = p.parker).TryLock ()) {
                    pk.Unpark (p.waitKey);
                }
            }
            while ((p = p.next) != null);

            //
            // Return the previous state of the event.
            //

            return false;
        }

        //
        // Resets the event to the non-signalled state.
        //

        internal bool Reset ()
        {
            return state == SET && Interlocked.CompareExchange (ref state, null, SET) == SET;
        }

        //
        // Waits until the event is signalled, activating the specified cancellers.
        //

        internal int Wait (StCancelArgs cargs)
        {
            return state == SET
                       ? StParkStatus.Success
                       : cargs.Timeout == 0
                             ? StParkStatus.Timeout
                             : SlowWait (cargs, new StWaitBlock (WaitType.WaitAny));
        }

        private int SlowWait (StCancelArgs cargs, StWaitBlock wb)
        {
            do {
                //
                // If the event is now signalled, return success.
                //

                StWaitBlock s;
                if ((s = state) == SET) {
                    return StParkStatus.Success;
                }

                wb.next = s;
                if (Interlocked.CompareExchange (ref state, wb, s) == s) {
                    break;
                }
            }
            while (true);

            //
            // Park the current thread, activating the specified cancellers and spinning
            // if appropriate.
            //

            int ws = wb.parker.Park (wb.next == null ? spinCount : 0, cargs);

            //
            // If the wait was cancelled, unlink the wait block from the
            // event's queue.
            //

            if (ws != StParkStatus.Success) {
                Unlink (wb);
            }

            return ws;
        }

        //
        // Waits until the event is signalled, using the specified parker object.
        //

        internal StWaitBlock WaitWithParker (StParker pk, WaitType type, int key, ref int sc)
        {
            StWaitBlock wb = null;
            do {
                StWaitBlock s;
                if ((s = state) == SET) {
                    //
                    // The event is signalled. Try to lock it and self unpark the current thread. 
                    // Anyway, return null to signal that no wait block was queued.
                    //

                    if (pk.TryLock ()) {
                        pk.UnparkSelf (key);
                    }
                    return null;
                }

                //
                // The event seems closed; so, if this is the first loop iteration,
                // create a wait block.
                //

                if (wb == null) {
                    wb = new StWaitBlock (pk, type, 0, key);
                }

                //
                // Try to insert the wait block in the event's queue, if the
                // event remains non-signalled.
                //

                wb.next = s;
                if (Interlocked.CompareExchange (ref state, wb, s) == s) {
                    //
                    // Return the inserted wait block and the suggested spin count.
                    //

                    sc = s == null ? spinCount : 0;
                    return wb;
                }
            }
            while (true);
        }

        //
        // Unlinks the wait block from the event's wait queue.
        //

        internal void Unlink (StWaitBlock wb)
        {
            StWaitBlock s;
            if ((s = state) == SET || s == null ||
                (wb.next == null && s == wb &&
                 Interlocked.CompareExchange (ref state, null, s) == s)) {
                return;
            }
            SlowUnlink (wb);
        }

        //
        // Slow path to unlink the wait block from the event's
        // wait queue.
        //

        internal void SlowUnlink (StWaitBlock wb)
        {
            StWaitBlock next;
            if ((next = wb.next) != null && next.parker.IsLocked) {
                next = next.next;
            }

            StWaitBlock p = state;

            while (p != null && p != next && state != null && state != SET) {
                StWaitBlock n;
                if ((n = p.next) != null && n.parker.IsLocked) {
                    p.CasNext (n, n.next);
                }
                else {
                    p = n;
                }
            }
        }
    }

    internal abstract class StNotificationEventBase : StWaitable
    {
        internal NotificationEvent waitEvent;

        protected StNotificationEventBase (bool initialState, int sc)
        {
            id = NOTIFICATION_EVENT_ID;
            waitEvent = new NotificationEvent (initialState, sc);
        }

        protected StNotificationEventBase (bool initialState)
            : this (initialState, 0) { }

        internal override bool _AllowsAcquire
        {
            get { return waitEvent.IsSet; }
        }

        internal override bool _TryAcquire ()
        {
            return waitEvent.IsSet;
        }

        internal override StWaitBlock _WaitAnyPrologue (StParker pk, int key,
                                                        ref StWaitBlock hint, ref int sc)
        {
            return waitEvent.WaitWithParker (pk, WaitType.WaitAny, key, ref sc);
        }

        internal override StWaitBlock _WaitAllPrologue (StParker pk, ref StWaitBlock hint,
                                                        ref int sc)
        {
            return waitEvent.WaitWithParker (pk, WaitType.WaitAll, StParkStatus.StateChange, ref sc);
        }


        internal override void _CancelAcquire (StWaitBlock wb, StWaitBlock ignored)
        {
            waitEvent.Unlink (wb);
        }
    }

    internal sealed class StNotificationEvent : StNotificationEventBase
    {
        internal StNotificationEvent (bool initialState, int spinCount)
            : base (initialState, spinCount) { }

        internal StNotificationEvent (bool initialState)
            : this (initialState, 0) { }

        internal StNotificationEvent ()
            : this (false, 0) { }

        public bool IsSet {
            get { return waitEvent.IsSet; }
        }

        public bool Set ()
        {
            return waitEvent.Set ();
        }

        public bool Reset ()
        {
            return waitEvent.Reset ();
        }

        public bool Wait (StCancelArgs cargs)
        {
            int ws = waitEvent.Wait (cargs);
            if (ws == StParkStatus.Success) {
                return true;
            }

            StCancelArgs.ThrowIfException (ws);
            return false;
        }

        public void Wait ()
        {
            Wait (StCancelArgs.None);
        }

        internal override bool _Release ()
        {
            waitEvent.Set ();
            return true;
        }
    }
}