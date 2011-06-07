//
// System.Threading.StMutant.cs
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
    internal class StMutant : StWaitable
    {
        //
        // The boolean state of the lock (signalled/non-signalled) is stored
        // in the *head.next* field, as follows:
        // - head.next == UNLOCKED: the lock is signalled and its queue is empty;
        // - head.next == null: the lock is non-signalled and its queue is empty;
        // - others: the lock isn't signalled and its queue is non-empty.
        //

        private static readonly StWaitBlock UNLOCKED = new StWaitBlock();

        private volatile StWaitBlock head;
        private volatile StWaitBlock tail;

        //
        // The predecessor of a wait block that must be unlinked
        // when the right conditions are met.
        //

        private volatile StWaitBlock toUnlink;

        private readonly int spinCount;

        internal StMutant (bool initialState, int sc)
        {
            head = tail = new StWaitBlock ();
            if (initialState) {
                head.next = UNLOCKED;
            }
            spinCount = Environment.ProcessorCount > 0 ? sc : 0;
        }

        internal override bool _AllowsAcquire {
            get { return head.next == UNLOCKED; }
        }

        internal override bool _TryAcquire ()
        {
            return (head.next == UNLOCKED &&
                    Interlocked.CompareExchange (ref head.next, null, UNLOCKED) == UNLOCKED);
        }

        internal bool SlowTryAcquire (StCancelArgs cargs)
        {
            StWaitBlock wb = null, pred;
            do {
                if (head.next == UNLOCKED &&
                    Interlocked.CompareExchange (ref head.next, null, UNLOCKED) == UNLOCKED) {
                    return true;
                }

                if (cargs.Timeout == 0) {
                    return false;
                }

                if (wb == null) {
                    wb = new StWaitBlock (WaitType.WaitAny);
                }

                //
                // Do the necessary consistency checks before trying to insert
                // the wait block in the lock's queue; if the queue is in a
                // quiescent state, try to perform the insertion.
                //

                StWaitBlock t, tn;
                if ((tn = (t = tail).next) == UNLOCKED) {
                    continue;
                }
                if (tn != null) {
                    AdvanceTail (t, tn);
                    continue;
                }

                if (Interlocked.CompareExchange (ref t.next, wb, null) == null) {
                    AdvanceTail (t, wb);

                    //
                    // Save the predecessor of the wait block and exit the loop.
                    //

                    pred = t;
                    break;
                }
            }
            while (true);

            int ws = wb.parker.Park ((head == pred) ? spinCount : 0, cargs);

            if (ws == StParkStatus.Success) {
                return true;
            }

            Unlink (wb, pred);
            StCancelArgs.ThrowIfException (ws);
            return false;
        }

        internal override bool _Release ()
        {
            do {
                StWaitBlock h, hn;
                if ((hn = (h = head).next) == UNLOCKED) {
                    return true;
                }

                if (hn == null) {
                    if (Interlocked.CompareExchange (ref head.next, UNLOCKED, null) == null) {
                        return false;
                    }
                    continue;
                }

                if (AdvanceHead (h, hn)) {
                    StParker pk;
                    if ((pk = hn.parker).TryLock ()) {
                        pk.Unpark (hn.waitKey);

                        //
                        // If this is a wait-any wait block, we are done;
                        // otherwise, keep trying to release another waiter.
                        //

                        if (hn.waitType == WaitType.WaitAny) {
                            return false;
                        }
                    }
                }
            }
            while (true);
        }

        internal override StWaitBlock _WaitAnyPrologue (StParker pk, int key,
                                                        ref StWaitBlock hint, ref int sc)
        {
            StWaitBlock wb = null;
            do {
                if (head.next == UNLOCKED) {
                    if (Interlocked.CompareExchange (ref head.next, null, UNLOCKED) == UNLOCKED) {
                        if (pk.TryLock ()) {
                            pk.UnparkSelf (key);
                        }
                        else {

                            //
                            // The parker is already lock, which means that the
                            // wait-any operation was already accomplished. So,
                            // release the lock, undoing the previous acquire.
                            //

                            _Release ();
                        }
                        return null;
                    }
                    continue;
                }

                if (wb == null) {
                    wb = new StWaitBlock (pk, WaitType.WaitAny, 0, key);
                }

                StWaitBlock t, tn;
                if ((tn = (t = tail).next) == UNLOCKED) {
                    continue;
                }
                if (tn != null) {
                    AdvanceTail (t, tn);
                    continue;
                }

                if (Interlocked.CompareExchange (ref t.next, wb, null) == null) {
                    AdvanceTail (t, wb);

                    //
                    // Return the inserted wait block, its predecessor and
                    // the sugested spin count.
                    //

                    sc = ((hint = t) == head) ? spinCount : 0;
                    return wb;
                }
            }
            while (true);
        }

        internal override StWaitBlock _WaitAllPrologue (StParker pk, ref StWaitBlock hint,
                                                        ref int sc)
        {
            StWaitBlock wb = null;
            do {
                //
                // If the lock can be immediately acquired, lock our parker
                // and if this is the last cooperative release, self unpark 
                // the current thread.
                //

                if (_AllowsAcquire) {
                    if (pk.TryLock ()) {
                        pk.UnparkSelf (StParkStatus.StateChange);
                    }
                    return null;
                }

                if (wb == null) {
                    wb = new StWaitBlock (pk, WaitType.WaitAll, 0, StParkStatus.StateChange);
                }

                StWaitBlock t, tn;
                if ((tn = (t = tail).next) == UNLOCKED) {
                    continue;
                }
                if (tn != null) {
                    AdvanceTail (t, tn);
                    continue;
                }
                if (Interlocked.CompareExchange (ref t.next, wb, null) == null) {
                    AdvanceTail (t, wb);

                    //
                    // Return the inserted wait block, its predecessor and
                    // the spin count for this wait block.
                    //

                    sc = ((hint = t) == head) ? spinCount : 0;
                    return wb;
                }
            }
            while (true);
        }

        internal override void _UndoAcquire ()
        {
            _Release ();
        }

        internal override void _CancelAcquire (StWaitBlock wb, StWaitBlock hint)
        {
            Unlink (wb, hint);
        }

        private void Unlink (StWaitBlock wb, StWaitBlock pred)
        {
            while (pred.next == wb) {
                //
                // Remove the cancelled wait blocks that are at the front
                // of the queue.
                //

                StWaitBlock h, hn;
                if (((hn = (h = head).next) != null && hn != UNLOCKED) &&
                    (hn.parker.IsLocked && hn.request > 0)) {
                    AdvanceHead (h, hn);
                    continue;
                }

                //
                // If the queue is empty, return.
                //

                StWaitBlock t, tn;
                if ((t = tail) == h) {
                    return;
                }

                //
                // Do the necessary consistency checks before trying to
                // unlink the wait block.
                //

                if (t != tail) {
                    continue;
                }

                if ((tn = t.next) != null) {
                    AdvanceTail (t, tn);
                    continue;
                }

                //
                // If the wait block is not at the tail of the queue, try
                // to unlink it.
                //

                if (wb != t) {
                    StWaitBlock wbn;
                    if ((wbn = wb.next) == wb || pred.CasNext (wb, wbn)) {
                        return;
                    }
                }

                //
                // The wait block is at the tail of the queue; so, take
                // into account the *toUnlink* wait block.
                //

                StWaitBlock dp;
                if ((dp = toUnlink) != null) {
                    StWaitBlock d, dn;
                    if ((d = dp.next) == dp || ((dn = d.next) != null && dp.CasNext (d, dn))) {
                        CasToUnlink (dp, null);
                    }
                    if (dp == pred) {
                        return; // *wb* is an already the saved node.
                    }
                }
                else if (CasToUnlink (null, pred)) {
                    return;
                }
            }
        }

        private bool AdvanceHead (StWaitBlock h, StWaitBlock nh)
        {
            if (h == head && Interlocked.CompareExchange (ref head, nh, h) == h) {
                h.next = h; // Mark the old head as unlinked.
                return true;
            }
            return false;
        }

        private void AdvanceTail (StWaitBlock t, StWaitBlock nt)
        {
            if (t == tail) {
                Interlocked.CompareExchange (ref tail, nt, t);
            }
        }

        private bool CasToUnlink (StWaitBlock tu, StWaitBlock ntu)
        {
            return toUnlink == tu && Interlocked.CompareExchange (ref toUnlink, ntu, tu) == tu;
        }
    }
}